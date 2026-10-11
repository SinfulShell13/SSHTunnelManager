using Microsoft.Win32;

namespace SSHTunnelManager.Services;

/// <summary>程序开机自启（HKCU Run 注册表键）</summary>
public static class AutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SSHTunnelManager";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) != null;
        }
        catch { return false; }
    }

    /// <summary>静默启动参数：开机自启时带上，程序不弹主窗口，仅驻留托盘</summary>
    public const string SilentArg = "--startup";

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (enabled)
            {
                var exe = Environment.ProcessPath ?? "";
                key.SetValue(ValueName, "\"" + exe + "\" " + SilentArg);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch { /* 注册表写入失败时静默 */ }
    }

    /// <summary>
    /// 升级旧版本写入的自启项：老版本命令只有 exe 路径（无 --startup），
    /// 会在开机时弹出主窗口。检测到就静默补上参数，用户无需手动关/开开关。
    /// 返回是否发生了升级——发生则说明本次就是"旧式自启拉起"，调用方应让本次也静默。
    /// </summary>
    public static bool EnsureSilentArg()
    {
        if (!IsEnabled()) return false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            var val = key?.GetValue(ValueName) as string ?? "";
            if (!val.Contains(SilentArg, StringComparison.OrdinalIgnoreCase))
            {
                key?.SetValue(ValueName, val.TrimEnd() + " " + SilentArg);
                return true;  // 已升级：本次即旧式自启，应静默
            }
        }
        catch { /* 注册表写入失败时静默 */ }
        return false;
    }
}
