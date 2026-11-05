namespace MessengerSync.Core;

/// <summary>이 PC에만 저장되는 파일들의 위치 (%LOCALAPPDATA%\MessengerSync).</summary>
public static class Paths
{
    public static string Root { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MessengerSync");

    public static string SettingsFile => Path.Combine(Ensure(Root), "settings.json");
    public static string StateFile(string userId) => Path.Combine(Ensure(Root), $"state-{userId}.json");
    public static string TokenDir => Ensure(Path.Combine(Root, "token"));
    public static string BackupDir => Ensure(Path.Combine(Root, "backup"));
    public static string ConflictDir => Ensure(Path.Combine(Root, "conflicts"));
    public static string LogDir => Ensure(Path.Combine(Root, "logs"));
    public static string TempDir => Ensure(Path.Combine(Root, "temp"));

    public static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}
