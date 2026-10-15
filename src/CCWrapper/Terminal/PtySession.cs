using System.Collections;
using System.IO;
using System.Text;
using Porta.Pty;

namespace CCWrapper.Terminal;

/// <summary>One child process in a ConPTY (Porta.Pty; its Job Object kills the tree when this process dies).
/// Output is decoded with a stateful UTF-8 decoder on a background thread and pauses while more than
/// <see cref="High"/> chars wait for <see cref="Ack"/>.</summary>
public sealed class PtySession : IDisposable
{
    const int High = 100_000, Low = 5_000;

    readonly IPtyConnection _pty;
    readonly ManualResetEventSlim _flowing = new(true);
    readonly Lock _writeLock = new();
    int _unacked, _exitCode = -1, _exitSeen;

    PtySession(IPtyConnection pty) => _pty = pty;

    /// <param name="env">The complete environment block for the child.</param>
    /// <param name="output">Decoded output, called on the reader thread.</param>
    /// <param name="exited">Called once on the reader thread after the last output, with the exit code (-1 if unknown).</param>
    public static async Task<PtySession> StartAsync(string app, IReadOnlyList<string> args, string cwd,
        IDictionary<string, string> env, int cols, int rows, Action<string> output, Action<int> exited)
    {
        // Porta.Pty merges Environment into this process's env and removes keys whose value is empty.
        var block = new Dictionary<string, string>(env, StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables()) block.TryAdd((string)e.Key, "");

        var pty = await PtyProvider.SpawnAsync(new PtyOptions
        {
            App = app, CommandLine = [.. args.Select(QuoteArg)], VerbatimCommandLine = true,
            Cwd = cwd, Environment = block, Cols = cols, Rows = rows,
        }, CancellationToken.None);
        var session = new PtySession(pty);
        pty.ProcessExited += (_, e) => session.OnProcessExited(e.ExitCode);
        if (pty.WaitForExit(0)) session.OnProcessExited(pty.ExitCode); // exited before we subscribed
        new Thread(() => session.ReadLoop(output, exited)) { IsBackground = true, Name = "pty " + pty.Pid }.Start();
        return session;
    }

    /// <summary>Windows (CommandLineToArgvW) quoting. Porta.Pty's own quotes every argument and breaks a trailing backslash.</summary>
    internal static string QuoteArg(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"']) < 0) return arg;
        var sb = new StringBuilder("\"");
        for (var i = 0; i <= arg.Length; i++)
        {
            var slashes = 0;
            while (i < arg.Length && arg[i] == '\\') { slashes++; i++; }
            if (i == arg.Length) { sb.Append('\\', slashes * 2); break; } // before the closing quote
            if (arg[i] == '"') sb.Append('\\', slashes * 2 + 1).Append('"');
            else sb.Append('\\', slashes).Append(arg[i]);
        }
        return sb.Append('"').ToString();
    }

    // ConPTY keeps the output pipe open after the child exits; closing it flushes the last output and ends ReadLoop.
    void OnProcessExited(int code)
    {
        if (Interlocked.Exchange(ref _exitSeen, 1) == 1) return;
        _exitCode = code;
        Task.Run(Close);
    }

    void ReadLoop(Action<string> output, Action<int> exited)
    {
        var decoder = Encoding.UTF8.GetDecoder();
        var bytes = new byte[16 * 1024];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        try
        {
            int n;
            while ((n = _pty.ReaderStream.Read(bytes, 0, bytes.Length)) > 0)
            {
                var count = decoder.GetChars(bytes, 0, n, chars, 0);
                if (count == 0) continue;
                output(new string(chars, 0, count));
                if (Interlocked.Add(ref _unacked, count) <= High) continue;
                _flowing.Reset();
                if (Volatile.Read(ref _unacked) > High) _flowing.Wait(); // re-check: an Ack may have landed before Reset
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { } // pipe closed
        exited(_exitCode);
    }

    /// <summary>The terminal has processed <paramref name="chars"/> more chars of output.</summary>
    public void Ack(int chars)
    {
        if (Interlocked.Add(ref _unacked, -chars) < Low) _flowing.Set();
    }

    public void Write(string text) => Write(Encoding.UTF8.GetBytes(text));

    public void Write(byte[] bytes)
    {
        lock (_writeLock)
        {
            try
            {
                _pty.WriterStream.Write(bytes);
                _pty.WriterStream.Flush();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { } // child gone
        }
    }

    public void Resize(int cols, int rows)
    {
        try { _pty.Resize(cols, rows); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    /// <summary>Kills the child (and its tree) and closes the console; the exited callback still fires.</summary>
    public void Dispose()
    {
        try { _pty.Kill(); }
        catch (Exception) { } // already exited
        Task.Run(Close);
    }

    void Close()
    {
        _flowing.Set();
        try { _pty.Dispose(); }
        catch (Exception) { } // second close
    }
}
