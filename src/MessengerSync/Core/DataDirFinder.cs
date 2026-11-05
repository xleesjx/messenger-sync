namespace MessengerSync.Core;

/// <summary>PC마다 다른 사용자 폴더 아래에서 쪽지(history) 폴더를 찾는다.</summary>
public static class DataDirFinder
{
    public static IEnumerable<string> Candidates()
    {
        var bases = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        }.Where(b => !string.IsNullOrEmpty(b) && Directory.Exists(b)).Distinct();

        foreach (var b in bases)
        {
            yield return Path.Combine(b, "UlsanEdu Messenger", "history");
            string[] dirs;
            try { dirs = Directory.GetDirectories(b, "*Messenger*"); }
            catch { continue; }
            foreach (var d in dirs)
                yield return Path.Combine(d, "history");
        }
    }

    /// <summary>아이디.db 가 들어 있는 history 폴더를 우선 찾고, 없으면 처음 발견한 history 폴더.</summary>
    public static string? Find(string userId)
    {
        var existing = Candidates().Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists).ToList();
        if (!string.IsNullOrWhiteSpace(userId))
        {
            var withDb = existing.FirstOrDefault(d => File.Exists(Path.Combine(d, userId.Trim() + ".db")));
            if (withDb != null) return withDb;
        }
        return existing.FirstOrDefault();
    }
}
