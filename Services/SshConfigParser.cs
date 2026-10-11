using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using SSHTunnelManager.Models;

namespace SSHTunnelManager.Services;

/// <summary>只读识别本地转发及显式标记的常驻反向代理。不写回 config。</summary>
public static class SshConfigParser
{
    private static readonly Regex DirectiveRegex = new(
        @"^(\w+)(?:\s*=\s*|\s+)(.+)$", RegexOptions.Compiled);
    private static readonly Regex PersistentProxyRegex = new(
        @"^#\s*SSHTunnelManager\s*:\s*persistent-proxy\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static List<SshHost> Parse(string configPath)
        => File.Exists(configPath) ? ParseText(File.ReadAllText(configPath)) : new();

    public static List<SshHost> ParseText(string text)
    {
        var hosts = new List<SshHost>();
        var current = new List<SshHost>();
        var sharedConfiguration = new StringBuilder();
        var sharedBlock = true;
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.Trim();
            if (PersistentProxyRegex.IsMatch(trimmed))
            {
                foreach (var host in current)
                {
                    host.PersistentProxy = true;
                    host.Configuration += "# SSHTunnelManager: persistent-proxy\n";
                }
                continue;
            }
            var enabled = !trimmed.StartsWith('#');
            if (!enabled) trimmed = trimmed[1..].TrimStart();
            var match = DirectiveRegex.Match(trimmed);
            if (!match.Success) continue;

            var keyword = match.Groups[1].Value;
            var value = match.Groups[2].Value.Split('#', 2)[0].Trim();
            if (enabled && keyword.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                var aliases = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                sharedBlock = aliases.Any(alias => alias.Contains('*') || alias.Contains('?') || alias.StartsWith('!'));
                if (sharedBlock) sharedConfiguration.AppendLine(trimmed);
                current = aliases
                    .Where(alias => !alias.Contains('*') && !alias.Contains('?') && !alias.StartsWith('!'))
                    .Select(alias => new SshHost { Alias = alias })
                    .ToList();
                hosts.AddRange(current);
                continue;
            }
            // Match 之后的条件配置不能误算到前一个 Host 下。
            if (enabled && keyword.Equals("Match", StringComparison.OrdinalIgnoreCase))
            {
                current.Clear();
                sharedBlock = true;
                sharedConfiguration.AppendLine(trimmed);
                continue;
            }
            if (enabled)
            {
                var directive = keyword + " " + value + "\n";
                foreach (var host in current) host.Configuration += directive;
                if (sharedBlock) sharedConfiguration.Append(directive);
            }
            var local = keyword.Equals("LocalForward", StringComparison.OrdinalIgnoreCase);
            var remote = keyword.Equals("RemoteForward", StringComparison.OrdinalIgnoreCase);
            if ((!local && !remote) || string.IsNullOrWhiteSpace(value)) continue;
            foreach (var host in current)
                (local ? host.LocalForwards : host.RemoteForwards)
                    .Add(new ForwardEntry { Enabled = enabled, Value = value });
        }

        foreach (var host in hosts) host.Configuration += sharedConfiguration.ToString();

        return hosts;
    }

    /// <summary>软件托管的主机别名列表（本地转发或显式常驻的反向代理）</summary>
    public static List<string> ListTunnelAliases(string configPath)
        => Parse(configPath)
            .Where(h => h.IsTunnel)
            .Select(h => h.Alias)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
