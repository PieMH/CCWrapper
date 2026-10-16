using System.IO;
using System.Runtime.InteropServices;
using CCWrapper.Terminal;

namespace CCWrapper.Tests.Terminal;

public sealed class PtySessionTests
{
    [Theory]
    [InlineData("plain")]
    [InlineData("my task")]
    [InlineData("say \"hi\"")]
    [InlineData(@"C:\Users\John Doe\relay-settings.json")]
    [InlineData("{\"statusLine\":{\"type\":\"command\",\"command\":\"C:/Apps/CCW/CCWrapper.StatusRelay.exe\"}}")] // the inline --settings
    [InlineData(@"ends in backslash\")]
    [InlineData(@"two\\")]
    [InlineData(@"a\""b")]
    [InlineData("")]
    public void QuoteArg_round_trips_through_CommandLineToArgvW(string arg)
    {
        var argv = CommandLineToArgvW("app.exe " + PtySession.QuoteArg(arg), out var count);
        try
        {
            Assert.Equal(2, count);
            Assert.Equal(arg, Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, IntPtr.Size)));
        }
        finally { LocalFree(argv); }
    }

    [Fact]
    public async Task Output_pauses_until_acked_then_runs_to_exit()
    {
        long received = 0;
        var acking = false;
        PtySession? pty = null;
        var exited = new TaskCompletionSource<int>();
        var env = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string)e.Value!);

        pty = await PtySession.StartAsync(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            ["/c", "for", "/L", "%i", "in", "(1,1,40000)", "do", "@echo", "line", "%i", "of", "the", "flow", "control", "test"],
            Environment.SystemDirectory, env, 120, 30,
            text => { Interlocked.Add(ref received, text.Length); if (Volatile.Read(ref acking)) pty?.Ack(text.Length); },
            code => exited.TrySetResult(code));
        using var _ = pty;

        await Task.Delay(2000);
        var paused = Interlocked.Read(ref received);
        Assert.InRange(paused, 100_001, 100_000 + 16_385); // stops on the first read past the high mark
        await Task.Delay(500);
        Assert.Equal(paused, Interlocked.Read(ref received));

        Volatile.Write(ref acking, true);
        pty.Ack((int)paused);
        Assert.Same(exited.Task, await Task.WhenAny(exited.Task, Task.Delay(TimeSpan.FromSeconds(30))));
        Assert.Equal(0, await exited.Task);
        Assert.True(Interlocked.Read(ref received) > paused);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CommandLineToArgvW(string cmdLine, out int numArgs);

    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr mem);
}
