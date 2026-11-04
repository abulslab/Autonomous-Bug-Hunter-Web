using System.Net;
using System.Net.Sockets;

namespace BugHunter.Api.Common;

// Blocks server-side requests to internal networks (SSRF) for user-supplied targets and endpoints.
public static class UrlGuard
{
    public static async Task<string?> CheckAsync(string url, bool allowPrivate, bool allowHttp = false)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return "Endpoint must be an absolute URL.";
        if (u.Scheme != Uri.UriSchemeHttps && !(allowHttp && u.Scheme == Uri.UriSchemeHttp)) return "Endpoint must use https.";
        if (!string.IsNullOrEmpty(u.UserInfo)) return "Credentials in URLs are not allowed.";
        if (allowPrivate) return null;
        IPAddress[] addrs;
        try { addrs = IPAddress.TryParse(u.Host, out var ip) ? [ip] : await Dns.GetHostAddressesAsync(u.Host); }
        catch (SocketException) { return "Endpoint host cannot be resolved."; }
        return addrs.Any(IsPrivate) ? "Endpoint resolves to a private or reserved address." : null;
    }

    public static bool IsPrivate(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return true;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal || ip.IsIPv6Multicast;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || b[0] == 127 || b[0] == 0 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] is >= 64 and <= 127) || b[0] >= 224;
    }

    // Ensures a local file or directory path resolves inside the allowed root directory (prevents arbitrary file read / traversal).
    public static string? CheckLocalPath(string? path, string? allowedRoot)
    {
        if (string.IsNullOrWhiteSpace(path)) return "A local file path is required.";
        if (string.IsNullOrWhiteSpace(allowedRoot)) return "Local file sources are not enabled.";
        try
        {
            var root = Path.GetFullPath(allowedRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                ? null : "Local file path must be inside the ground-truth directory.";
        }
        catch { return "Invalid local file path."; }
    }
}
