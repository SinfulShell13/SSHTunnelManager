using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace SSHTunnelManager.Services;

public sealed class TunnelSession
{
    public required string Alias { get; init; }
    public required Process Process { get; init; }
    public DateTime StartTime { get; init; }
    public bool StopRequested { get; set; }
    public int ReconnectAttempt { get; set; }
    public string? LastError { get; set; }
    public Task OutputDrain { get; set; } = Task.CompletedTask;
    public Task ErrorDrain { get; set; } = Task.CompletedTask;
    public TaskCompletionSource<bool> Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>后台启动和管理 ssh -N，并为异常退出提供可取消的渐进重连。</summary>
public sealed class TunnelManager : IDisposable
{
    private static readonly ConcurrentDictionary<string, object> LogLocks = new();
    private readonly SettingsService _settings;
    private readonly Dictionary<string, TunnelSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _lastErrors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CancellationTokenSource> _pendingReconnects = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (int Attempt, DateTime RetryAt)> _pendingReconnectInfo = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _requestedAliases = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _manualStops = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly object _lock = new();
    // 启停操作串行化；退出回调只持有 _lock，停止可以等待清理完成。
    private readonly object _operations = new();
    private Dictionary<string, string> _configurations = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public event Action<string, bool>? StateChanged;

    public TunnelManager(SettingsService settings) => _settings = settings;

    public IReadOnlyList<string> RunningAliases
    {
        get { lock (_lock) return _sessions.Values.Where(session => !session.Process.HasExited)
            .Select(session => session.Alias).OrderBy(value => value).ToList(); }
    }

    public IReadOnlyList<string> RequestedAliases
    {
        get { lock (_lock) return _requestedAliases.ToList(); }
    }

    public bool IsRequested(string alias)
    {
        lock (_lock) return _requestedAliases.Contains(alias);
    }

    /// <summary>停掉移除的 Host，仅重启配置发生变化且仍请求运行的 Host。</summary>
    public void SynchronizeConfig(IReadOnlyDictionary<string, string> configurations, bool startNew)
    {
        lock (_operations)
        {
            foreach (var alias in RequestedAliases)
            {
                if (!configurations.TryGetValue(alias, out var configuration)) Stop(alias);
                else if (!_configurations.TryGetValue(alias, out var previous) || previous != configuration)
                    Restart(alias);
            }
            if (startNew)
                foreach (var alias in configurations.Keys.Except(_configurations.Keys, StringComparer.OrdinalIgnoreCase))
                    Start(alias);
            _configurations = new Dictionary<string, string>(configurations, StringComparer.OrdinalIgnoreCase);
        }
    }

    public bool IsRunning(string alias)
    {
        lock (_lock)
            return _sessions.TryGetValue(alias, out var session) && !session.Process.HasExited;
    }

    public IReadOnlyCollection<int> TrackedPids
    {
        get
        {
            lock (_lock)
                return _sessions.Values.Where(session => !session.Process.HasExited)
                    .Select(session => session.Process.Id).ToList();
        }
    }

    public bool Start(string alias, bool autoStart = false)
    {
        lock (_operations)
        {
            lock (_lock)
            {
                if (_disposed) return false;
                if (autoStart && _manualStops.Contains(alias)) return false;
                _manualStops.Remove(alias);
                _requestedAliases.Add(alias);
            }
            var started = StartCore(alias, reconnectAttempt: 0, cancelPendingReconnect: true);
            if (!started && !IsRunning(alias)) ScheduleReconnect(alias, 1);
            return started;
        }
    }

    /// <summary>停止后立即重开（用于网络切换 / 端口探活失败）</summary>
    public bool Restart(string alias)
    {
        lock (_operations)
        {
            // 看门狗的旧快照不能重新启动用户已经停止的隧道。
            if (!IsRequested(alias)) return false;
            if (!Stop(alias)) return false;
            return Start(alias);
        }
    }

    /// <summary>重启当前所有在跑的隧道</summary>
    public void RestartRunning()
    {
        List<string> aliases;
        lock (_lock) aliases = _sessions.Keys.ToList();
        foreach (var alias in aliases) Restart(alias);
    }

    private bool StartCore(string alias, int reconnectAttempt, bool cancelPendingReconnect)
    {
        lock (_operations)
            return StartSerialized(alias, reconnectAttempt, cancelPendingReconnect);
    }

    private bool StartSerialized(string alias, int reconnectAttempt, bool cancelPendingReconnect)
    {
        TunnelSession? previous;
        lock (_lock)
        {
            if (_disposed || !_requestedAliases.Contains(alias)) return false;
            _sessions.TryGetValue(alias, out previous);
            if (previous != null && !previous.Process.HasExited) return false;
            if (cancelPendingReconnect)
            {
                CancelPendingReconnectLocked(alias);
            }
        }
        if (previous != null && !previous.Exited.Task.Wait(TimeSpan.FromSeconds(5))) return false;

        Process? process = null;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _settings.SshExePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("-N");
            startInfo.ArgumentList.Add("-T");
            startInfo.ArgumentList.Add("-F");
            startInfo.ArgumentList.Add(_settings.ConfigPath);
            foreach (var option in new[]
            {
                "BatchMode=yes", "ExitOnForwardFailure=yes", "ConnectTimeout=15", "StdinNull=yes",
                "ControlMaster=no", "ControlPath=none", "ControlPersist=no", "ForkAfterAuthentication=no",
            })
            {
                startInfo.ArgumentList.Add("-o");
                startInfo.ArgumentList.Add(option);
            }
            startInfo.ArgumentList.Add(alias);

            process = new Process { StartInfo = startInfo, EnableRaisingEvents = false };
            if (!process.Start()) throw new InvalidOperationException("ssh.exe 没有成功启动进程。");

            var session = new TunnelSession
            {
                Alias = alias,
                Process = process,
                StartTime = DateTime.Now,
                ReconnectAttempt = reconnectAttempt,
            };
            lock (_lock)
            {
                _sessions[alias] = session;
                _lastErrors.Remove(alias);
            }

            var logPath = _settings.GetLogPath(alias);
            AppendLog(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] --- ssh -N 启动"
                + (reconnectAttempt > 0 ? $"（第 {reconnectAttempt} 次重连）" : "") + " ---");
            session.OutputDrain = Drain(process.StandardOutput, logPath, session, isError: false);
            session.ErrorDrain = Drain(process.StandardError, logPath, session, isError: true);
            process.Exited += (_, _) => _ = OnExitedAsync(session);
            process.EnableRaisingEvents = true;
            _ = ResetBackoffAfterStableRunAsync(session);
            NotifyStateChanged(alias, IsRunning(alias));
            return true;
        }
        catch (Exception ex)
        {
            try { process?.Dispose(); } catch { }
            RecordStartError(alias, ex);
            return false;
        }
    }

    public bool Stop(string alias)
    {
        lock (_operations) return StopSerialized(alias);
    }

    private bool StopSerialized(string alias)
    {
        TunnelSession? session;
        lock (_lock)
        {
            _requestedAliases.Remove(alias);
            _manualStops.Add(alias);
            CancelPendingReconnectLocked(alias);
            if (!_sessions.TryGetValue(alias, out session))
            {
                _lastErrors.Remove(alias);
                NotifyStateChanged(alias, false);
                return true;
            }
            session.StopRequested = true;
            _lastErrors.Remove(alias);
        }
        return KillProcess(session);
    }

    public void StopAll()
    {
        lock (_operations) StopAllSerialized();
    }

    private void StopAllSerialized()
    {
        List<TunnelSession> sessions;
        lock (_lock)
        {
            _manualStops.UnionWith(_configurations.Keys);
            _manualStops.UnionWith(_requestedAliases);
            _requestedAliases.Clear();
            foreach (var pending in _pendingReconnects.Values) pending.Cancel();
            _pendingReconnects.Clear();
            _pendingReconnectInfo.Clear();
            sessions = _sessions.Values.ToList();
            foreach (var session in sessions) session.StopRequested = true;
            _lastErrors.Clear();
        }
        foreach (var session in sessions) KillProcess(session);
        NotifyStateChanged("", false);
    }

    private static bool KillProcess(TunnelSession session)
    {
        try
        {
            if (session.Exited.Task.IsCompleted) return true;
            if (!session.Process.HasExited) session.Process.Kill(true);
            // Kill 是异步的；等待退出回调释放监听端口及旧 session 后才能重开。
            return session.Exited.Task.Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
            return session.Exited.Task.IsCompleted;
        }
    }

    private async Task OnExitedAsync(TunnelSession session)
    {
        try
        {
            await Task.WhenAll(session.OutputDrain, session.ErrorDrain);
            bool shouldReconnect;
            lock (_lock)
            {
                var isCurrent = _sessions.TryGetValue(session.Alias, out var current) && ReferenceEquals(current, session);
                if (isCurrent) _sessions.Remove(session.Alias);
                if (isCurrent && !session.StopRequested && !string.IsNullOrWhiteSpace(session.LastError))
                    _lastErrors[session.Alias] = session.LastError;
                shouldReconnect = isCurrent && !_disposed && _settings.AutoReconnect && !session.StopRequested
                    && _requestedAliases.Contains(session.Alias);
            }

            AppendLog(_settings.GetLogPath(session.Alias),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] --- 进程退出（代码 {SafeExitCode(session.Process)}） ---");
            NotifyStateChanged(session.Alias, false);
            if (shouldReconnect) ScheduleReconnect(session.Alias, session.ReconnectAttempt + 1);
        }
        finally
        {
            session.Process.Dispose();
            session.Exited.TrySetResult(true);
        }
    }

    private void ScheduleReconnect(string alias, int attempt)
    {
        CancellationTokenSource cancellation;
        lock (_lock)
        {
            if (_disposed || !_settings.AutoReconnect || !_requestedAliases.Contains(alias)
                || (_sessions.TryGetValue(alias, out var session) && !session.Process.HasExited)) return;
            CancelPendingReconnectLocked(alias);
            cancellation = new CancellationTokenSource();
            _pendingReconnects[alias] = cancellation;
            var retrySeconds = Math.Min(5 * Math.Max(1, attempt), 60);
            _pendingReconnectInfo[alias] = (attempt, DateTime.Now.AddSeconds(retrySeconds));
        }

        var seconds = Math.Min(5 * Math.Max(1, attempt), 60);
        AppendLog(_settings.GetLogPath(alias),
            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 将在 {seconds} 秒后进行第 {attempt} 次自动重连。");
        NotifyStateChanged(alias, false);
        _ = ReconnectAfterDelayAsync(alias, attempt, TimeSpan.FromSeconds(seconds), cancellation);
    }

    private async Task ReconnectAfterDelayAsync(
        string alias, int attempt, TimeSpan delay, CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(delay, cancellation.Token);
            lock (_lock)
            {
                if (_disposed || cancellation.IsCancellationRequested) return;
                if (_pendingReconnects.TryGetValue(alias, out var current) && ReferenceEquals(current, cancellation))
                {
                    _pendingReconnects.Remove(alias);
                    _pendingReconnectInfo.Remove(alias);
                }
                else
                    return;
            }

            if (!IsRunning(alias) && !StartCore(alias, attempt, cancelPendingReconnect: false))
                ScheduleReconnect(alias, attempt + 1);
        }
        catch (OperationCanceledException) { }
        finally
        {
            cancellation.Dispose();
        }
    }

    private async Task ResetBackoffAfterStableRunAsync(TunnelSession session)
    {
        if (session.ReconnectAttempt == 0) return;
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), _lifetimeCancellation.Token);
            lock (_lock)
            {
                if (!_disposed && _sessions.TryGetValue(session.Alias, out var current)
                    && ReferenceEquals(current, session) && !session.Process.HasExited)
                    session.ReconnectAttempt = 0;
            }
        }
        catch (OperationCanceledException) { }
    }

    public string? GetLastError(string alias)
    {
        lock (_lock) return _lastErrors.TryGetValue(alias, out var error) ? error : null;
    }

    public string? GetReconnectStatus(string alias)
    {
        lock (_lock)
        {
            if (!_pendingReconnectInfo.TryGetValue(alias, out var info)) return null;
            var seconds = Math.Max(0, (int)Math.Ceiling((info.RetryAt - DateTime.Now).TotalSeconds));
            return $"等待第 {info.Attempt} 次重连 · {seconds} 秒";
        }
    }

    private void RecordStartError(string alias, Exception exception)
    {
        lock (_lock) _lastErrors[alias] = exception.Message;
        try
        {
            AppendLog(_settings.GetLogPath(alias),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 启动失败：{exception}");
        }
        catch { }
        NotifyStateChanged(alias, false);
    }

    private void NotifyStateChanged(string alias, bool running)
    {
        try { StateChanged?.Invoke(alias, running); } catch { }
    }

    private static async Task Drain(StreamReader reader, string logPath, TunnelSession session, bool isError)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                if (isError && !string.IsNullOrWhiteSpace(line)) session.LastError = line;
                AppendLog(logPath, $"[{DateTime.Now:HH:mm:ss}] {line}");
            }
        }
        catch { }
    }

    private static int SafeExitCode(Process process)
    {
        try { return process.ExitCode; } catch { return -1; }
    }

    private bool CancelPendingReconnectLocked(string alias)
    {
        if (!_pendingReconnects.Remove(alias, out var cancellation)) return false;
        _pendingReconnectInfo.Remove(alias);
        cancellation.Cancel();
        return true;
    }

    private static void AppendLog(string logPath, string line)
    {
        try
        {
            var directory = Path.GetDirectoryName(logPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var sync = LogLocks.GetOrAdd(logPath, _ => new object());
            lock (sync) File.AppendAllText(logPath, line + Environment.NewLine, Encoding.UTF8);
        }
        catch { }
    }

    public void Dispose()
    {
        lock (_operations)
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                _lifetimeCancellation.Cancel();
            }
            StopAll();
            _lifetimeCancellation.Dispose();
        }
    }
}
