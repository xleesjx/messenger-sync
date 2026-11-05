using System.Diagnostics;
using System.Reflection;
using MessengerSync.Core;

namespace MessengerSync.Platform;

public sealed record UpdateInfo(Version Version, string Tag, string Notes);

/// <summary>
/// GitHub 릴리스에서 새 버전을 확인하고 exe를 교체한다.
///  - 확인: github.com/{저장소}/releases/latest 가 가리키는 태그(v0.1.5 등)를 읽는다 (API 사용 한도와 무관)
///  - 받기: 릴리스의 MessengerSync.exe 와 MessengerSync.exe.sha256 을 받아 해시값을 확인한다
///  - 교체: 실행 중인 exe는 덮어쓸 수 없지만 이름은 바꿀 수 있으므로, 지금 exe → .old, 새 exe → 원래 이름
/// </summary>
public static class Updater
{
    public const string Repo = "xleesjx/messenger-sync";
    private const string AssetName = "MessengerSync.exe";

    public static Version Current
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    private static HttpClient NewClient(bool followRedirects)
    {
        var c = new HttpClient(new HttpClientHandler { AllowAutoRedirect = followRedirects }) { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"MessengerSync-Updater/{Current}");
        return c;
    }

    /// <summary>새 버전이 있으면 정보를, 없으면 null.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct)
    {
        using var c = NewClient(followRedirects: false);
        using var r = await c.GetAsync($"https://github.com/{Repo}/releases/latest", ct);
        var loc = r.Headers.Location?.ToString();
        if (string.IsNullOrEmpty(loc) || !loc.Contains("/releases/tag/"))
        {
            Log.Info($"업데이트 확인: 릴리스 없음 ({(int)r.StatusCode})");
            return null;
        }
        var tag = Uri.UnescapeDataString(loc[(loc.LastIndexOf('/') + 1)..]);
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest))
        {
            Log.Warn($"업데이트 확인: 버전 형식을 읽지 못함 ({tag})");
            return null;
        }
        latest = new Version(latest.Major, latest.Minor, Math.Max(0, latest.Build));
        Log.Info($"업데이트 확인: 최신 {latest}, 현재 {Current}");
        if (latest <= Current) return null;
        return new UpdateInfo(latest, tag, await TryReadNotesAsync(tag, ct));
    }

    /// <summary>저장소의 CHANGELOG.md 에서 그 버전 부분만 읽는다.</summary>
    private static async Task<string> TryReadNotesAsync(string tag, CancellationToken ct)
    {
        try
        {
            using var c = NewClient(followRedirects: true);
            var text = await c.GetStringAsync($"https://raw.githubusercontent.com/{Repo}/{tag}/CHANGELOG.md", ct);
            var lines = text.Replace("\r", "").Split('\n');
            var start = Array.FindIndex(lines, l => l.StartsWith("## ") && l.Contains(tag));
            if (start < 0) return "";
            var end = Array.FindIndex(lines, start + 1, l => l.StartsWith("## "));
            var body = lines.Skip(start + 1).Take((end < 0 ? lines.Length : end) - start - 1);
            return string.Join("\n", body).Trim();
        }
        catch (Exception ex)
        {
            Log.Warn($"변경 내용을 읽지 못했습니다: {ex.Message}");
            return "";
        }
    }

    /// <summary>새 exe를 받아 확인한 뒤 지금 exe와 바꾼다. 실패하면 원래대로 둔다.</summary>
    public static async Task InstallAsync(UpdateInfo u, IProgress<(double Percent, string Text)> progress, CancellationToken ct)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("실행 파일 위치를 알 수 없습니다.");
        var newPath = exe + ".new";
        var oldPath = exe + ".old";
        var baseUrl = $"https://github.com/{Repo}/releases/download/{u.Tag}/";

        using var c = NewClient(followRedirects: true);

        // 1) 다운로드
        using (var resp = await c.GetAsync(baseUrl + AssetName, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? 0;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(newPath);
            var buffer = new byte[1 << 20];
            long done = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total > 0) progress.Report(((double)done / total, $"받는 중... {FileOps.Human(done)} / {FileOps.Human(total)}"));
            }
        }

        // 2) 해시값 확인 (받다가 끊기거나 바뀐 파일을 쓰지 않도록)
        progress.Report((1, "받은 파일을 확인하는 중..."));
        var expected = (await c.GetStringAsync(baseUrl + AssetName + ".sha256", ct)).Trim().Split(' ', '\t', '\n')[0].ToLowerInvariant();
        var actual = await Task.Run(() => FileOps.Sha256(newPath), ct);
        if (expected.Length != 64 || expected != actual)
        {
            File.Delete(newPath);
            throw new IOException($"받은 파일의 해시값이 맞지 않습니다. 업데이트를 취소했습니다.\n(기대 {expected[..Math.Min(12, expected.Length)]}…, 실제 {actual[..12]}…)");
        }

        // 3) 교체
        progress.Report((1, "프로그램 파일을 바꾸는 중..."));
        if (File.Exists(oldPath)) File.Delete(oldPath);
        File.Move(exe, oldPath);
        try
        {
            File.Move(newPath, exe);
        }
        catch
        {
            File.Move(oldPath, exe); // 원래대로
            throw;
        }
        Log.Info($"업데이트 설치: {Current} → {u.Version}");
    }

    /// <summary>새 exe를 실행한다. 새 프로그램은 지금 프로그램이 끝날 때까지 기다렸다가 시작한다.</summary>
    public static void Restart(bool launchMessenger)
    {
        var exe = Environment.ProcessPath!;
        var args = $"--wait-pid {Environment.ProcessId} --updated" + (launchMessenger ? " --launch" : "");
        Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) ?? "" })?.Dispose();
    }

    /// <summary>지난 업데이트 때 남은 .old 파일을 지운다.</summary>
    public static void CleanupOld()
    {
        try
        {
            var old = (Environment.ProcessPath ?? "") + ".old";
            if (File.Exists(old)) File.Delete(old);
            var partial = (Environment.ProcessPath ?? "") + ".new";
            if (File.Exists(partial)) File.Delete(partial);
        }
        catch
        {
            // 다음 실행 때 다시 시도
        }
    }
}
