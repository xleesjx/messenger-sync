namespace MessengerSync.Core;

public enum CloseResult
{
    NotRunning,
    /// <summary>0단계: 메신저 창 메뉴의 '종료'를 대신 눌러 정상 종료.</summary>
    GracefulMenu,
    /// <summary>1단계: 윈도우 종료 신호로 정상 종료.</summary>
    GracefulSession,
    /// <summary>2단계: 창 닫기 신호로 정상 종료.</summary>
    GracefulClose,
    /// <summary>3단계: 강제 종료.</summary>
    Killed,
    Failed,
}

public sealed record CloseReport(CloseResult Result, double Seconds, string Detail = "")
{
    public bool Closed => Result is CloseResult.NotRunning or CloseResult.GracefulMenu or CloseResult.GracefulSession
        or CloseResult.GracefulClose or CloseResult.Killed;
}

public interface IMessenger
{
    bool IsRunning();
    void Launch();
    Task<CloseReport> CloseAsync(bool allowKill, CancellationToken ct);
}

public enum DownloadChoice { Download, Skip, Cancel }
public enum ConflictChoice { UseCloud, UseLocal, Cancel }
public enum LoginChoice { Login, SkipSync, Cancel }
public enum LockChoice { RequestUpload, Continue, Cancel }

/// <summary>사용자에게 묻거나 알리는 창. 실제 화면은 UI 쪽에서 구현한다.</summary>
public interface IUserInteraction
{
    Task<bool> AskMessengerAlreadyRunningAsync();
    Task<LoginChoice> AskLoginAsync(bool expired);
    Task<bool> AskRenewLoginAsync(int daysSinceLogin);
    Task<bool> AskLaunchOfflineAsync(string error);
    /// <summary>다른 PC가 사용 중일 때: 그 PC에 업로드 요청 / 그냥 계속 / 취소. noResponse 면 요청했지만 응답이 없었던 뒤.</summary>
    Task<LockChoice> AskLockAsync(LockInfo other, bool noResponse);
    Task<DownloadChoice> AskDownloadAsync(CloudState cloud, LocalInfo local);
    Task<ConflictChoice> AskConflictAsync(CloudState cloud, LocalInfo local, bool firstRunOnThisPc);
    Task<bool> ConfirmAsync(string heading, string text);
    void Notify(string title, string text, bool error = false);
}
