using Microsoft.Win32;

namespace MessengerSync.UI;

/// <summary>윈도우 로그인 시 자동 실행 (HKCU\...\Run).</summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "MessengerSync";

    private static string Command => $"\"{Environment.ProcessPath}\" --startup";

    public static bool IsEnabled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(Name) is string v && v.Contains(Path.GetFileName(Environment.ProcessPath ?? "MessengerSync.exe"),
            StringComparison.OrdinalIgnoreCase);
    }

    public static void Set(bool enabled)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) k.SetValue(Name, Command);
        else if (k.GetValue(Name) != null) k.DeleteValue(Name);
    }
}
