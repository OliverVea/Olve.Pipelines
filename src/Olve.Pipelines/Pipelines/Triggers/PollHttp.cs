using System.Net;
using System.Net.Sockets;

namespace Olve.Pipelines.Pipelines.Triggers;

/// <summary>
/// The HTTP client poll triggers fetch with. The GET runs in the controller, which holds cluster and storage
/// credentials, so it connects only to public addresses (checked on the address actually connected to, so DNS
/// can't swap one in), never follows redirects (they would carry <c>$SECRET</c> headers elsewhere), uses no
/// proxy, and caps the response body.
/// </summary>
internal static class PollHttp
{
    internal const int MaxResponseBytes = 1024 * 1024;

    public static HttpClient Create(Func<IPAddress, bool>? allowed = null)
    {
        allowed ??= IsPublic;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectCallback = async (context, ct) =>
            {
                var host = context.DnsEndPoint.Host;
                var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct);
                var permitted = addresses.Where(allowed).ToArray();
                if (permitted.Length == 0)
                    throw new HttpRequestException($"'{host}' resolves only to addresses poll triggers may not reach (private, loopback, link-local)");

                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(permitted, context.DnsEndPoint.Port, ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = MaxResponseBytes };
    }

    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.GetAddressBytes() is [0x00, 0x64, 0xFF, 0x9B, 0, 0, 0, 0, 0, 0, 0, 0, var v0, var v1, var v2, var v3])
            address = new IPAddress([v0, v1, v2, v3]); // NAT64 well-known prefix
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            return false;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] == 10
                     || b[0] == 0
                     || (b[0] == 172 && b[1] is >= 16 and <= 31)
                     || (b[0] == 192 && b[1] == 168)
                     || (b[0] == 169 && b[1] == 254)
                     || (b[0] == 100 && b[1] is >= 64 and <= 127)
                     || b[0] >= 224);
        }

        return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast
                 || (address.GetAddressBytes()[0] & 0xFE) == 0xFC);
    }
}
