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
/// "the server is down".
/// </summary>
public enum RealmStatus { Unknown, Online, Offline }

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

    public async Task<RealmStatus> CheckAsync(string? realmlistHost, CancellationToken ct = default)
    {
        if (!TryParseHostPort(realmlistHost, out string host, out int port))
        {
            return RealmStatus.Unknown;
        }

        try
        {
            using var dnsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            dnsCts.CancelAfter(ConnectTimeout);
            await Dns.GetHostAddressesAsync(host, dnsCts.Token);
        }
        catch
        {
            // Doesn't resolve at all — a typo or a made-up domain, not a real server that's down.
            return RealmStatus.Unknown;
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
