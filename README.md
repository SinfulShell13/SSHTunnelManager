# SSH 隧道管理器

常驻系统托盘，只做两件事：

1. **后台运行隧道和常驻反向代理** — 自动托管 `~/.ssh/config` 中启用的 `LocalForward` Host；仅有 `RemoteForward` 的 Host 需显式标记常驻，才执行后台 `ssh -N` 和断线重连
2. **打开 config** — 用记事本打开 `~/.ssh/config`；保存后自动重载隧道列表

主机增删、端口转发、密钥等**一律直接编辑 config 文件**，本软件不做可视化编辑。

技术栈：C# / WPF + WinForms NotifyIcon（.NET 8），依赖系统 OpenSSH `ssh.exe`。

---

## 行为

| 项 | 说明 |
|---|---|
| 托管判定 | 显式 Host 有启用的 `LocalForward`，或有启用的 `RemoteForward` 且 Host 块内带 `# SSHTunnelManager: persistent-proxy` 标记；注释转发及通配 Host 不列出 |
| 启动 | 程序启动约 2 秒后自动拉起全部隧道；托盘也可手动启停 |
| 重连 | 异常退出或首次启动失败后按 5、10、15… 秒渐进重连（最长 60 秒）；稳定运行一分钟后重置退避；手动停止取消重连 |
| 配置变更 | 新纳入托管的 Host 自动启动；移除、禁用转发或移除常驻代理标记后停止并取消重连；仅重启配置改变且仍启用运行的 Host，保留手动停止状态；全局默认改变会重启受影响的运行 Host |
| 连接隔离 | 每个 Host 使用独立 ssh 进程；管理器关闭连接复用及 SSH 自行后台化，防止代理与本地隧道共用 master |
| 存活检查 | 遵循 config 的 `ServerAliveInterval` / `ServerAliveCountMax`；每 20 秒检查全部本地监听端口，两次缺失后重启；不以本机端口判断 RemoteForward 健康 |
| 托盘状态 | 区分本地隧道、反向代理，以及 ssh 已启动、等待重连、启动失败；ssh 已启动不代表代理目标服务可用 |
| 双击托盘 | 打开 config |
| 日志 | `%APPDATA%\SSHTunnelManager\logs\<alias>-<日期>.log` |

## 托盘菜单

右键菜单使用 WPF 绘制，按屏幕 DPI 缩放（Per-Monitor V2），保留原有托盘图标。主机名、转发类型和运行状态分别排版，长名称省略显示并可通过提示查看；小屏幕上菜单可滚动。菜单打开时跟随系统明暗及高对比度配色，支持方向键、Enter、Esc 和点击外部关闭。后台重连只更新状态，不替换正在操作的菜单项。

- 各隧道主机（点击启停）
- 启动全部 / 停止全部
- 打开 config / 重新加载 config / 打开日志目录
- 开机自启 / 退出

## 从本机通过 SSH 连接 Docker，并让容器使用本机代理

本节是一条完整的操作流程：本机通过 SSH 到达远端 Docker 主机，再连接其中的容器，同时把本机的 HTTP 代理转发到容器。示例中的地址、账号、端口和密钥路径都是占位符；请替换为自己的值，不要把私钥、密码或真实主机信息提交到公开仓库。

### 1. 确认远端 Docker 网络

先登录远端 Docker 主机，确认容器使用的网络模式、IP、网关和端口映射：

```bash
docker inspect -f '{{.HostConfig.NetworkMode}}' <container>
docker inspect -f '{{json .NetworkSettings.Ports}}' <container>
docker inspect -f '{{range .NetworkSettings.Networks}}{{.IPAddress}} {{.Gateway}}{{end}}' <container>
```

`host` 模式与宿主机共享网络命名空间，没有独立容器 IP，`-p` 端口映射不会生效；`bridge` 模式有独立 IP，通常通过宿主机回环端口发布，或由宿主机直接访问容器 IP。参见 Docker 的 [host 网络模式](https://docs.docker.com/engine/network/drivers/host/) 和 [端口发布](https://docs.docker.com/engine/network/port-publishing/) 文档。

下面先按最稳定的入口设计：

- `host` 模式：让容器 sshd 监听远端宿主机回环地址 `<CONTAINER_SSH_HOST_PORT>`。
- `bridge` 模式：将容器的 22 端口发布到远端宿主机回环地址，例如 `127.0.0.1:<CONTAINER_SSH_HOST_PORT>`。
- 如果远端宿主机明确能路由到容器 IP，也可以把隧道目标直接写成 `<CONTAINER_IP>:22`，但容器重建后 IP 可能变化。

不要把容器 SSH 端口发布到 `0.0.0.0`，除非已经配置好防火墙和访问控制。

### 2. 准备容器 SSH 服务

在容器中准备 OpenSSH Server，并把本机公钥追加到容器用户的 `~/.ssh/authorized_keys`。私钥只保留在本机。sshd 至少应关闭密码认证：

```text
PubkeyAuthentication yes
PasswordAuthentication no
KbdInteractiveAuthentication no
AllowUsers <CONTAINER_USER>
```

`host` 模式下让 sshd 监听 `127.0.0.1:<CONTAINER_SSH_HOST_PORT>`；`bridge` 模式下让 sshd 监听容器网络接口上的 `22` 端口。如果使用 Compose 发布 bridge 端口：

```yaml
services:
  app:
    ports:
      - "127.0.0.1:<CONTAINER_SSH_HOST_PORT>:22"
```

修改后在容器内用 `sshd -t` 校验配置，并确认远端主机能连接目标端口。

### 3. 写入本机 SSH 配置

Windows 常见位置是 `%USERPROFILE%\.ssh\config`，Linux/macOS 是 `~/.ssh/config`。把下面的 Host 块加入本机配置：

```sshconfig
# 这个 Host 由 SSHTunnelManager 常驻维护，只负责连接容器
Host docker-tunnel
  HostName <DOCKER_HOST>
  Port <DOCKER_SSH_PORT>
  User <DOCKER_HOST_USER>
  IdentityFile ~/.ssh/id_ed25519
  IdentitiesOnly yes
  ExitOnForwardFailure yes
  ServerAliveInterval 60
  ServerAliveCountMax 3

  # 本机端口 -> 远端 Docker 容器的 SSH 入口
  LocalForward 127.0.0.1:<LOCAL_CONTAINER_SSH_PORT> 127.0.0.1:<CONTAINER_SSH_HOST_PORT>

# 通过上面的本机端口登录容器
Host docker-container
  HostName 127.0.0.1
  Port <LOCAL_CONTAINER_SSH_PORT>
  User <CONTAINER_USER>
  IdentityFile ~/.ssh/id_ed25519
  IdentitiesOnly yes

# 这个 Host 由 SSHTunnelManager 常驻维护，只负责提供本机代理
Host docker-proxy
  # SSHTunnelManager: persistent-proxy
  HostName <DOCKER_HOST>
  Port <DOCKER_SSH_PORT>
  User <DOCKER_HOST_USER>
  IdentityFile ~/.ssh/id_ed25519
  IdentitiesOnly yes
  ExitOnForwardFailure yes
  ServerAliveInterval 60
  ServerAliveCountMax 3

  # 本机 HTTP 代理 -> 远端 SSH 主机上的代理端口
  RemoteForward 127.0.0.1:<REMOTE_PROXY_PORT> 127.0.0.1:<LOCAL_PROXY_PORT>
```

其中：

- `<LOCAL_CONTAINER_SSH_PORT>` 是本机未占用的端口。
- `<CONTAINER_SSH_HOST_PORT>` 是远端 Docker 主机上的容器 SSH 入口端口。
- `docker-proxy` 与 `docker-tunnel` 使用相同的远端主机、账号和密钥，但它们是两个独立的 SSH 进程。

`LocalForward` 会让本机的 `127.0.0.1:<LOCAL_CONTAINER_SSH_PORT>` 通到容器 SSH。`docker-proxy` 中的 `RemoteForward` 会让远端 SSH 主机的 `127.0.0.1:<REMOTE_PROXY_PORT>` 通到本机代理。`<LOCAL_PROXY_PORT>` 是本机 Clash 或其他 HTTP/mixed 代理端口，`<REMOTE_PROXY_PORT>` 是远端 SSH 主机上用于接收代理流量的端口，选择一个未占用的端口即可。

保存配置后，托盘中应出现 `docker-tunnel` 和 `docker-proxy`；可以分别启动和停止，也可以启用开机自启让它们自动运行。软件只读取配置，不会改写 SSH config。

### 4. 验证 SSH 直连容器

先查看本机转发是否建立，再执行：

```bash
ssh docker-container 'id; hostname'
```

如果失败，按顺序检查：远端容器 sshd 是否监听、远端主机端口是否发布、本机 `<LOCAL_CONTAINER_SSH_PORT>` 是否被占用，以及 `ssh -vvv docker-tunnel` 的认证和转发错误。

### 5. 让容器访问本机代理

`host` 模式容器与远端宿主机共用网络命名空间，容器内可以使用：

```text
http://127.0.0.1:<REMOTE_PROXY_PORT>
```

`bridge` 模式容器有自己的网络命名空间，容器内的 `127.0.0.1` 不是远端宿主机回环。此时在远端 Docker 主机上运行一个只绑定 Docker 网桥网关的 TCP 中继，把 `<REMOTE_PROXY_PORT>` 转到宿主机回环上的同一端口：

```bash
socat TCP-LISTEN:<REMOTE_PROXY_PORT>,bind=<DOCKER_GATEWAY>,reuseaddr,fork TCP:127.0.0.1:<REMOTE_PROXY_PORT>
```

`<DOCKER_GATEWAY>` 必须通过 `docker network inspect <network>` 查询，不能假定固定值。中继不要监听 `0.0.0.0`。bridge 容器最终使用：

```text
http://<DOCKER_GATEWAY>:<REMOTE_PROXY_PORT>
```

先在容器内验证代理链路：

```bash
curl -I --max-time 20 -x http://<PROXY_HOST>:<REMOTE_PROXY_PORT> https://chatgpt.com/codex/install.sh
```

`<PROXY_HOST>` 在 `host` 模式是 `127.0.0.1`，在 bridge 模式是 `<DOCKER_GATEWAY>`。

### 6. 在容器终端中一键开关代理

把下面内容写入容器中实际运行用户的 `~/.bashrc`。将 `<PROXY_HOST>` 和 `<REMOTE_PROXY_PORT>` 替换为上一步确定的值；如果容器使用 host 模式，`<PROXY_HOST>` 就是 `127.0.0.1`。这个开关只影响当前用户启动的终端程序，不会修改镜像或系统全局配置：

```bash
# ========== 终端代理一键开关 ==========
proxy_http="http://<PROXY_HOST>:<REMOTE_PROXY_PORT>"
proxy_socks="socks5://<PROXY_HOST>:<REMOTE_PROXY_PORT>"

alias proxy_on='
export http_proxy="$proxy_http"
export https_proxy="$proxy_http"
export all_proxy="$proxy_socks"
export no_proxy="localhost,127.0.0.1,192.168.0.0/16,10.0.0.0/8,172.16.0.0/12,::1,localaddress,.localdomain.com"
echo "✅ 代理已开启"
'

alias proxy_off='
unset http_proxy https_proxy all_proxy no_proxy
echo "❌ 代理已关闭"
'

alias proxy_status='echo "=== 当前代理状态 ===" && echo "http_proxy: $http_proxy"'
```

重新打开终端或执行 `source ~/.bashrc`，然后运行 `proxy_on`。如果代理只提供 HTTP/mixed 端口，`all_proxy` 的 SOCKS 地址应按实际代理能力调整；不能把 HTTP 端口当作 SOCKS 端口使用。

### 7. Codex CLI / Codex 桌面版必须使用 `~/.codex/.env`

`proxy_on` 只适用于当前 shell 及其普通子进程，例如 Git、curl 或包管理器。通过 SSH 连接到 Docker 后，Codex CLI 和 Codex 桌面版的远程进程不要依赖 `~/.bashrc` 或当前终端环境；请为运行 Codex 的同一个容器用户创建 `~/.codex/.env`：

```bash
mkdir -p ~/.codex
chmod 700 ~/.codex
cat > ~/.codex/.env <<'ENV'
HTTP_PROXY=http://<PROXY_HOST>:<REMOTE_PROXY_PORT>
HTTPS_PROXY=http://<PROXY_HOST>:<REMOTE_PROXY_PORT>
NO_PROXY=localhost,127.0.0.1,::1
ENV
chmod 600 ~/.codex/.env
```

`<PROXY_HOST>` 在 `host` 模式是 `127.0.0.1`，在 `bridge` 模式是 `<DOCKER_GATEWAY>`。不要把代理用户名、密码或认证令牌写入公开仓库。写入后重启 Codex CLI 或远程 Codex 会话，再执行：

```bash
codex exec --skip-git-repo-check 'reply with exactly: PROXY_TEST_OK'
```

收到 `PROXY_TEST_OK` 才表示 Codex 本身已经通过代理。`curl` 能通只代表基础 HTTP 链路可用，不能代替 Codex 的端到端检查。

Git 等普通终端程序仍可以使用上一节的开关：

```bash
proxy_on
git ls-remote https://github.com/<OWNER>/<REPO>.git
proxy_off
```

如果只让单条 Git 命令走代理：

```bash
HTTPS_PROXY="$proxy_http" HTTP_PROXY="$proxy_http" git ls-remote https://github.com/<OWNER>/<REPO>.git
```
### 8. VS Code Remote-SSH 与 Codex 插件

先确认命令行的 `ssh docker-container` 和容器内代理链路都正常，再在 VS Code 中使用 `Remote-SSH: Connect to Host...` 连接 `docker-container`。普通 VS Code 终端可以使用 `proxy_on`；Codex 插件或 Codex 桌面版的远程进程必须使用容器用户的 `~/.codex/.env`。这样 VS Code Server 和远程终端都运行在容器环境中。

在远程窗口打开 `Preferences: Open Remote Settings (JSON)`，按容器内实际代理地址填写：

```jsonc
{
  "http.proxy": "http://<PROXY_HOST>:<REMOTE_PROXY_PORT>",
  "http.proxySupport": "on",
  "http.proxyStrictSSL": true,
  "terminal.integrated.env.linux": {
    "HTTP_PROXY": "http://<PROXY_HOST>:<REMOTE_PROXY_PORT>",
    "HTTPS_PROXY": "http://<PROXY_HOST>:<REMOTE_PROXY_PORT>",
    "http_proxy": "http://<PROXY_HOST>:<REMOTE_PROXY_PORT>",
    "https_proxy": "http://<PROXY_HOST>:<REMOTE_PROXY_PORT>",
    "NO_PROXY": "localhost,127.0.0.1,::1",
    "no_proxy": "localhost,127.0.0.1,::1"
  }
}
```

`terminal.integrated.env.linux` 只覆盖 VS Code 集成终端及其子进程。它不能替代 Codex 的 `~/.codex/.env`；Codex 插件或远程 Codex 进程启动后若仍无法联网，应确认 `.env` 位于实际运行用户的容器 home 下，并重启 VS Code Server 或重新连接 SSH。保持 `http.proxyStrictSSL` 为 `true`，公司代理做 TLS 检查时应安装正确的 CA 证书，不要关闭证书校验。

相关参考：[VS Code Remote-SSH 代理排查](https://code.visualstudio.com/docs/remote/troubleshooting#_set-http_proxy-https_proxy-on-the-remote-host)、[VS Code 网络连接和代理](https://code.visualstudio.com/docs/setup/network)。

后台连接使用 `BatchMode=yes`，需要密钥或 ssh-agent 提供无需交互的认证，且已完成主机指纹确认。不要让多个后台服务同时维护同一条转发。

## 构建

需安装 **.NET 8 SDK**（本机若无 SDK 无法编译）：

```powershell
dotnet build SSHTunnelManager.csproj -c Release
dotnet test Tests\SSHTunnelManager.Tests.csproj -c Release
dotnet publish SSHTunnelManager.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o bin/Release/net8.0-windows/win-x64/publish
```

提交代码前请确认没有把 `bin/`、`obj/`、`publish/`、`artifacts/` 或本机配置文件加入版本库；这些路径已列入 `.gitignore`。GitHub Actions 会在 Windows runner 上自动执行 Release 构建和测试。

## 贡献

开发环境、测试命令和 Pull Request 约定见 [CONTRIBUTING.md](CONTRIBUTING.md)。项目刻意保持托盘-only、SSH config 只读和零额外运行时依赖；涉及这些边界的改动请先在 issue 中说明使用场景。

## 数据

| 数据 | 位置 |
|---|---|
| SSH 配置 | `%USERPROFILE%\.ssh\config`（唯一主机/隧道数据源） |
| 应用设置 | `%APPDATA%\SSHTunnelManager\settings.json`（ssh 路径、日志、自启等） |
| 隧道日志 | `%APPDATA%\SSHTunnelManager\logs\` |
| 开机自启 | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` |

## 命令行

| 参数 | 说明 |
|---|---|
| `--startup` | 开机自启用（静默驻留） |

## 许可证

本项目使用 [MIT License](LICENSE)。第三方组件仍受其各自许可证约束。






