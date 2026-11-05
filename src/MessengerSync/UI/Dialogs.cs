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
        foreach (var b in buttons)
        {
            var parts = b.Split('|', 2); // "제목|설명" 이면 큰 버튼(명령 링크)
            TaskDialogButton btn = parts.Length == 2
                ? new TaskDialogCommandLinkButton(parts[0], parts[1])
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

    public Task<bool> AskMessengerAlreadyRunningAsync()
    {
        var r = Dialogs.Ask("메신저가 동기화 전에 켜져 있습니다",
            "동기화를 거치지 않고 메신저가 먼저 실행되었습니다.\n" +
            "다른 PC에서 올린 최신 쪽지를 받으려면 메신저를 껐다가 동기화한 뒤 다시 켜야 합니다.",
            TaskDialogIcon.Warning,
            "끄고 동기화한 뒤 다시 켜기 (추천)|메신저를 종료 → 최신 데이터 확인 → 다시 실행",
            "그대로 사용|이번에는 동기화하지 않습니다. 나중에 충돌로 처리될 수 있습니다.");
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

    public Task<bool> ConfirmLockOverrideAsync(LockInfo other)
    {
        var r = Dialogs.Ask($"{other.PcName} PC에서 사용 중으로 표시되어 있습니다",
            $"{other.PcName} PC가 {FileOps.When(other.SinceUtc)}부터 메신저를 쓰고 있다고 표시되어 있습니다.\n" +
            "그 PC의 업로드가 아직 안 됐을 수 있습니다. 그 PC에서 Win+L(잠금)이나 메신저 종료를 먼저 해 주세요.\n\n" +
            "이미 그 PC를 끄고 왔다면 계속해도 됩니다. 나중에 그 PC 내용과 충돌하면 어느 쪽을 쓸지 묻습니다.",
            TaskDialogIcon.Warning, "계속", "취소");
        return Task.FromResult(r == 0);
    }

    public Task<DownloadChoice> AskDownloadAsync(CloudState cloud, LocalInfo local)
    {
        var r = Dialogs.Ask("최신 쪽지 데이터가 있습니다",
            $"클라우드: {Cloud(cloud)}\n이 PC: v{local.BaseVersion} 기준 · {Local(local)}",
            TaskDialogIcon.Information,
            "받고 시작 (추천)|이 PC 파일은 백업한 뒤 덮어씁니다",
            "받지 않고 시작|다음 업로드 때 충돌로 처리됩니다",
            "취소");
        return Task.FromResult(r switch { 0 => DownloadChoice.Download, 1 => DownloadChoice.Skip, _ => DownloadChoice.Cancel });
    }

    public Task<ConflictChoice> AskConflictAsync(CloudState cloud, LocalInfo local, bool firstRun)
    {
        var heading = firstRun ? "이 PC에서 처음 동기화합니다" : "두 곳 모두 쪽지가 바뀌었습니다";
        var text = firstRun
            ? $"클라우드에 다른 PC가 올린 쪽지가 있습니다.\n\n클라우드: {Cloud(cloud)}\n이 PC: {Local(local)}\n\n어느 쪽을 쓸까요? 선택하지 않은 쪽은 보관 폴더(conflicts)에 남습니다."
            : $"다른 PC와 이 PC에서 각각 쪽지가 바뀌어, 한쪽을 골라야 합니다. (내용을 합치지는 못합니다)\n\n클라우드: {Cloud(cloud)}\n이 PC: v{local.BaseVersion} 이후 변경 · {Local(local)}\n\n선택하지 않은 쪽은 보관 폴더(conflicts)에 남습니다.";
        var r = Dialogs.Ask(heading, text, TaskDialogIcon.Warning,
            $"클라우드 사용 ({cloud.PcName})|클라우드 내용으로 덮어씁니다",
            "이 PC 사용|이 PC 내용을 새 버전으로 올립니다",
            "취소 (메신저 실행 안 함)");
        return Task.FromResult(r switch { 0 => ConflictChoice.UseCloud, 1 => ConflictChoice.UseLocal, _ => ConflictChoice.Cancel });
    }

    public Task<bool> ConfirmAsync(string heading, string text)
        => Task.FromResult(Dialogs.Ask(heading, text, TaskDialogIcon.Warning, "예", "아니오") == 0);

    public void Notify(string title, string text, bool error = false) => _balloon(title, text, error);
}
