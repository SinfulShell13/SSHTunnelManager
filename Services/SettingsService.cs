using System.IO;
using System.Text.Json;

namespace SSHTunnelManager.Services;

/// <summary>应用设置，存于 %APPDATA%\SSHTunnelManager\settings.json（仅保留托盘/隧道所需项）</summary>
public class SettingsService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private readonly object _saveLock = new();

    public string SettingsDir { get; }
    public string SettingsPath { get; }

    public string SshExePath { get; set; } = @"C:\Windows\System32\OpenSSH\ssh.exe";
    public bool AppAutoStart { get; set; }
    public bool AutoReconnect { get; set; } = true;
    public string ConfigPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config");
    public string LogsDir { get; set; } = "";
    public int LogRetentionDays { get; set; } = 30;

    public SettingsService(string? settingsDirectory = null)
    {
        SettingsDir = settingsDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SSHTunnelManager");
        SettingsPath = Path.Combine(SettingsDir, "settings.json");
        LogsDir = Path.Combine(SettingsDir, "logs");
        Load();
        CleanOldLogs();
    }

    public void Load()
    {
        Directory.CreateDirectory(SettingsDir);
        Directory.CreateDirectory(LogsDir);

        if (!File.Exists(SettingsPath))
        {
            SshExePath = ProbeSshExe();
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            var root = doc.RootElement;
            SshExePath = GetStr(root, "SshExePath") ?? ProbeSshExe();
            AppAutoStart = GetBool(root, "AppAutoStart");
            // 旧配置缺省为 false；新装默认开启自动重连
            AutoReconnect = !root.TryGetProperty("AutoReconnect", out _) || GetBool(root, "AutoReconnect");
            ConfigPath = GetStr(root, "ConfigPath") ?? ConfigPath;
            LogsDir = GetStr(root, "LogsDir") ?? LogsDir;
            LogRetentionDays = Math.Max(1, GetInt(root, "LogRetentionDays", 30));
        }
        catch
        {
            SshExePath = ProbeSshExe();
        }
    }

    private static string? GetStr(JsonElement root, string key)
        => root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool GetBool(JsonElement root, string key)
        => root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;

    private static int GetInt(JsonElement root, string key, int def)
        => root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : def;

    public void Save()
    {
        lock (_saveLock)
        {
            Directory.CreateDirectory(SettingsDir);
            var payload = new
            {
                SshExePath,
                AppAutoStart,
                AutoReconnect,
                ConfigPath,
                LogsDir,
                LogRetentionDays,
            };
            var json = JsonSerializer.Serialize(payload, JsonOpts);
            var temporaryPath = SettingsPath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temporaryPath, json, new System.Text.UTF8Encoding(false));
                File.Move(temporaryPath, SettingsPath, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
            }
        }
    }

    public static string ProbeSshExe()
    {
        var standard = @"C:\Windows\System32\OpenSSH\ssh.exe";
        if (File.Exists(standard)) return standard;

        var fromPath = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in fromPath.Split(';'))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim().Trim('"'), "ssh.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }
        return "ssh";
    }

    public string GetLogPath(string alias)
    {
        var safe = string.Join("_", alias.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(LogsDir, $"{safe}-{DateTime.Today:yyyy-MM-dd}.log");
    }

    public void CleanOldLogs()
    {
        try
        {
            if (!Directory.Exists(LogsDir)) return;
            var cutoff = DateTime.Now.AddDays(-LogRetentionDays);
            foreach (var file in Directory.EnumerateFiles(LogsDir, "*.log"))
                if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
        }
        catch { }
    }
}
