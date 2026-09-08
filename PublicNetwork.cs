using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace WinCalendar;

// 不包含原始链接，异常可以安全地转为本地化状态。
public sealed class SubscriptionBlockedException : IOException { }
public static class PublicNetwork
{
    // 保守拒绝 IANA 特殊用途段；IPv6 仅接受普通全局单播地址。
    private static readonly IPNetwork[] Blocked = new[] {
        "0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8", "169.254.0.0/16", "172.16.0.0/12",
        "192.0.0.0/24", "192.0.2.0/24", "192.31.196.0/24", "192.52.193.0/24", "192.88.99.0/24",
        "192.168.0.0/16", "192.175.48.0/24", "198.18.0.0/15", "198.51.100.0/24", "203.0.113.0/24",
        "224.0.0.0/4", "240.0.0.0/4", "2001::/23", "2001:db8::/32", "2002::/16", "2620:4f:8000::/48", "3fff::/20"
    }.Select(IPNetwork.Parse).ToArray();
    private static readonly IPNetwork GlobalV6 = IPNetwork.Parse("2000::/3");
    private static IPAddress Normalize(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
    public static bool IsPublic(IPAddress address, IPAddress[] local)
    {
        var ip = Normalize(address);
        return !local.Any(a => Normalize(a).Equals(ip)) && !IPAddress.IsLoopback(ip) &&
            (ip.AddressFamily == AddressFamily.InterNetwork || ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.ScopeId == 0 && GlobalV6.Contains(ip)) &&
            !Blocked.Any(n => n.Contains(ip));
    }
    private static IPAddress[] LocalAddresses() => NetworkInterface.GetAllNetworkInterfaces()
        .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address).ToArray();
    public static void ValidateHost(string host)
    {
        host = host.Trim('[', ']').TrimEnd('.');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            IPAddress.TryParse(host, out var ip) && !IsPublic(ip, LocalAddresses())) throw new SubscriptionBlockedException();
    }
    public static bool WasBlocked(Exception error)
    {
        for (Exception? e = error; e != null; e = e.InnerException)
            if (e is SubscriptionBlockedException) return true;
        return false;
    }
    // DNS 只解析一次；检查整个结果集后，仅通过 IP 端点连接，禁止域名回退。
    public static async ValueTask<Stream> Connect(string host, int port, CancellationToken ct,
        Func<string, CancellationToken, Task<IPAddress[]>> resolve,
        Func<IPEndPoint, CancellationToken, ValueTask<Stream>> dial, Func<IPAddress[]> localAddresses)
    {
        ct.ThrowIfCancellationRequested(); ValidateHost(host);
        var addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal) ? new[] { literal } : await resolve(host, ct).ConfigureAwait(false);
        var local = localAddresses();
        if (addresses.Length == 0 || addresses.Any(ip => !IsPublic(ip, local))) throw new SubscriptionBlockedException();
        foreach (var address in addresses.Distinct())
        {
            ct.ThrowIfCancellationRequested();
            // 尝试下一个地址前重新检查本机网卡，避免接口变化造成自连接。
            if (!IsPublic(address, localAddresses())) throw new SubscriptionBlockedException();
            try { return await dial(new IPEndPoint(Normalize(address), port), ct).ConfigureAwait(false); }
            catch (SocketException) { }
        }
        throw new HttpRequestException("Subscription connection failed.");
    }
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false, UseProxy = false, UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        // 返回原始 TCP 流，TLS/SNI/证书验证由 HttpClient 按原始 URI 完成。
        ConnectCallback = (context, ct) => Connect(context.DnsEndPoint.Host, context.DnsEndPoint.Port, ct,
            (host, token) => Dns.GetHostAddressesAsync(host, token), Dial, LocalAddresses)
    };
    private static async ValueTask<Stream> Dial(IPEndPoint endpoint, CancellationToken ct)
    {
        var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try { await socket.ConnectAsync(endpoint, ct).ConfigureAwait(false); return new NetworkStream(socket, ownsSocket: true); }
        catch { socket.Dispose(); throw; }
    }
}
