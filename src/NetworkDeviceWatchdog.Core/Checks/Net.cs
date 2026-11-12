using System.Net;
using System.Net.Sockets;
using NetworkDeviceWatchdog.Localization;

namespace NetworkDeviceWatchdog.Checks;

/// <summary>Socket helpers that turn network errors into localizable details.</summary>
internal static class Net
{
    /// <summary>
    /// Opens a TCP connection with a timeout. Returns the connected socket, or null
    /// plus a localizable reason.
    /// </summary>
    public static async Task<(Socket? Socket, Msg? Error)> ConnectAsync(
        string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            await socket.ConnectAsync(new DnsEndPoint(host, port), cts.Token).ConfigureAwait(false);
            return (socket, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            socket.Dispose();
            return (null, new Msg("detail.timeout"));
        }
        catch (SocketException e)
        {
            socket.Dispose();
            return (null, Describe(e));
        }
    }

    /// <summary>Reads exactly one byte or returns -1 on timeout / closed connection.</summary>
    public static async Task<int> ReadByteAsync(Socket socket, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            var n = await socket.ReceiveAsync(buffer, SocketFlags.None, cts.Token).ConfigureAwait(false);
            return n == 1 ? buffer[0] : -1;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return -1;
        }
        catch (SocketException)
        {
            return -1;
        }
    }

    /// <summary>Discards any bytes the device sent on its own (e.g. automatic status packets).</summary>
    public static async Task DrainAsync(Socket socket, TimeSpan settle, CancellationToken cancellationToken)
    {
        await Task.Delay(settle, cancellationToken).ConfigureAwait(false);
        var buffer = new byte[256];
        while (socket.Available > 0)
        {
            var n = await socket.ReceiveAsync(buffer, SocketFlags.None, cancellationToken).ConfigureAwait(false);
            if (n == 0)
                break;
        }
    }

    public static Msg Describe(SocketException e) => e.SocketErrorCode switch
    {
        SocketError.ConnectionRefused => new Msg("detail.refused"),
        SocketError.TimedOut => new Msg("detail.timeout"),
        SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain => new Msg("detail.hostNotFound"),
        SocketError.NetworkUnreachable or SocketError.HostUnreachable or SocketError.HostDown => new Msg("detail.unreachable"),
        SocketError.ConnectionReset or SocketError.ConnectionAborted => new Msg("detail.reset"),
        _ => Msg.Of("detail.socketError", ("code", e.SocketErrorCode.ToString())),
    };

    /// <summary>Finds the innermost <see cref="SocketException"/>, if any.</summary>
    public static SocketException? FindSocketException(Exception? e)
    {
        for (; e is not null; e = e.InnerException)
        {
            if (e is SocketException se)
                return se;
        }
        return null;
    }

    /// <summary>Resolves a host to IP addresses (IPv4 first). Empty on failure.</summary>
    public static async Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var ip))
            return [ip];
        try
        {
            var all = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
            return [.. all.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)];
        }
        catch (SocketException)
        {
            return [];
        }
    }
}
