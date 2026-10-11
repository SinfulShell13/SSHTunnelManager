using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using SSHTunnelManager.Services;
using Xunit;

namespace SSHTunnelManager.Tests;

// 本地 TCP 端点让 ssh 停在握手阶段，用真实子进程验证生命周期；不访问真实 SSH 配置或服务器。
public sealed class TunnelManagerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "SSHTunnelManagerTests-" + Guid.NewGuid().ToString("N"));
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly List<TcpClient> _clients = new();
    private readonly SettingsService _settings;
    private readonly TunnelManager _manager;
    private readonly Task _accepting;
    private int _connections;

    public TunnelManagerTests()
    {
        Directory.CreateDirectory(_directory);
        _listener.Start();
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        var configPath = Path.Combine(_directory, "config");
        File.WriteAllText(configPath, $"""
            Host test proxy
              HostName 127.0.0.1
              Port {port}
              User test
            Host *
              ControlMaster auto
              ControlPath nonexistent-control-socket
              ControlPersist 10m
            """);
        _settings = new SettingsService(Path.Combine(_directory, "settings")) { ConfigPath = configPath };
        _manager = new TunnelManager(_settings);
        _accepting = AcceptAsync();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                var client = await _listener.AcceptTcpClientAsync();
                lock (_clients) _clients.Add(client);
                Interlocked.Increment(ref _connections);
            }
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
    }

    [Fact]
    public async Task Restart_WaitsForOldProcessAndStartsReplacement()
    {
        Assert.True(_manager.Start("test"));
        await WaitUntilAsync(() => Volatile.Read(ref _connections) == 1);
        var oldPid = Assert.Single(_manager.TrackedPids);
        using var oldProcess = Process.GetProcessById(oldPid);

        Assert.True(_manager.Restart("test"));
        await WaitUntilAsync(() => Volatile.Read(ref _connections) == 2);

        Assert.True(oldProcess.HasExited);
        Assert.NotEqual(oldPid, Assert.Single(_manager.TrackedPids));
        Assert.Null(_manager.GetReconnectStatus("test"));
    }

    [Fact]
    public async Task ConcurrentStart_CreatesOnlyOneProcess()
    {
        var starts = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => _manager.Start("test"))));
        await WaitUntilAsync(() => Volatile.Read(ref _connections) > 0);

        Assert.Single(starts, started => started);
        Assert.Single(_manager.TrackedPids);
        Assert.Equal(1, Volatile.Read(ref _connections));
    }

    [Fact]
    public async Task Stop_CancelsPendingReconnectAndRejectsStaleWatchdogRestart()
    {
        Assert.True(_manager.Start("test"));
        await WaitUntilAsync(() => Volatile.Read(ref _connections) == 1);
        using (var process = Process.GetProcessById(Assert.Single(_manager.TrackedPids))) process.Kill(true);
        await WaitUntilAsync(() => _manager.GetReconnectStatus("test") != null);

        Assert.True(_manager.Stop("test"));
        Assert.False(_manager.Restart("test"));
        await Task.Delay(5500);

        Assert.Empty(_manager.TrackedPids);
        Assert.False(_manager.IsRequested("test"));
        Assert.Null(_manager.GetReconnectStatus("test"));
        Assert.Equal(1, Volatile.Read(ref _connections));
    }

    [Fact]
    public async Task UnexpectedExit_ReconnectsAutomatically()
    {
        Assert.True(_manager.Start("test"));
        await WaitUntilAsync(() => Volatile.Read(ref _connections) == 1);
        var oldPid = Assert.Single(_manager.TrackedPids);
        using (var process = Process.GetProcessById(oldPid)) process.Kill(true);

        await WaitUntilAsync(() => Volatile.Read(ref _connections) == 2);

        Assert.NotEqual(oldPid, Assert.Single(_manager.TrackedPids));
        Assert.True(_manager.IsRequested("test"));
    }

    [Fact]
    public void StopBeforeDelayedAutoStart_IsRespected()
    {
        _manager.Stop("test");
        Assert.False(_manager.Start("test", autoStart: true));
        Assert.Empty(_manager.TrackedPids);
        Assert.True(_manager.Start("test"));
    }

    [Fact]
    public async Task ProxyCrash_ReconnectsWithoutRestartingLoginTunnel()
    {
        Assert.True(_manager.Start("test"));
        var jumpPid = Assert.Single(_manager.TrackedPids);
        Assert.True(_manager.Start("proxy"));
        await WaitUntilAsync(() => Volatile.Read(ref _connections) == 2);
        var proxyPid = Assert.Single(_manager.TrackedPids, pid => pid != jumpPid);
        using (var process = Process.GetProcessById(proxyPid)) process.Kill(true);

        await WaitUntilAsync(() => Volatile.Read(ref _connections) == 3);

        Assert.Contains(jumpPid, _manager.TrackedPids);
        Assert.DoesNotContain(proxyPid, _manager.TrackedPids);
        Assert.Equal(2, _manager.TrackedPids.Count);
    }

    [Fact]
    public async Task InitialLaunchFailure_RetriesAfterExecutableBecomesAvailable()
    {
        var sshPath = _settings.SshExePath;
        _settings.SshExePath = Path.Combine(_directory, "missing-ssh.exe");
        Assert.False(_manager.Start("test"));
        Assert.NotNull(_manager.GetLastError("test"));
        Assert.NotNull(_manager.GetReconnectStatus("test"));

        _settings.SshExePath = sshPath;
        await WaitUntilAsync(() => Volatile.Read(ref _connections) == 1);

        Assert.Single(_manager.TrackedPids);
        Assert.Null(_manager.GetLastError("test"));
    }

    [Fact]
    public async Task ConfigReload_OnlyRestartsChangedHostAndPreservesManualStop()
    {
        var config = new Dictionary<string, string> { ["test"] = "jump-v1", ["proxy"] = "proxy-v1" };
        _manager.SynchronizeConfig(config, startNew: false);
        Assert.True(_manager.Start("test"));
        var jumpPid = Assert.Single(_manager.TrackedPids);
        Assert.True(_manager.Start("proxy"));
        await WaitUntilAsync(() => Volatile.Read(ref _connections) == 2);
        var proxyPid = Assert.Single(_manager.TrackedPids, pid => pid != jumpPid);

        config["proxy"] = "proxy-v2";
        _manager.SynchronizeConfig(config, startNew: true);
        await WaitUntilAsync(() => Volatile.Read(ref _connections) == 3);
        Assert.Contains(jumpPid, _manager.TrackedPids);
        Assert.DoesNotContain(proxyPid, _manager.TrackedPids);

        Assert.True(_manager.Stop("proxy"));
        config["proxy"] = "proxy-v3";
        _manager.SynchronizeConfig(config, startNew: true);
        Assert.Equal(jumpPid, Assert.Single(_manager.TrackedPids));

        config.Remove("test");
        _manager.SynchronizeConfig(config, startNew: true);
        Assert.Empty(_manager.TrackedPids);
        Assert.Empty(_manager.RequestedAliases);

        config["test"] = "jump-v2";
        _manager.SynchronizeConfig(config, startNew: true);
        await WaitUntilAsync(() => Volatile.Read(ref _connections) == 4);
        Assert.Single(_manager.TrackedPids);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(timeout.Elapsed < TimeSpan.FromSeconds(9), "等待本地 ssh 子进程状态超时。");
            await Task.Delay(25);
        }
    }

    public void Dispose()
    {
        _manager.Dispose();
        _listener.Stop();
        _accepting.GetAwaiter().GetResult();
        lock (_clients) foreach (var client in _clients) client.Dispose();
        Directory.Delete(_directory, recursive: true);
    }
}
