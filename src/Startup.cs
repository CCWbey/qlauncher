using Microsoft.Win32;

namespace QLaunch;

internal static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "QLaunch";

    /// <summary>
    /// 写入或清除当前用户的开机自启注册表项。
    /// </summary>
    /// <param name="enable">true 注册自启，false 移除。</param>
    /// <returns>无返回值。</returns>
    public static void Register(bool enable)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKey);
        if (key is null)
        {
            return;
        }
        if (enable)
        {
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --minimized");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
