using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace TeronWoWLauncher.Services;

/// <summary>
/// Online = the auth port accepted a connection. Offline = the host is real (DNS resolves) but the
/// auth port refused/timed out — a genuinely down or misconfigured server. Unknown = no realmlist is
/// set, or the host doesn't even resolve — a typo'd or fake address, which isn't the same claim as
/// "the server is down". NetworkUnavailable = this machine itself has no usable network/DNS path
/// right now (Wi-Fi down, cable unplugged, DNS server unreachable) — deliberately distinct from
/// Unknown, since a DNS failure caused by the local machine being offline says nothing about whether
/// the realm address itself is real.
/// </summary>
public enum RealmStatus { Unknown, Online, Offline, NetworkUnavailable }

/// <summary>
/// Checks whether a realm's auth server is actually accepting connections, by attempting a raw TCP
/// connect to its auth port — 3724 by default, the vanilla WoW auth protocol's port since original
/// release and kept by virtually every private-server implementation unless the realmlist string
/// itself specifies a different one as "host:port".
///
/// This is deliberately not a ping: a host can answer ICMP echo while its login service is crashed
/// or not listening, and just as often blocks ICMP entirely while fully operational — ping proves
/// nothing about whether the actual auth service will accept the client's connection.
/// </summary>
public sealed class RealmStatusChecker
{
    private const int DefaultAuthPort = 3724;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// IANA-reserved specifically for documentation/testing (RFC 2606) — guaranteed to always exist
    /// and resolve, with no ties to any one company, making it a safe, neutral canary for "is DNS
    /// resolution actually working right now" that isn't the realm's own address.
    /// </summary>
    private const string CanaryHost = "example.com";

    public async Task<RealmStatus> CheckAsync(string? realmlistHost, CancellationToken ct = default)
    {
        if (!TryParseHostPort(realmlistHost, out string host, out int port))
        {
            return RealmStatus.Unknown;
        }

        if (!await ResolvesAsync(host, ct))
        {
            // The target didn't resolve, but that alone doesn't prove it's fake: the exact same
            // failure happens when this machine has no real network path right now, even when
            // there's an "up" network adapter reporting otherwise — confirmed against a WireGuard
            // tunnel adapter that Windows kept showing as Connected with Wi-Fi and Ethernet both
            // off, which made an earlier NetworkInterface.GetIsNetworkAvailable()-based check give a
            // false "network is fine" reading and go on to wrongly prune a perfectly valid realm.
            // Resolving a neutral, permanently-reserved canary host settles it either way without
            // trusting adapter state at all: if DNS can't resolve *that* either, DNS itself isn't
            // working right now, so the original failure says nothing about the realm being fake.
            bool dnsWorks = await ResolvesAsync(CanaryHost, ct);
            return dnsWorks ? RealmStatus.Unknown : RealmStatus.NetworkUnavailable;
        }

        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ConnectTimeout);
            await client.ConnectAsync(host, port, cts.Token);
            return RealmStatus.Online;
        }
        catch
        {
            // A real host that refused the connection or timed out — genuinely down/unreachable.
            return RealmStatus.Offline;
        }
    }

    private static async Task<bool> ResolvesAsync(string host, CancellationToken ct)
    {
        try
        {
            using var dnsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            dnsCts.CancelAfter(ConnectTimeout);
            await Dns.GetHostAddressesAsync(host, dnsCts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseHostPort(string? realmlistHost, out string host, out int port)
    {
        host = string.Empty;
        port = DefaultAuthPort;
        if (string.IsNullOrWhiteSpace(realmlistHost))
        {
            return false;
        }

        string value = realmlistHost.Trim();
        int colon = value.LastIndexOf(':');
        if (colon > 0 && int.TryParse(value[(colon + 1)..], out int parsedPort))
        {
            host = value[..colon];
            port = parsedPort;
        }
        else
        {
            host = value;
        }

        return host.Length > 0;
    }
}
