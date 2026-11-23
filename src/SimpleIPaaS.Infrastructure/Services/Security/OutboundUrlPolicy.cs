using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace SimpleIPaaS.Infrastructure.Services.Security;

public class OutboundOptions
{
    // Internal integrations are common in a private deployment. Public deployments should keep
    // this false and explicitly allow only the hosts they need.
    public bool AllowPrivateNetworks { get; set; }
    public string[] AllowedHosts { get; set; } = Array.Empty<string>();

    // Largest response body an HTTP card will buffer. A partner endpoint that suddenly returns hundreds of
    // megabytes used to be read completely into memory (twice); now the call fails with a clear transport
    // error instead of exhausting the Engine's RAM. "Outbound:MaxResponseBytes", default 64 MB.
    public long MaxResponseBytes { get; set; } = 64L * 1024 * 1024;
}

public class OutboundRequestBlockedException : Exception
{
    public OutboundRequestBlockedException(string message) : base(message)
    {
    }
}

public class OutboundUrlPolicy
{
    private readonly OutboundOptions _options;

    public OutboundUrlPolicy(IOptions<OutboundOptions> options)
    {
        _options = options.Value;
    }

    public async Task<Uri> ValidateAsync(string? url, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new OutboundRequestBlockedException("Only absolute http(s) URLs are allowed.");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new OutboundRequestBlockedException("URLs with embedded credentials are not allowed.");
        }

        if (_options.AllowedHosts.Length > 0 && !IsAllowedHost(uri.Host))
        {
            throw new OutboundRequestBlockedException($"The host '{uri.Host}' is not in the outbound allow-list.");
        }

        if (_options.AllowPrivateNetworks)
        {
            return uri;
        }

        var addresses = IPAddress.TryParse(uri.DnsSafeHost, out var literal)
            ? new[] { literal }
            : await Dns.GetHostAddressesAsync(uri.DnsSafeHost, cancellationToken);

        if (addresses.Length == 0 || addresses.Any(IsBlocked))
        {
            throw new OutboundRequestBlockedException(
                $"The host '{uri.Host}' resolves to a private, loopback, link-local, or otherwise blocked address. " +
                "Set Outbound:AllowPrivateNetworks=true only for trusted internal deployments.");
        }

        return uri;
    }

    public static bool IsBlocked(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            return IsBlocked(address.MapToIPv4());
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes6 = address.GetAddressBytes();
            if (address.Equals(IPAddress.IPv6Any) ||
                address.Equals(IPAddress.IPv6Loopback) ||
                address.IsIPv6LinkLocal ||
                address.IsIPv6Multicast)
            {
                return true;
            }

            // fc00::/7 unique-local and fec0::/10 site-local.
            if ((bytes6[0] & 0xFE) == 0xFC)
            {
                return true;
            }

            if (bytes6[0] == 0xFE && (bytes6[1] & 0xC0) == 0xC0)
            {
                return true;
            }

            return false;
        }

        var bytes = address.GetAddressBytes();
        if (bytes.Length != 4)
        {
            return true;
        }

        return bytes[0] == 0 ||
               bytes[0] == 10 ||
               bytes[0] == 127 ||
               (bytes[0] == 169 && bytes[1] == 254) ||
               (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168) ||
               (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) ||
               (bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19)) ||
               bytes[0] >= 224;
    }

    private bool IsAllowedHost(string host)
    {
        foreach (var pattern in _options.AllowedHosts)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                continue;
            }

            if (pattern == "*" || string.Equals(pattern, host, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (pattern.StartsWith("*.", StringComparison.Ordinal) &&
                host.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
