using System.IO;
using System.Threading;

namespace SSHTunnelManager.Services;

/// <summary>只读监视 config 的创建、保存、替换和删除；允许启动时目录尚不存在。</summary>
public sealed class ConfigWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly Action _onChange;
    private readonly string _configPath;
    private readonly object _timerLock = new();
    private System.Threading.Timer? _debounce;
    private bool _disposed;

    public ConfigWatcher(string configPath, Action onChange)
    {
        _onChange = onChange;
        _configPath = Path.GetFullPath(configPath);
        var dir = Path.GetDirectoryName(_configPath)!;
        var watchDir = dir;
        while (!Directory.Exists(watchDir))
            watchDir = Path.GetDirectoryName(watchDir)
                ?? throw new DirectoryNotFoundException("找不到可监视的 config 父目录。");

        _watcher = new FileSystemWatcher(watchDir)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            IncludeSubdirectories = !string.Equals(dir, watchDir, StringComparison.OrdinalIgnoreCase),
        };
        _watcher.Changed += OnChanged;
        _watcher.Created += OnChanged;
        _watcher.Deleted += OnChanged;
        _watcher.Renamed += (_, args) =>
        {
            if (IsConfig(args.FullPath) || IsConfig(args.OldFullPath)) Schedule();
        };
        _watcher.Error += (_, _) => Schedule();
        _watcher.EnableRaisingEvents = true;
    }

    private bool IsConfig(string path) => string.Equals(path, _configPath, StringComparison.OrdinalIgnoreCase);

    private void OnChanged(object sender, FileSystemEventArgs args)
    {
        if (IsConfig(args.FullPath)) Schedule();
    }

    private void Schedule()
    {
        lock (_timerLock)
        {
            if (_disposed) return;
            _debounce?.Dispose();
            _debounce = new System.Threading.Timer(_ =>
            {
                lock (_timerLock) if (_disposed) return;
                try { _onChange(); } catch { }
            }, null, 400, Timeout.Infinite);
        }
    }

    public void Dispose()
    {
        lock (_timerLock)
        {
            if (_disposed) return;
            _disposed = true;
            _debounce?.Dispose();
        }
        _watcher.Dispose();
    }
}
