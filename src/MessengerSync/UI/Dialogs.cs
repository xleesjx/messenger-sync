using MessengerSync.Core;

namespace MessengerSync.UI;

/// <summary>버튼 문구를 바꿀 수 있는 윈도우 기본 대화상자(TaskDialog) 도우미. 항상 맨 앞에 뜬다.</summary>
public static class Dialogs
{
    public const string Caption = "메신저 동기화";

    public static int Ask(string heading, string text, TaskDialogIcon icon, params string[] buttons)
    {
        // 묻는 창이 다른 창 뒤에 숨지 않도록: 작업 표시줄에 보이는 투명한 주인 창을 만들고 맨 앞으로 가져온다.
        using var owner = new Form
        {
            Text = Caption,
            Icon = IconFactory.Get(TrayState.Normal),
            TopMost = true,
            ShowInTaskbar = true,
            StartPosition = FormStartPosition.CenterScreen,
            Size = new Size(1, 1),
            FormBorderStyle = FormBorderStyle.None,
            Opacity = 0,
        };
        owner.Show();
        ForceForeground(owner.Handle);

        var page = new TaskDialogPage
        {
            Caption = Caption,
            Heading = heading,
            Text = text,
            Icon = icon,
            AllowCancel = true,
        };
        var list = new List<TaskDialogButton>();
        // 윈도우 대화상자는 큰 버튼(명령 링크)과 일반 버튼을 한 창에 섞을 수 없다.
        // 설명이 붙은 버튼이 하나라도 있으면 모든 버튼을 큰 버튼으로 만든다.
        var useLinks = buttons.Any(b => b.Contains('|'));
        foreach (var b in buttons)
        {
            var parts = b.Split('|', 2); // "제목|설명" 이면 큰 버튼(명령 링크)
            TaskDialogButton btn = useLinks
                ? new TaskDialogCommandLinkButton(parts[0], parts.Length == 2 ? parts[1] : null)
                : new TaskDialogButton(parts[0]);
            list.Add(btn);
            page.Buttons.Add(btn);
        }
        if (list.Count > 0) page.DefaultButton = list[0];

        var result = TaskDialog.ShowDialog(owner, page, TaskDialogStartupLocation.CenterScreen);
        var idx = list.IndexOf(result);
        return idx; // 창을 그냥 닫으면 -1
    }

    private static void ForceForeground(IntPtr hwnd)
    {
        try
        {
            // 윈도우는 백그라운드 프로그램이 앞으로 나오는 것을 막으므로, Alt 키 입력을 흉내 내서 허용받는다.
            keybd_event(0x12, 0, 0, UIntPtr.Zero);
            keybd_event(0x12, 0, 2, UIntPtr.Zero);
            SetForegroundWindow(hwnd);
            FlashWindow(hwnd, true);
        }
        catch
        {
            // 실패해도 작업 표시줄에서 찾을 수 있다
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool FlashWindow(IntPtr hWnd, bool invert);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    public static void Info(string heading, string text) => Ask(heading, text, TaskDialogIcon.Information, "확인");
    public static void Error(string heading, string text) => Ask(heading, text, TaskDialogIcon.Error, "확인");
}

/// <summary>동기화 엔진이 묻는 질문들을 실제 창으로 보여준다.</summary>
public sealed class WinUi : IUserInteraction
{
    private readonly Action<string, string, bool> _balloon;

    public WinUi(Action<string, string, bool> balloon) => _balloon = balloon;

    private static string Local(LocalInfo l)
        => l.Exists ? $"{FileOps.When(l.ModifiedLocal?.ToUniversalTime())} 수정 · {FileOps.Human(l.Size)}" : "파일 없음";

    private static string Cloud(CloudState c)
        => $"{c.PcName} · {FileOps.When(c.UploadedUtc)} 업로드 · v{c.Version} · {FileOps.Human(c.Size)}";

    private const string Newest = "   ◀ 가장 최근";

    /// <summary>시각 비교표: (이름, 시각, 설명) 줄들을 만들고 가장 최근 줄에 표시를 붙인다.</summary>
    private static string Timeline(params (string Label, DateTime? Utc, string Note)[] rows)
    {
        var newest = rows.Where(r => r.Utc != null).Select(r => r.Utc!.Value).DefaultIfEmpty().Max();
        var lines = rows.Select(r =>
            $"■ {r.Label}\n      {(r.Utc == null ? "-" : FileOps.When(r.Utc))}  {r.Note}" +
            (r.Utc != null && r.Utc == newest && rows.Count(x => x.Utc != null) > 1 ? Newest : ""));
        return string.Join("\n", lines);
    }

    public Task<bool> AskMessengerAlreadyRunningAsync()
    {
        var r = Dialogs.Ask("⚠ 메신저가 동기화 전에 먼저 켜졌습니다",
            "메신저 동기화를 거치지 않고 메신저가 실행되었습니다.\n" +
            "다른 PC에서 쓴 최근 쪽지가 이 PC에 없을 수 있습니다.\n\n" +
            "그대로 쓰면 나중에 두 PC 중 한쪽 쪽지만 고를 수 있습니다 (합치기 불가).",
            TaskDialogIcon.ShieldWarningYellowBar,
            "끄고 동기화한 뒤 다시 켜기 (추천)|메신저를 종료 → 최신 데이터 확인 → 다시 실행 (10초 정도)",
            "그대로 사용|이번에는 동기화하지 않습니다");
        return Task.FromResult(r == 0);
    }

    public Task<LoginChoice> AskLoginAsync(bool expired)
    {
        var r = Dialogs.Ask(expired ? "구글 로그인이 만료되었습니다" : "구글 로그인이 필요합니다",
            "브라우저가 열리면 학교 구글 계정으로 로그인한 뒤 '허용'을 눌러 주세요.\n" +
            "(테스트 모드 앱이라 7일마다 다시 로그인해야 합니다.)",
            TaskDialogIcon.Information,
            "지금 로그인|브라우저에서 구글 로그인",
            "동기화 없이 메신저만 실행|이번에는 받기/올리기를 하지 않습니다",
            "취소");
        return Task.FromResult(r switch { 0 => LoginChoice.Login, 1 => LoginChoice.SkipSync, _ => LoginChoice.Cancel });
    }

    public Task<bool> AskRenewLoginAsync(int days)
    {
        var r = Dialogs.Ask("구글 로그인 갱신 시기입니다",
            $"로그인한 지 {days}일이 지났습니다. 7일이 지나면 자동으로 풀립니다.\n" +
            "자리를 비운 사이 업로드가 실패하지 않도록 지금 갱신하는 것을 추천합니다. (10초 정도)",
            TaskDialogIcon.Information, "지금 갱신", "나중에");
        return Task.FromResult(r == 0);
    }

    public Task<bool> AskLaunchOfflineAsync(string error)
    {
        var r = Dialogs.Ask("클라우드를 확인할 수 없습니다",
            $"인터넷 연결이나 구글 드라이브 상태를 확인해 주세요.\n\n오류: {error}\n\n" +
            "그래도 메신저를 실행하면, 다른 PC의 최신 쪽지가 반영되지 않은 상태로 시작합니다.",
            TaskDialogIcon.Warning, "그래도 메신저 실행", "취소");
        return Task.FromResult(r == 0);
    }

    private static string Ago(DateTime utc)
    {
        var d = DateTime.UtcNow - utc;
        if (d < TimeSpan.FromMinutes(1)) return "방금";
        if (d < TimeSpan.FromHours(1)) return $"{(int)d.TotalMinutes}분 전";
        if (d < TimeSpan.FromDays(1)) return $"{(int)d.TotalHours}시간 {d.Minutes}분 전";
        return $"{(int)d.TotalDays}일 전";
    }

    public Task<LockChoice> AskLockAsync(LockInfo other, CloudState? cloud, LocalInfo local, bool noResponse)
    {
        var pc = other.PcName;
        // 메신저를 쓰는 동안 10분마다 신호를 남기므로, 30분 안에 신호가 있었으면 그 PC가 켜져 있을 가능성이 높다
        var fresh = other.LastSeenUtc != null && DateTime.UtcNow - other.LastSeenUtc.Value < TimeSpan.FromMinutes(30);
        var table = Timeline(
            ($"{pc} PC 메신저 (동기화 안 하고 켜져 있음)", other.LastSignalUtc,
                $"마지막 신호 ({Ago(other.LastSignalUtc)}) · {FileOps.When(other.SinceUtc)}부터 사용"),
            ("클라우드 최신 업로드", cloud?.UploadedUtc, cloud == null ? "없음" : $"{cloud.PcName} · v{cloud.Version}"),
            ("이 PC 마지막 동기화", local.LastSyncUtc, $"v{local.BaseVersion}"));

        var risk = $"지금 그냥 시작하면 {pc} PC에서 {FileOps.When(other.SinceUtc)} 이후 주고받은 쪽지가 빠진 채로 시작합니다.\n" +
                   $"나중에 {pc} PC가 동기화할 때 두 PC 중 한쪽 쪽지만 고를 수 있습니다 (합치기 불가).";
        string heading, guide;
        if (noResponse)
        {
            heading = $"⚠ {pc} PC가 응답하지 않습니다";
            guide = $"업로드를 요청했지만 3분 동안 응답이 없었습니다. {pc} PC가 꺼졌거나 절전 상태일 수 있습니다.";
        }
        else
        {
            heading = $"⚠ {pc} PC의 최근 쪽지가 아직 올라오지 않았습니다";
            guide = fresh
                ? $"{pc} PC가 켜져 있는 것 같습니다. [업로드 요청]을 누르면 그 PC가 1분 안에 메신저를 끄고 쪽지를 올립니다."
                : $"{pc} PC에서 한동안 신호가 없었습니다 (꺼졌거나 절전 상태일 수 있음). 켜져 있다면 [업로드 요청]으로 받을 수 있습니다.";
        }
        var text = $"{table}\n\n{guide}\n\n{risk}";

        var request = $"{pc} PC에 업로드 요청 (추천)|그 PC가 켜져 있으면 메신저를 끄고 올립니다. 올라오면 바로 받아서 시작합니다 (최대 3분)";
        var cancel = "취소|이 PC에서는 메신저를 켜지 않습니다. 그 PC에서 Ctrl+Alt+Shift+L 로 동기화한 뒤 다시 시도하세요";
        var go = $"위험을 알고 그냥 계속|{pc} PC의 최근 쪽지 없이 시작합니다";
        var r = Dialogs.Ask(heading, text, TaskDialogIcon.ShieldErrorRedBar, request, cancel, go);
        if (r == 0) return Task.FromResult(LockChoice.RequestUpload);
        if (r != 2) return Task.FromResult(LockChoice.Cancel);

        // 실수로 누르지 않도록 한 번 더 확인
        var sure = Dialogs.Ask("정말 그냥 계속할까요?",
            $"{risk}\n\n{pc} PC 쪽지는 사라지지 않고 그 PC에 남아 있지만, 두 PC 내용을 합칠 수는 없습니다.",
            TaskDialogIcon.ShieldErrorRedBar,
            "취소 (추천)|돌아가서 업로드 요청이나 다른 방법을 고릅니다",
            $"계속|{pc} PC의 최근 쪽지 없이 메신저를 켭니다");
        if (sure == 1) return Task.FromResult(LockChoice.Continue);
        return AskLockAsync(other, cloud, local, noResponse);
    }

    public Task<DownloadChoice> AskDownloadAsync(CloudState cloud, LocalInfo local)
    {
        var table = Timeline(
            ($"클라우드 최신 업로드", cloud.UploadedUtc, $"{cloud.PcName} · v{cloud.Version}"),
            ("이 PC 마지막 동기화", local.LastSyncUtc, $"v{local.BaseVersion}"));
        var r = Dialogs.Ask($"{cloud.PcName} PC에서 올린 최신 쪽지가 있습니다",
            $"{table}\n\n받으면 이 PC 쪽지 파일은 백업한 뒤 최신 내용으로 바뀝니다.",
            TaskDialogIcon.ShieldBlueBar,
            "받고 시작 (추천)|최신 쪽지를 받은 뒤 메신저를 켭니다",
            "취소|메신저를 켜지 않습니다",
            "받지 않고 시작|최근 쪽지 없이 시작합니다. 다음 업로드 때 충돌로 처리됩니다");
        return Task.FromResult(r switch { 0 => DownloadChoice.Download, 2 => DownloadChoice.Skip, _ => DownloadChoice.Cancel });
    }

    public Task<ConflictChoice> AskConflictAsync(CloudState cloud, LocalInfo local, bool firstRun)
    {
        var localUtc = local.ModifiedLocal?.ToUniversalTime();
        var cloudNewer = localUtc == null || cloud.UploadedUtc >= localUtc;
        var table = Timeline(
            ($"클라우드 ({cloud.PcName} PC가 올림)", cloud.UploadedUtc, $"v{cloud.Version} · {FileOps.Human(cloud.Size)}"),
            ("이 PC 쪽지 파일", localUtc, firstRun ? $"{FileOps.Human(local.Size)}" : $"v{local.BaseVersion} 이후 바뀜 · {FileOps.Human(local.Size)}"));
        var heading = firstRun ? "이 PC에서 처음 동기화합니다" : "⚠ 두 PC 모두 쪽지가 바뀌었습니다";
        var intro = firstRun
            ? "클라우드에 다른 PC가 올린 쪽지가 있습니다. 어느 쪽을 쓸지 골라 주세요."
            : "동기화하지 않은 채 두 PC에서 각각 쪽지를 주고받았습니다. 내용을 합칠 수 없어 한쪽을 골라야 합니다.";
        var useCloud = $"클라우드 사용 ({cloud.PcName}){(cloudNewer ? " - 더 최근" : "")}|클라우드 내용으로 바꿉니다";
        var useLocal = $"이 PC 사용{(!cloudNewer ? " - 더 최근" : "")}|이 PC 내용을 새 버전으로 올립니다";
        var buttons = cloudNewer ? new[] { useCloud, useLocal, "취소 (메신저 실행 안 함)" } : new[] { useLocal, useCloud, "취소 (메신저 실행 안 함)" };
        var r = Dialogs.Ask(heading, $"{intro}\n\n{table}\n\n고르지 않은 쪽은 보관 폴더(conflicts)에 남습니다.",
            firstRun ? TaskDialogIcon.ShieldBlueBar : TaskDialogIcon.ShieldErrorRedBar, buttons);
        if (r < 0 || r == 2) return Task.FromResult(ConflictChoice.Cancel);
        return Task.FromResult(buttons[r] == useCloud ? ConflictChoice.UseCloud : ConflictChoice.UseLocal);
    }

    public Task<bool> ConfirmAsync(string heading, string text)
        => Task.FromResult(Dialogs.Ask(heading, text, TaskDialogIcon.Warning, "예", "아니오") == 0);

    public void Notify(string title, string text, bool error = false) => _balloon(title, text, error);
}
