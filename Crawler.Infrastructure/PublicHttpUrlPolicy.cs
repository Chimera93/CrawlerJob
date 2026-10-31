using System.Net;
using System.Net.Sockets;

namespace Crawler.Infrastructure;

public sealed class UnsafeCrawlUrlException(string message) : Exception(message);

public sealed class PublicHttpUrlPolicy : ICrawlUrlPolicy
{
    public async Task EnsurePublicHttpUrlAsync(Uri uri, CancellationToken ct)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https") ||
            string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
            throw new UnsafeCrawlUrlException("Only absolute HTTP(S) URLs without credentials are allowed.");

        var addresses = await ResolvePublicAddressesAsync(uri.Host, ct);
        if (addresses.Length == 0 || addresses.Any(address => !IsPublicAddress(address)))
            throw new UnsafeCrawlUrlException("The URL must resolve only to globally routable IP addresses.");
    }

    public async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var addresses = await ResolvePublicAddressesAsync(context.DnsEndPoint.Host, ct);
        Exception? lastError = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException exception)
            {
                lastError = exception;
                socket.Dispose();
            }
        }

        throw new HttpRequestException("Could not connect to any validated address for the crawl URL.", lastError);
    }

    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return !(bytes[0] == 0 ||
                bytes[0] == 10 ||
                bytes[0] == 127 ||
                bytes[0] >= 224 ||
                bytes[0] == 169 && bytes[1] == 254 ||
                bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
                bytes[0] == 192 && bytes[1] == 168 ||
                bytes[0] == 100 && bytes[1] is >= 64 and <= 127 ||
                bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 0 ||
                bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 2 ||
                bytes[0] == 192 && bytes[1] == 88 && bytes[2] == 99 ||
                bytes[0] == 198 && bytes[1] is 18 or 19 ||
                bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100 ||
                bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113 ||
                bytes[0] == 255);
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
            return false;

        var first = bytes[0];
        var second = bytes[1];
        var isGlobalUnicast = (first & 0xE0) == 0x20;
        var isDocumentation = HasPrefix(bytes, [0x20, 0x01, 0x0D, 0xB8], 32);
        var isSpecialPurpose = HasPrefix(bytes, [0x20, 0x01, 0x00, 0x00], 23) ||
            HasPrefix(bytes, [0x20, 0x01, 0x00, 0x10], 28) ||
            HasPrefix(bytes, [0x20, 0x01, 0x00, 0x20], 28);
        var isSixToFour = HasPrefix(bytes, [0x20, 0x02], 16);
        return isGlobalUnicast && !isDocumentation && !isSpecialPurpose && !isSixToFour;
    }

    private static bool HasPrefix(byte[] address, byte[] prefix, int bitCount)
    {
        var wholeBytes = bitCount / 8;
        var remainingBits = bitCount % 8;
        if (!address.AsSpan(0, wholeBytes).SequenceEqual(prefix.AsSpan(0, wholeBytes)))
            return false;
        if (remainingBits == 0)
            return true;
        var mask = (byte)(0xFF << (8 - remainingBits));
        return (address[wholeBytes] & mask) == (prefix[wholeBytes] & mask);
    }

    private static async Task<IPAddress[]> ResolvePublicAddressesAsync(string host, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var address))
        {
            if (!IsPublicAddress(address))
                throw new UnsafeCrawlUrlException("Private, reserved, and non-routable IP addresses are not allowed.");
            return [address];
        }

        var addresses = await Dns.GetHostAddressesAsync(host, ct);
        if (addresses.Length == 0 || addresses.Any(candidate => !IsPublicAddress(candidate)))
            throw new UnsafeCrawlUrlException("The URL must resolve only to globally routable IP addresses.");
        return addresses;
    }
}
