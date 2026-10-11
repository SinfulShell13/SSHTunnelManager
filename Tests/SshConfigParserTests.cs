using System.Text;
using SSHTunnelManager.Services;
using Xunit;

namespace SSHTunnelManager.Tests;

public sealed class SshConfigParserTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "SSHTunnelManagerTests-" + Guid.NewGuid().ToString("N"));

    public SshConfigParserTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void Parse_FindsTunnelHostWithEnabledLocalForward()
    {
        var path = Path.Combine(_directory, "config");
        File.WriteAllText(path,
            "Host jump\n" +
            "  HostName example.invalid\n" +
            "  LocalForward 2222 127.0.0.1:40529\n" +
            "\n" +
            "Host nas\n" +
            "  HostName 100.1.1.1\n",
            new UTF8Encoding(false));

        var aliases = SshConfigParser.ListTunnelAliases(path);

        Assert.Equal(new[] { "jump" }, aliases);
    }

    [Fact]
    public void Parse_IgnoresCommentedLocalForward()
    {
        var path = Path.Combine(_directory, "config");
        File.WriteAllText(path,
            "Host demo\n" +
            "  #LocalForward 2222 127.0.0.1:40529\n",
            new UTF8Encoding(false));

        Assert.Empty(SshConfigParser.ListTunnelAliases(path));
    }

    [Fact]
    public void Parse_MissingFile_ReturnsEmpty()
    {
        var path = Path.Combine(_directory, "no-such-config");
        Assert.Empty(SshConfigParser.Parse(path));
        Assert.Empty(SshConfigParser.ListTunnelAliases(path));
    }

    [Fact]
    public void Parse_SkipsWildcardHosts()
    {
        var path = Path.Combine(_directory, "config");
        File.WriteAllText(path,
            "Host *\n" +
            "  LocalForward 9 127.0.0.1:9\n" +
            "Host real\n" +
            "  LocalForward 2222 127.0.0.1:22\n",
            new UTF8Encoding(false));

        Assert.Equal(new[] { "real" }, SshConfigParser.ListTunnelAliases(path));
    }

    [Fact]
    public void ListenPort_ParsesBindPrefixAndPlainPort()
    {
        Assert.Equal(2222, new SSHTunnelManager.Models.ForwardEntry { Value = "2222 127.0.0.1:40529" }.ListenPort);
        Assert.Equal(2223, new SSHTunnelManager.Models.ForwardEntry { Value = "127.0.0.1:2223 172.17.0.2:22" }.ListenPort);
        Assert.Equal(0, new SSHTunnelManager.Models.ForwardEntry { Value = "" }.ListenPort);
        Assert.Equal(2224, new SSHTunnelManager.Models.ForwardEntry { Value = "[::1]:2224\tlocalhost:22" }.ListenPort);
    }

    [Fact]
    public void Parse_MarkedRemoteProxy_IsManagedButHasNoLocalListenPorts()
    {
        var hosts = SshConfigParser.ParseText("""
            Host proxy
              # SSHTunnelManager: persistent-proxy
              RemoteForward 17899 127.0.0.1:7897
            Host disabled
              # SSHTunnelManager: persistent-proxy
              #RemoteForward 17899 127.0.0.1:7897
            Host login
              HostName example.invalid
            """);

        Assert.True(hosts[0].IsTunnel);
        Assert.Empty(hosts[0].EnabledListenPorts);
        Assert.Equal("17899 127.0.0.1:7897", Assert.Single(hosts[0].RemoteForwards).Value);
        Assert.False(hosts[1].IsTunnel);
        Assert.False(Assert.Single(hosts[1].RemoteForwards).Enabled);
        Assert.False(hosts[2].IsTunnel);
    }

    [Fact]
    public void Parse_KeepsLocalAndRemoteHostsIndependent()
    {
        var before = """
            Host jump
              LocalForward 2222 localhost:22
            Host proxy
              # SSHTunnelManager: persistent-proxy
              RemoteForward 17899 localhost:7897
            Host *
              ServerAliveInterval 30
            """;
        var after = before.Replace("17899", "17890");

        var oldHosts = SshConfigParser.ParseText(before);
        var newHosts = SshConfigParser.ParseText(after);
        Assert.Equal(oldHosts[0].Configuration, newHosts[0].Configuration);
        Assert.NotEqual(oldHosts[1].Configuration, newHosts[1].Configuration);
        Assert.All(newHosts, host => Assert.Contains("ServerAliveInterval 30", host.Configuration));
    }

    [Fact]
    public void Parse_DirectiveBoundariesAliasesAndMatch_AreRespected()
    {
        var hosts = SshConfigParser.ParseText("""
            Host = first second wildcard? !excluded
              # SSHTunnelManager: persistent-proxy
              RemoteForward=17899 localhost:7897 # comment
            Host typo
              LocalForwardExtra 2222 localhost:22
            Host conditional
            Match host something
              LocalForward 2223 localhost:22
            """);
        Assert.Equal(new[] { "first", "second" }, hosts.Where(host => host.IsTunnel).Select(host => host.Alias));
        Assert.Equal("17899 localhost:7897", hosts[0].RemoteForwards[0].Value);
    }

    [Fact]
    public void Parse_OrdinaryRemoteForward_IsNotManagedRegardlessOfAlias()
    {
        var path = Path.Combine(_directory, "config");
        File.WriteAllText(path, """
            Host login
              RemoteForward 17890 localhost:7897
            Host server-proxy
              RemoteForward 17899 localhost:7897
            """);

        Assert.Empty(SshConfigParser.ListTunnelAliases(path));
        Assert.All(SshConfigParser.Parse(path), host =>
        {
            Assert.False(host.PersistentProxy);
            Assert.False(host.IsTunnel);
            Assert.True(Assert.Single(host.RemoteForwards).Enabled);
        });
    }

    [Fact]
    public void Parse_ProxyMarker_IsScopedToExplicitHostBlock()
    {
        var hosts = SshConfigParser.ParseText("""
            # SSHTunnelManager: persistent-proxy
            Host ordinary
              RemoteForward 17890 localhost:7897
            Host marked
              RemoteForward 17899 localhost:7897
              #SSHTunnelManager : PERSISTENT-PROXY
            Host next
              RemoteForward 17891 localhost:7897
            Host *
              # SSHTunnelManager: persistent-proxy
            Match host next
              # SSHTunnelManager: persistent-proxy
            """);

        Assert.Equal(new[] { "marked" }, hosts.Where(host => host.IsTunnel).Select(host => host.Alias));
        Assert.False(hosts[0].PersistentProxy);
        Assert.False(hosts[2].PersistentProxy);
    }

    [Fact]
    public void Parse_RemovingProxyMarker_DisablesManagementAndChangesConfiguration()
    {
        var text = "Host proxy\n # SSHTunnelManager: persistent-proxy\n RemoteForward 17899 localhost:7897\n";
        var before = Assert.Single(SshConfigParser.ParseText(text));
        var after = Assert.Single(SshConfigParser.ParseText(text.Replace("# SSHTunnelManager: persistent-proxy", "# ordinary comment")));

        Assert.True(before.IsTunnel);
        Assert.False(after.IsTunnel);
        Assert.NotEqual(before.Configuration, after.Configuration);
        Assert.True(Assert.Single(after.RemoteForwards).Enabled);
    }

    [Fact]
    public void Parse_ProxyMarkerWithoutEnabledForward_DoesNotStartLoginHost()
    {
        var hosts = SshConfigParser.ParseText("""
            Host login
              # SSHTunnelManager: persistent-proxy
              HostName example.invalid
            Host disabled
              # SSHTunnelManager: persistent-proxy
              # RemoteForward 17899 localhost:7897
            """);

        Assert.All(hosts, host => Assert.False(host.IsTunnel));
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }
}
