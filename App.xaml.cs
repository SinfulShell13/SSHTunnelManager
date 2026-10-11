using System.Diagnostics;
using System.IO;
using System.Windows;
using SSHTunnelManager.Services;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace SSHTunnelManager;

public partial class App : Application
{
    private Mutex? _mutex;
    private SettingsService? _settings;
    private TunnelManager? _tunnels;
    private TrayService? _tray;
    private ConfigWatcher? _configWatcher;
    private TunnelWatchdog? _watchdog;
    private readonly CancellationTokenSource _shutdown = new();
    private List<string> _aliases = new();
    private bool _startupComplete;
    private bool _quitting;
    private int _reloadVersion;

    public SettingsService Settings => _settings!;
    public TunnelManager Tunnels => _tunnels!;

    public static bool DetectSystemDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var v = key?.GetValue("AppsUseLightTheme");
            return v is int i && i == 0;
        }
        catch
        {
            return true;
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try { AutoStartService.EnsureSilentArg(); } catch { }

        _mutex = new Mutex(true, @"Global\SSHTunnelManager_SingleInstance", out var createdNew);
        if (!createdNew)
        {
            Shutdown();
            return;
        }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        DispatcherUnhandledException += (_, args) =>
        {
            try { MessageBox.Show(args.Exception.ToString(), "发生错误", MessageBoxButton.OK, MessageBoxImage.Error); }
            catch { }
            args.Handled = true;
        };

        _settings = new SettingsService();
        _tunnels = new TunnelManager(_settings);
        _tray = new TrayService(
            _tunnels,
            OpenConfig,
            ReloadConfig,
            QuitApplication,
            isDark: DetectSystemDark());

        ReloadConfig();

        _configWatcher = new ConfigWatcher(_settings.ConfigPath, () =>
            Dispatcher.BeginInvoke(ReloadConfig));

        // 网络变更时重启；本地监听端口缺失时恢复转发。
        _watchdog = new TunnelWatchdog(_tunnels, () => _settings.ConfigPath);

        // 启动 2 秒后拉起全部隧道（后台无感运行）
        Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                await Task.Delay(2000, _shutdown.Token);
                _startupComplete = true;
                StartAllTunnels();
            }
            catch (OperationCanceledException) { }
        });
    }

    private void StartAllTunnels()
    {
        if (_tunnels == null) return;
        foreach (var alias in _aliases)
        {
            if (!_tunnels.IsRequested(alias))
                _tunnels.Start(alias, autoStart: true);
        }
    }

    public async void ReloadConfig()
    {
        if (_settings == null || _tray == null || _tunnels == null || _quitting) return;
        var version = ++_reloadVersion;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                string text;
                try { text = File.ReadAllText(_settings.ConfigPath); }
                catch (FileNotFoundException) { text = ""; }
                catch (DirectoryNotFoundException) { text = ""; }
                if (_quitting || version != _reloadVersion) return;
                var hosts = SshConfigParser.ParseText(text).Where(host => host.IsTunnel).ToList();
                var configurations = hosts.GroupBy(host => host.Alias, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => string.Join("\n", group.Select(host => host.Configuration)),
                        StringComparer.OrdinalIgnoreCase);
                _tunnels.SynchronizeConfig(configurations, startNew: _startupComplete);
                _aliases = configurations.Keys.ToList();
                _tray.SetTunnels(hosts);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 2)
                {
                    _tray.ShowBalloon("config 重载失败", ex.Message, System.Windows.Forms.ToolTipIcon.Warning);
                    return;
                }
                try { await Task.Delay(200, _shutdown.Token); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    public void OpenConfig()
    {
        if (_settings == null) return;
        try
        {
            var path = _settings.ConfigPath;
            Process.Start(new ProcessStartInfo
            {
                FileName = "notepad.exe",
                Arguments = $"\"{path}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show("无法打开 config：\n" + ex.Message, "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public void QuitApplication()
    {
        DisposeServices();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DisposeServices();
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        base.OnExit(e);
    }

    private void DisposeServices()
    {
        if (_quitting) return;
        _quitting = true;
        _shutdown.Cancel();
        try { _configWatcher?.Dispose(); } catch { }
        try { _watchdog?.Dispose(); } catch { }
        try { _tray?.Dispose(); } catch { }
        try { _tunnels?.Dispose(); } catch { }
    }
}
