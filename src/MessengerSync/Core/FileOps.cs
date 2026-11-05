using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace MessengerSync.Core;

public static class FileOps
{
    /// <summary>SQLite가 db 옆에 만드는 보조 파일들.</summary>
    public static readonly string[] CompanionSuffixes = { "-journal", "-wal", "-shm" };

    private static FileStream OpenRead(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20);

    public static string Sha256(string path)
    {
        using var s = OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    public static string Md5(string path)
    {
        using var s = OpenRead(path);
        return Convert.ToHexString(MD5.HashData(s)).ToLowerInvariant();
    }

    public static void Gzip(string src, string dst)
    {
        using var input = OpenRead(src);
        using var output = File.Create(dst);
        using var gz = new GZipStream(output, CompressionLevel.Optimal);
        input.CopyTo(gz, 1 << 20);
    }

    public static void Gunzip(string src, string dst)
    {
        using var input = OpenRead(src);
        using var gz = new GZipStream(input, CompressionMode.Decompress);
        using var output = File.Create(dst);
        gz.CopyTo(output, 1 << 20);
    }

    public static bool HasCompanionFiles(string db) => CompanionSuffixes.Any(s => File.Exists(db + s));

    public static void DeleteCompanionFiles(string db)
    {
        foreach (var s in CompanionSuffixes)
            if (File.Exists(db + s)) File.Delete(db + s);
    }

    /// <summary>db(와 보조 파일)를 dir 에 "접두어_시각_사유.db" 로 복사하고, 오래된 것은 keep 개만 남긴다.</summary>
    public static string? Backup(string db, string dir, string prefix, string reason, int keep)
    {
        if (!File.Exists(db)) return null;
        var stamp = $"{prefix}_{DateTime.Now:yyyyMMdd-HHmmss}";
        var dest = Path.Combine(dir, $"{stamp}_{SafeName(reason)}.db");
        for (var n = 2; File.Exists(dest); n++) // 같은 초에 또 백업하면 덮어쓰지 않고 번호를 붙인다
            dest = Path.Combine(dir, $"{stamp}-{n}_{SafeName(reason)}.db");
        File.Copy(db, dest, overwrite: false);
        foreach (var s in CompanionSuffixes)
            if (File.Exists(db + s)) File.Copy(db + s, dest + s, overwrite: true);
        Rotate(dir, prefix, keep);
        return dest;
    }

    public static void Rotate(string dir, string prefix, int keep)
    {
        if (keep <= 0) return;
        var old = Directory.GetFiles(dir, prefix + "_*.db")
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal) // 이름에 시각이 들어 있어 이름순 = 시간순
            .Skip(keep);
        foreach (var f in old)
        {
            try
            {
                File.Delete(f);
                foreach (var s in CompanionSuffixes)
                    if (File.Exists(f + s)) File.Delete(f + s);
            }
            catch (Exception ex)
            {
                Log.Warn($"오래된 백업을 지우지 못했습니다: {f} ({ex.Message})");
            }
        }
    }

    /// <summary>
    /// SQLite 손상 검사. 강제 종료 등으로 -journal 이 남아 있으면, 여는 순간 SQLite가 자동 복구(롤백)한다.
    /// </summary>
    public static (bool Ok, string Message) CheckSqlite(string db, bool full)
    {
        try
        {
            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = db,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false, // 검사 후 파일 잠금이 남지 않도록
            }.ToString();
            var results = new List<string>();
            using (var conn = new SqliteConnection(cs))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = full ? "PRAGMA integrity_check(20);" : "PRAGMA quick_check(20);";
                cmd.CommandTimeout = 600;
                using var r = cmd.ExecuteReader();
                while (r.Read()) results.Add(r.GetString(0));
            }
            var ok = results.Count == 1 && results[0] == "ok";
            return (ok, ok ? "ok" : string.Join(" / ", results));
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public static bool IsFileFree(string path)
    {
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static async Task<bool> WaitFileFreeAsync(string path, TimeSpan timeout, CancellationToken ct)
    {
        var until = DateTime.UtcNow + timeout;
        while (true)
        {
            if (!File.Exists(path) || IsFileFree(path)) return true;
            if (DateTime.UtcNow > until) return false;
            await Task.Delay(500, ct);
        }
    }

    public static string SafeName(string s)
    {
        var bad = Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|', ' ' }).ToHashSet();
        var clean = new string(s.Select(c => bad.Contains(c) ? '-' : c).ToArray()).Trim('-');
        return clean.Length == 0 ? "PC" : clean;
    }

    public static string Human(long bytes)
    {
        if (bytes < 1024) return $"{bytes}B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#}KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024:0.#}MB";
        return $"{bytes / 1024.0 / 1024 / 1024:0.##}GB";
    }

    public static string When(DateTime? utc)
    {
        if (utc == null) return "-";
        var t = utc.Value.ToLocalTime();
        var ko = System.Globalization.CultureInfo.GetCultureInfo("ko-KR");
        return t.ToString("MM/dd(ddd) HH:mm", ko);
    }
}
