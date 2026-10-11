# Repository Guidelines

## What this is

.NET 8 **WPF + WinForms hybrid** tray app（WPF Application + WinForms `NotifyIcon`）。

**范围（有意保持极简）**：
1. 后台运行隧道及常驻反向代理：解析 `~/.ssh/config`，自动托管启用的 `LocalForward`；仅有 `RemoteForward` 的 Host 必须在对应块内显式标记 `# SSHTunnelManager: persistent-proxy` 才跑独立的 `ssh -N`
2. 打开 config：记事本编辑，保存后自动重载

**不做**：主机/转发可视化编辑、密钥上传、设置窗、主窗口、备份 UI。

Two projects, **no `.sln`** — always pass csproj paths:

| Project | Role |
|---|---|
| `SSHTunnelManager.csproj` | App (`net8.0-windows`, `UseWPF` + `UseWindowsForms`) |
| `Tests/SSHTunnelManager.Tests.csproj` | xUnit; references the app project |

Main csproj **excludes** `Tests/**/*.cs`. Do not add a solution file unless asked.

Layout: `App.xaml(.cs)` 启动与托盘装配；`Models/SshHost.cs`；`Services/`（`SshConfigParser` 只读解析、`TunnelManager`、`TrayService`、`SettingsService`、`ConfigWatcher`、`AutoStartService`）；`Resources/TrayMenu.xaml` 为 WPF 托盘菜单样式，`app.manifest` 声明 Per-Monitor V2 DPI。

## Commands

```powershell
dotnet restore SSHTunnelManager.csproj
dotnet build SSHTunnelManager.csproj
dotnet build SSHTunnelManager.csproj -c Release
dotnet test Tests\SSHTunnelManager.Tests.csproj -c Release
dotnet publish SSHTunnelManager.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o bin/Release/net8.0-windows/win-x64/publish
```

- Tests always via Tests csproj path.
- Close running instance before `publish`.
- Single-instance mutex: `Global\SSHTunnelManager_SingleInstance`.

## Critical invariants

1. **Config is read-only.** Never write `~/.ssh/config`. Users edit in notepad; `ConfigWatcher` reloads.
2. **Managed tunnel = enabled LocalForward, or enabled RemoteForward with a Host-scoped `# SSHTunnelManager: persistent-proxy` marker.** Ordinary RemoteForward-only login Hosts must not be listed or auto-started. Commented forwards do not qualify; wildcard `Host *` is not listed. Keep proxy Hosts separate from login tunnels; do not reuse SSH control masters.
3. **No extra NuGet.** Do not re-add `Renci.SshNet` / `System.Management` unless a new feature needs them.
4. **Tray-only.** No main/settings windows. New features should almost always say no.
5. **Clipboard / theme / key upload code must not return** without an explicit product decision.

## Style

- File-scoped namespaces; four-space indent; nullable enabled.
- Follow surrounding code; prefer deleting complexity over adding abstraction.

## Testing

- `Tests/SshConfigParserTests.cs` covers parse/tunnel-detection only.
- `Tests/TunnelManagerTests.cs` uses system OpenSSH against a local TCP listener to cover lifecycle, retries, cancellation and config reconciliation; `Tests/ConfigWatcherTests.cs` covers file change events.
- `Tests/TrayServiceTests.cs` exercises WPF tray menu labels, start/stop, reload, right-click opening, Escape dismissal and focus preservation on an STA thread with temporary settings and config; light/dark layout checks cover long aliases and rendering at 100–200% scales. Optional `SSHTUNNEL_MENU_PREVIEW_DIR` exports preview PNGs.
- Tests use temp dirs; no real `~/.ssh`.
- After changes: `dotnet build` + test command, then manually exercise tray start/stop and config reload.

## Security / user data

Never commit SSH private keys, passwords, personal host names, or `settings.json` (in `.gitignore`).

## Commits

Short imperative messages (`Simplify to tray-only tunnel runner`). Keep commits focused.
