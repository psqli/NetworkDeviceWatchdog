using System.Net;
using System.Net.Sockets;

namespace NetworkDeviceWatchdog.Tests;

/// <summary>How a fake ESC/POS printer behaves.</summary>
public enum FakePrinterMode
{
    Ready,
    PaperLow,
    CoverOpen,
    PaperOut,
    CutterError,
    Silent,
    Garbage,
    AutoStatusOnConnect,
}

/// <summary>A TCP server on 127.0.0.1 that answers DLE EOT like an ESC/POS printer.</summary>
internal sealed class FakeEscPosPrinter : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public FakeEscPosPrinter(FakePrinterMode mode)
    {
        Mode = mode;
        _listener.Start();
        _loop = AcceptLoopAsync();
    }

    public FakePrinterMode Mode { get; }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public int Connections { get; private set; }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                Connections++;
                _ = ServeAsync(client);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            var buffer = new byte[3];
            try
            {
                if (Mode == FakePrinterMode.AutoStatusOnConnect)
                    await stream.WriteAsync(new byte[] { 0x10, 0x00, 0x00, 0x0F }, _stop.Token);   // ASB packet
                while (true)
                {
                    var read = 0;
                    while (read < 3)
                    {
                        var n = await stream.ReadAsync(buffer.AsMemory(read), _stop.Token);
                        if (n == 0)
                            return;
                        read += n;
                    }
                    if (Mode == FakePrinterMode.Silent)
                        continue;
                    var reply = Mode == FakePrinterMode.Garbage ? (byte)0xFF : Status(buffer[2]);
                    await stream.WriteAsync(new[] { reply }, _stop.Token);
                }
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or SocketException)
            {
            }
        }
    }

    private byte Status(byte n)
    {
        const byte Base = 0x12;
        return (Mode, n) switch
        {
            (FakePrinterMode.PaperLow, 4) => Base | 0x0C,
            (FakePrinterMode.CoverOpen, 1) => Base | 0x04 | 0x08,
            (FakePrinterMode.CoverOpen, 2) => Base | 0x04,
            (FakePrinterMode.PaperOut, 1) => Base | 0x04 | 0x08,
            (FakePrinterMode.PaperOut, 2) => Base | 0x20,
            (FakePrinterMode.PaperOut, 4) => Base | 0x60,
            (FakePrinterMode.CutterError, 1) => Base | 0x04 | 0x08,
            (FakePrinterMode.CutterError, 2) => Base | 0x40,
            (FakePrinterMode.CutterError, 3) => Base | 0x08,
            (_, 1) => Base | 0x04,
            _ => Base,
        };
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        await _loop;
        _stop.Dispose();
    }
}

/// <summary>An <see cref="HttpMessageHandler"/> that answers from a function, with no sockets.</summary>
internal sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    public List<byte[]> Bodies { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        Bodies.Add(request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken));
        // Like a real handler, give up when HttpClient's timeout cancels the request.
        return await respond(request).WaitAsync(cancellationToken);
    }
}

/// <summary>Returns a free TCP port on loopback that nothing listens on.</summary>
internal static class Ports
{
    public static int Closed()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
