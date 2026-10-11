namespace SSHTunnelManager.Models;

/// <summary>ssh config 中的一个 Host 块（只读；编辑请直接改 config 文件）</summary>
public class SshHost
{
    public string Alias { get; set; } = "";

    /// <summary>用于重载比较的配置内容（保留常驻代理标记，忽略普通注释）；不写回文件</summary>
    public string Configuration { get; set; } = "";

    /// <summary>含被注释（禁用）的 LocalForward 行</summary>
    public List<ForwardEntry> LocalForwards { get; set; } = new();

    /// <summary>反向转发；例如将本机 Clash 代理提供给远端 Docker</summary>
    public List<ForwardEntry> RemoteForwards { get; set; } = new();

    /// <summary>Host 块内用 # SSHTunnelManager: persistent-proxy 显式标记常驻反向代理</summary>
    public bool PersistentProxy { get; set; }

    /// <summary>托管启用中的本地转发，以及显式标记常驻的反向代理</summary>
    public bool IsTunnel => LocalForwards.Exists(f => f.Enabled)
        || (PersistentProxy && RemoteForwards.Exists(f => f.Enabled));

    /// <summary>启用中的 LocalForward 的本地监听端口</summary>
    public List<int> EnabledListenPorts => LocalForwards
        .Where(f => f.Enabled)
        .Select(f => f.ListenPort)
        .Where(p => p > 0)
        .Distinct()
        .ToList();
}

/// <summary>一条 LocalForward / RemoteForward 规则</summary>
public class ForwardEntry
{
    public bool Enabled { get; set; } = true;

    /// <summary>原始参数：[bind_address:]port host:hostport</summary>
    public string Value { get; set; } = "";

    /// <summary>本地监听端口；无法解析时为 0</summary>
    public int ListenPort
    {
        get
        {
            var parts = Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return 0;
            var first = parts[0];
            var portText = first.Contains(':') ? first[(first.LastIndexOf(':') + 1)..] : first;
            return int.TryParse(portText, out var port) && port > 0 && port < 65536 ? port : 0;
        }
    }
}
