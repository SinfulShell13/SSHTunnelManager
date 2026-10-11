using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using SSHTunnelManager.Models;
using Icon = System.Drawing.Icon;
using NotifyIcon = System.Windows.Forms.NotifyIcon;
using ToolTipIcon = System.Windows.Forms.ToolTipIcon;
using Path = System.IO.Path;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using SystemColors = System.Windows.SystemColors;

namespace SSHTunnelManager.Services;

/// <summary>WinForms 托盘图标 + 支持 DPI 缩放的 WPF 右键菜单。</summary>
public class TrayService : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenu _menu;
    private readonly TunnelManager _tunnels;
    private readonly Action _quit;
    private readonly Action _openConfig;
    private readonly Action _reloadConfig;
    private readonly Action<string, bool> _stateChanged;
    private readonly Dictionary<string, string> _kinds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MenuItem> _hostItems = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;
    private List<string> _aliases = new();

    public TrayService(TunnelManager tunnels, Action openConfig, Action reloadConfig, Action quit, bool isDark)
    {
        _tunnels = tunnels;
        _openConfig = openConfig;
        _reloadConfig = reloadConfig;
        _quit = quit;
        _menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        _menu.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/SSHTunnelManager;component/Resources/TrayMenu.xaml", UriKind.Relative),
        });
        ApplyTheme(isDark);
        _menu.Opened += (_, _) => ActivateMenu();
        _menu.Closed += (_, _) => RebuildMenu();
        _icon = new NotifyIcon { Text = "SSH 隧道", Icon = LoadAppIcon(), Visible = true };
        _icon.DoubleClick += (_, _) => _openConfig();
        _icon.MouseUp += (_, args) =>
        {
            if (args.Button == System.Windows.Forms.MouseButtons.Right) ShowMenu();
        };
        _stateChanged = (_, _) => _menu.Dispatcher.BeginInvoke(RebuildMenu);
        _tunnels.StateChanged += _stateChanged;
        RebuildMenu();
    }

    private void ShowMenu()
    {
        if (_disposed) return;
        _menu.IsOpen = false;
        ApplyTheme(App.DetectSystemDark());
        RebuildMenu();
        // WPF computes placement in the cursor monitor's DPI and keeps the popup on screen.
        _menu.IsOpen = true;
    }

    private void ActivateMenu()
    {
        var dpi = VisualTreeHelper.GetDpi(_menu);
        var workArea = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea;
        var width = Math.Max(180, workArea.Width / dpi.DpiScaleX - 16);
        _menu.MinWidth = Math.Min(350, width);
        _menu.MaxWidth = Math.Min(440, width);
        _menu.MaxHeight = Math.Max(100, workArea.Height / dpi.DpiScaleY - 16);
        if (PresentationSource.FromVisual(_menu) is HwndSource source)
        {
            // Foreground activation enables keyboard input and outside-click dismissal for a tray popup.
            SetForegroundWindow(source.Handle);
            PostMessage(source.Handle, 0, IntPtr.Zero, IntPtr.Zero);
        }
        _menu.Focus();
    }

    private void ApplyTheme(bool dark)
    {
        var colors = new Dictionary<string, string>
        {
            ["MenuBackground"] = dark ? "#202020" : "#FAFAFA",
            ["MenuForeground"] = dark ? "#F3F3F3" : "#202020",
            ["MenuMuted"] = dark ? "#A8A8A8" : "#686868",
            ["MenuBorder"] = dark ? "#3C3C3C" : "#DDDDDD",
            ["MenuHover"] = dark ? "#333333" : "#ECECEC",
            ["MenuHoverBorder"] = "#00000000",
            ["MenuRunning"] = dark ? "#6CCB9A" : "#168558",
            ["MenuRetry"] = dark ? "#FCE100" : "#986600",
            ["MenuError"] = dark ? "#FF998F" : "#C42B1C",
        };
        foreach (var (key, value) in colors)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
            brush.Freeze();
            _menu.Resources[key] = brush;
        }
        if (SystemParameters.HighContrast)
        {
            _menu.Resources["MenuBackground"] = SystemColors.MenuBrush;
            _menu.Resources["MenuForeground"] = SystemColors.MenuTextBrush;
            _menu.Resources["MenuMuted"] = SystemColors.MenuTextBrush;
            _menu.Resources["MenuBorder"] = SystemColors.MenuTextBrush;
            _menu.Resources["MenuHover"] = SystemColors.MenuBrush;
            _menu.Resources["MenuHoverBorder"] = SystemColors.HighlightBrush;
            foreach (var key in new[] { "MenuRunning", "MenuRetry", "MenuError" })
                _menu.Resources[key] = SystemColors.MenuTextBrush;
        }
    }

    public void SetTunnels(IEnumerable<SshHost> hosts)
    {
        _kinds.Clear();
        foreach (var group in hosts.Where(host => host.IsTunnel).GroupBy(host => host.Alias, StringComparer.OrdinalIgnoreCase))
        {
            var local = group.Any(host => host.LocalForwards.Any(forward => forward.Enabled));
            var remote = group.Any(host => host.RemoteForwards.Any(forward => forward.Enabled));
            _kinds[group.Key] = local && remote ? "本地 / 反向转发" : remote ? "反向代理" : "本地隧道";
        }
        _aliases = _kinds.Keys.Where(alias => !string.IsNullOrWhiteSpace(alias))
            .OrderBy(alias => alias, StringComparer.OrdinalIgnoreCase).ToList();
        RebuildMenu();
    }

    public void RebuildMenu()
    {
        if (_disposed) return;
        // Preserve keyboard focus during reconnects. Structural changes appear on the next open.
        if (_menu.IsOpen)
        {
            foreach (var (alias, item) in _hostItems)
            {
                item.IsEnabled = _kinds.ContainsKey(alias);
                if (item.IsEnabled) UpdateTunnelItem(alias, item);
            }
            return;
        }
        _menu.Items.Clear();
        _hostItems.Clear();
        _menu.Items.Add(new MenuItem
        {
            Header = _aliases.Count == 0 ? "config 中无隧道主机" : "隧道 · 点击启停",
            IsEnabled = false, FontSize = 12, FontWeight = FontWeights.SemiBold,
        });
        foreach (var alias in _aliases)
        {
            var item = Add(alias, () =>
            {
                if (!_kinds.ContainsKey(alias)) return;
                if (_tunnels.IsRunning(alias) || _tunnels.GetReconnectStatus(alias) != null) _tunnels.Stop(alias);
                else _tunnels.Start(alias);
            });
            UpdateTunnelItem(alias, item);
            _hostItems.Add(alias, item);
            _menu.Items.Add(item);
        }
        _menu.Items.Add(new Separator());
        _menu.Items.Add(Add("启动全部", StartAll, "M 3,2 L 13,8 L 3,14 Z"));
        _menu.Items.Add(Add("停止全部", () => _tunnels.StopAll(), "M 3,3 L 13,3 L 13,13 L 3,13 Z"));
        _menu.Items.Add(new Separator());
        _menu.Items.Add(Add("打开 config", _openConfig, "M 4,1 L 10,1 L 13,4 L 13,15 L 4,15 Z M 10,1 L 10,4 L 13,4 M 7,7 L 10,7 M 7,10 L 10,10"));
        _menu.Items.Add(Add("重新加载 config", _reloadConfig, "M 13,5 A 6,6 0 1 0 14,9 M 13,1 L 13,5 L 9,5"));
        _menu.Items.Add(Add("打开日志目录", OpenLogsDir, "M 1,4 L 1,13 L 15,13 L 15,5 L 7,5 L 5,3 L 1,3 Z"));
        _menu.Items.Add(new Separator());
        var autoItem = Add("开机自启", ToggleAutoStart);
        autoItem.IsCheckable = true;
        autoItem.IsChecked = AutoStartService.IsEnabled();
        _menu.Items.Add(autoItem);
        _menu.Items.Add(Add("退出", _quit, "M 6,2 L 2,2 L 2,14 L 6,14 M 6,8 L 14,8 M 10,4 L 14,8 L 10,12"));
    }

    private void UpdateTunnelItem(string alias, MenuItem item)
    {
        var running = _tunnels.IsRunning(alias);
        var reconnect = _tunnels.GetReconnectStatus(alias);
        var error = _tunnels.GetLastError(alias);
        var active = running || reconnect != null;
        var state = running ? "ssh 已启动" : reconnect != null ? "等待重连" : error != null ? "启动失败" : "已停止";
        var colorKey = running ? "MenuRunning" : reconnect != null ? "MenuRetry" : error != null ? "MenuError" : "MenuMuted";
        var dot = new Ellipse { Width = 8, Height = 8, StrokeThickness = 1.3 };
        dot.SetResourceReference(Shape.StrokeProperty, colorKey);
        if (running || reconnect != null || error != null) dot.SetResourceReference(Shape.FillProperty, colorKey);
        item.Icon = dot;
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var name = new TextBlock
        {
            Text = alias, FontSize = 13, FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 220,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var kind = new TextBlock { Text = _kinds[alias], FontSize = 11, Margin = new Thickness(0, 3, 0, 0) };
        kind.SetResourceReference(TextBlock.ForegroundProperty, "MenuMuted");
        var labels = new StackPanel { Margin = new Thickness(0, 1, 12, 1) };
        labels.Children.Add(name);
        labels.Children.Add(kind);
        row.Children.Add(labels);
        var status = new TextBlock
        {
            Text = state, FontSize = 12, MinWidth = 76,
            TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
        };
        status.SetResourceReference(TextBlock.ForegroundProperty, colorKey);
        Grid.SetColumn(status, 1);
        row.Children.Add(status);
        item.Header = row;
        item.ToolTip = string.Join("\n", new[]
        {
            alias, active ? "点击停止（包括取消重连）" : "点击启动（后台 ssh -N）", reconnect, error,
        }.Where(value => !string.IsNullOrWhiteSpace(value)));
        AutomationProperties.SetName(item, $"{alias} · {_kinds[alias]} · {state}");
    }

    private MenuItem Add(string text, Action onClick, string? geometry = null)
    {
        var item = new MenuItem { Header = text };
        if (geometry != null)
        {
            var glyph = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(geometry), Width = 14, Height = 14, Stretch = Stretch.Uniform,
                StrokeThickness = 1.5, StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            };
            glyph.SetResourceReference(Shape.StrokeProperty, "MenuForeground");
            item.Icon = glyph;
        }
        item.Click += (_, _) => { _menu.IsOpen = false; onClick(); };
        return item;
    }

    private void ToggleAutoStart()
    {
        var enable = !AutoStartService.IsEnabled();
        AutoStartService.SetEnabled(enable);
        if (System.Windows.Application.Current is App app)
        {
            app.Settings.AppAutoStart = enable;
            app.Settings.Save();
        }
        RebuildMenu();
    }

    private void StartAll()
    {
        foreach (var alias in _aliases)
            if (!_tunnels.IsRunning(alias)) _tunnels.Start(alias);
    }

    private void OpenLogsDir()
    {
        try
        {
            var directory = (System.Windows.Application.Current as App)?.Settings.LogsDir;
            if (string.IsNullOrEmpty(directory)) return;
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo { FileName = directory, UseShellExecute = true });
        }
        catch { }
    }

    public void ShowBalloon(string title, string text, ToolTipIcon icon = ToolTipIcon.Info)
        => _icon.ShowBalloonTip(3000, title, text, icon);

    private static Icon LoadAppIcon()
    {
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("SSHTunnelManager.Resources.app.ico")
            ?? throw new InvalidOperationException("找不到嵌入资源 SSHTunnelManager.Resources.app.ico");
        return new Icon(stream);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tunnels.StateChanged -= _stateChanged;
        _menu.IsOpen = false;
        _menu.Items.Clear();
        _hostItems.Clear();
        _icon.Visible = false;
        var icon = _icon.Icon;
        _icon.Dispose();
        icon?.Dispose();
    }
}
