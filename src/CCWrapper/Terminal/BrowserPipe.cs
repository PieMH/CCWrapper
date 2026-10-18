using System.IO;
using System.IO.Pipes;
using System.Text;

namespace CCWrapper.Terminal;

/// <summary>The named pipe each tab's Browser MCP relay (<c>CCWrapper.StatusRelay.exe --browser &lt;name&gt;</c>) connects to.
/// Any number of connections at once, each a stream of newline-delimited JSON-RPC answered by the one shared
/// <see cref="BrowserMcp"/>. Only this Windows user can connect. Stops on Dispose.</summary>
public sealed class BrowserPipe : IDisposable
{
    const int MaxLine = 1 << 20; // a longer request drops its connection

    readonly CancellationTokenSource _stop = new();
    readonly BrowserMcp _handler;

    /// <summary>The pipe of the window process <paramref name="pid"/>: each account window has its own.</summary>
    public static string NameFor(int pid) => $"CCWrapper.browser.{pid}";

    /// <summary>Listens at once, so a client may connect as soon as this returns. Throws if another user's process owns the name.</summary>
    public BrowserPipe(string name, BrowserMcp handler)
    {
        _handler = handler;
        var first = Listen(name);
        _ = AcceptAsync(name, first);
    }

    static NamedPipeServerStream Listen(string name) =>
        new(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    async Task AcceptAsync(string name, NamedPipeServerStream next)
    {
        var ct = _stop.Token;
        while (true)
        {
            try { await next.WaitForConnectionAsync(ct); }
            catch (IOException) // the client left before we accepted it
            {
                await next.DisposeAsync();
                next = Listen(name);
                continue;
            }
            catch (Exception) // stopped
            {
                await next.DisposeAsync();
                return;
            }
            _ = ServeAsync(next, ct);
            try { next = Listen(name); }
            catch (Exception) { return; } // stopped or the system refused a new instance: no further clients
        }
    }

    async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        await using var _ = pipe;
        var line = new MemoryStream();
        var buffer = new byte[64 * 1024];
        try
        {
            int n;
            while ((n = await pipe.ReadAsync(buffer, ct)) > 0)
            {
                var start = 0;
                for (int nl; (nl = Array.IndexOf(buffer, (byte)'\n', start, n - start)) >= 0; start = nl + 1)
                {
                    line.Write(buffer, start, nl - start);
                    if (line.Length > MaxLine) return;
                    var reply = await _handler.HandleAsync(Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length), ct);
                    line.SetLength(0);
                    if (reply is not null) await pipe.WriteAsync(Encoding.UTF8.GetBytes(reply + "\n"), ct);
                }
                line.Write(buffer, start, n - start);
                if (line.Length > MaxLine) return;
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { } // client gone or stopped
    }

    public void Dispose() => _stop.Cancel();
}
