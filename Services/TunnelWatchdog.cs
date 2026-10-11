using System.Net.NetworkInformation;

namespace SSHTunnelManager.Services;

/// <summary>
/// 隧道看门狗：
/// 1) 网络地址/可用性变化时，稍候重启全部在跑隧道（换 Wi-Fi 自愈）；
/// 2) 定时检查全部 LocalForward 监听端口；SSH 链路存活由 config 的 ServerAlive 参数判定。
/// RemoteForward 的监听在远端，不能用本机端口判断。
/// </summary>
public sealed class TunnelWatchdog : IDisposable
{
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan NetworkDebounce = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RestartCooldown = TimeSpan.FromSeconds(30);

    private readonly TunnelManager _tunnels;
    private readonly Func<string> _configPath;
    private readonly System.Threading.Timer _probeTimer;
    private readonly object _lock = new();
    private readonly Dictionary<string, int> _failCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _lastRestart = new(StringComparer.OrdinalIgnoreCase);
    private System.Threading.Timer? _networkDebounce;
    private volatile bool _disposed;
    private int _probing;

    public TunnelWatchdog(TunnelManager tunnels, Func<string> configPath)
    {
        _tunnels = tunnels;
        _configPath = configPath;
        _probeTimer = new System.Threading.Timer(_ => ProbeAll(), null, ProbeInterval, ProbeInterval);
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
    }

    private void OnNetworkChanged(object? sender, EventArgs e) => DebounceNetworkRestart();

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (e.IsAvailable) DebounceNetworkRestart();
    }

    private void DebounceNetworkRestart()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _networkDebounce?.Dispose();
            _networkDebounce = new System.Threading.Timer(_ => RestartAllSafe(), null, NetworkDebounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void RestartAllSafe()
    {
        if (_disposed) return;
        try
        {
            lock (_lock)
            {
                foreach (var alias in _tunnels.RunningAliases)
                    _lastRestart[alias] = DateTime.Now;
                _failCounts.Clear();
            }
            _tunnels.RestartRunning();
        }
        catch { }
    }

    private void ProbeAll()
    {
        if (_disposed || Interlocked.Exchange(ref _probing, 1) != 0) return;
        try
        {
            var running = _tunnels.RunningAliases;
            if (running.Count == 0) return;

            Dictionary<string, List<int>> portsByAlias;
            try
            {
                portsByAlias = SshConfigParser.Parse(_configPath())
                    .Where(h => h.IsTunnel)
                    .GroupBy(h => h.Alias, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.SelectMany(host => host.EnabledListenPorts)
                        .Distinct().ToList(), StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return;
            }

            var listeningPorts = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
                .Select(endpoint => endpoint.Port).ToHashSet();
            foreach (var alias in running)
            {
                if (!portsByAlias.TryGetValue(alias, out var ports) || ports.Count == 0)
                    continue;

                var allOpen = ports.All(listeningPorts.Contains);

                int fails;
                lock (_lock)
                {
                    if (allOpen)
                    {
                        _failCounts[alias] = 0;
                        continue;
                    }
                    fails = _failCounts.TryGetValue(alias, out var n) ? n + 1 : 1;
                    _failCounts[alias] = fails;

                    if (fails < 2) continue;
                    if (_lastRestart.TryGetValue(alias, out var last)
                        && DateTime.Now - last < RestartCooldown)
                        continue;
                    _lastRestart[alias] = DateTime.Now;
                    _failCounts[alias] = 0;
                }

                // 连续 2 次探活失败：强制重连
                try { _tunnels.Restart(alias); } catch { }
            }
        }
        catch { }
        finally
        {
            Volatile.Write(ref _probing, 0);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _networkDebounce?.Dispose();
        }
        try { NetworkChange.NetworkAddressChanged -= OnNetworkChanged; } catch { }
        try { NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged; } catch { }
        _probeTimer.Dispose();
    }
}
