using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using System.Windows.Threading;
using SSHTunnelManager.Services;
using Xunit;

namespace SSHTunnelManager.Tests;

public sealed class TrayServiceTests
{
    [Fact]
    public async Task Tray_LabelsProxiesSupportsStartStopAndReloadsConfig()
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                ExerciseTray();
                completion.SetResult(true);
            }
            catch (Exception ex) { completion.SetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static void ExerciseTray()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SSHTunnelManagerTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var configPath = Path.Combine(directory, "config");
            var config = "Host jump\n LocalForward 2222 localhost:22\n"
                + "Host proxy\n # SSHTunnelManager: persistent-proxy\n RemoteForward 17899 localhost:7897\n"
                + "Host login\n RemoteForward 17890 localhost:7897\n";
            File.WriteAllText(configPath, config);
            var settings = new SettingsService(Path.Combine(directory, "settings"))
            {
                ConfigPath = configPath,
                SshExePath = Path.Combine(directory, "missing-ssh.exe"),
            };
            using var manager = new TunnelManager(settings);
            manager.SynchronizeConfig(SshConfigParser.Parse(configPath).Where(host => host.IsTunnel)
                .ToDictionary(host => host.Alias, host => host.Configuration), startNew: false);
            TrayService? tray = null;
            void ReloadConfig()
            {
                var hosts = SshConfigParser.Parse(configPath).Where(host => host.IsTunnel).ToList();
                var configurations = hosts.ToDictionary(host => host.Alias, host => host.Configuration);
                manager.SynchronizeConfig(configurations, startNew: true);
                tray!.SetTunnels(hosts);
            }
            using var ownedTray = tray = new TrayService(manager, () => { },
                ReloadConfig,
                () => { }, isDark: false);
            tray.SetTunnels(SshConfigParser.Parse(configPath));
            var menu = (ContextMenu)typeof(TrayService).GetField("_menu", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(tray)!;
            string Text(MenuItem item) => AutomationProperties.GetName(item) is { Length: > 0 } name
                ? name : item.Header?.ToString() ?? "";
            MenuItem Find(string text) => Assert.Single(menu.Items.OfType<MenuItem>(), item => Text(item).Contains(text));
            void Click(string text) => Find(text).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            void RightClickTray()
            {
                var icon = typeof(TrayService).GetField("_icon", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tray)!;
                icon.GetType().GetMethod("OnMouseUp", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(icon,
                    new object[] { new System.Windows.Forms.MouseEventArgs(System.Windows.Forms.MouseButtons.Right, 1, 0, 0, 0) });
                menu.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            }

            Assert.Contains("本地隧道", Text(Find("jump")));
            Assert.Contains("反向代理", Text(Find("proxy")));
            Assert.DoesNotContain(menu.Items.OfType<MenuItem>(), item => Text(item).Contains("login"));
            Click("启动全部");
            tray.RebuildMenu();
            Assert.Equal(2, manager.RequestedAliases.Count);
            Assert.Contains("等待重连", Text(Find("proxy")));

            // Keep the selected item alive when a background reconnect updates the open menu.
            RightClickTray();
            Assert.NotNull(PresentationSource.FromVisual(menu));
            Assert.True(menu.UseLayoutRounding);
            var selectedProxy = Find("proxy");
            FocusManager.SetFocusedElement(menu, selectedProxy);
            tray.RebuildMenu();
            Assert.Same(selectedProxy, Find("proxy"));
            Assert.Same(selectedProxy, FocusManager.GetFocusedElement(menu));
            Click("proxy");
            Assert.False(menu.IsOpen);
            tray.RebuildMenu();
            Assert.False(manager.IsRequested("proxy"));
            Assert.Null(manager.GetReconnectStatus("proxy"));
            Assert.Contains("已停止", Text(Find("proxy")));

            Click("停止全部");
            tray.RebuildMenu();
            Assert.Empty(manager.RequestedAliases);

            // 移除管理器标记仍保留 RemoteForward，后续普通 ssh 登录仍可按需建立转发。
            Click("proxy");
            Assert.True(manager.IsRequested("proxy"));
            File.WriteAllText(configPath, config.Replace(" # SSHTunnelManager: persistent-proxy\n", ""));
            Click("重新加载 config");
            Assert.False(manager.IsRequested("proxy"));
            Assert.Null(manager.GetReconnectStatus("proxy"));
            Assert.DoesNotContain(menu.Items.OfType<MenuItem>(), item => Text(item).Contains("proxy"));
            Assert.Contains("jump", Text(Find("jump")));

            // 再加标记后，仅新纳入的代理启动，手动停止的 jump 保持停止。
            File.WriteAllText(configPath, config);
            Click("重新加载 config");
            Assert.True(manager.IsRequested("proxy"));
            Assert.False(manager.IsRequested("jump"));
            Assert.False(manager.IsRequested("login"));
            Assert.Contains("等待重连", Text(Find("proxy")));
            RightClickTray();
            menu.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(menu),
                Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent });
            Assert.False(menu.IsOpen);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tray_MenuFitsLongHostNamesAndRendersAtDifferentScales(bool dark)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "SSHTunnelManagerTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var settings = new SettingsService(Path.Combine(directory, "settings"))
                {
                    SshExePath = Path.Combine(directory, "missing-ssh.exe"),
                };
                using var manager = new TunnelManager(settings);
                using var tray = new TrayService(manager, () => { }, () => { }, () => { }, dark);
                tray.SetTunnels(SshConfigParser.ParseText(
                    "Host node-a-jump\n LocalForward 2222 localhost:22\n"
                    + "Host node-a-proxy\n # SSHTunnelManager: persistent-proxy\n RemoteForward 17899 localhost:7897\n"
                    + "Host node-b-jump\n LocalForward 2223 localhost:22\n"
                    + "Host node-b-proxy\n # SSHTunnelManager: persistent-proxy\n RemoteForward 17899 localhost:7897\n"
                    + "Host node-c-jump\n LocalForward 2224 localhost:22\n"
                    + "Host unusually-long-host-name-to-verify-ellipsis-and-status-alignment\n"
                    + " # SSHTunnelManager: persistent-proxy\n RemoteForward 17900 localhost:7897\n"));
                manager.Start("node-b-proxy");
                tray.RebuildMenu();
                var menu = (ContextMenu)typeof(TrayService).GetField("_menu", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(tray)!;
                menu.ApplyTemplate();
                menu.Measure(new Size(440, 900));
                menu.Arrange(new Rect(menu.DesiredSize));
                menu.UpdateLayout();
                Assert.InRange(menu.ActualWidth, 350, 440);
                Assert.True(menu.ActualHeight > 300);
                var hostItems = menu.Items.OfType<MenuItem>().Where(item => item.Header is Grid).ToList();
                Assert.Equal(6, hostItems.Count);
                double? statusRight = null;
                foreach (var item in hostItems)
                {
                    var row = (Grid)item.Header;
                    var status = Assert.Single(row.Children.OfType<TextBlock>());
                    var right = status.TranslatePoint(new Point(status.ActualWidth, 0), menu).X;
                    Assert.True(status.ActualWidth >= status.DesiredSize.Width);
                    if (statusRight != null) Assert.InRange(Math.Abs(right - statusRight.Value), 0, 1);
                    statusRight = right;
                }
                foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
                {
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(menu.ActualWidth * scale),
                        (int)Math.Ceiling(menu.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                    bitmap.Render(menu);
                    var previewDirectory = Environment.GetEnvironmentVariable("SSHTUNNEL_MENU_PREVIEW_DIR");
                    if (!string.IsNullOrEmpty(previewDirectory))
                    {
                        Directory.CreateDirectory(previewDirectory);
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var file = File.Create(Path.Combine(previewDirectory, $"menu-{(dark ? "dark" : "light")}-{scale * 100:0}.png"));
                        encoder.Save(file);
                    }
                }
                completion.SetResult(true);
            }
            catch (Exception ex) { completion.SetException(ex); }
            finally { Directory.Delete(directory, recursive: true); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }
}
