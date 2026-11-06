using System.Text.Json.Serialization;

namespace MessengerSync.Core;

public enum StorageMode
{
    GoogleApi,
    Folder,
}

/// <summary>PC마다 따로 저장되는 설정.</summary>
public sealed class AppSettings
{
    public const string DefaultMessengerExe = @"C:\Program Files (x86)\UlsanEdu Messenger\TUC.exe";
    public static readonly string[] SuggestedPcNames = { "창의센터", "교수부", "노트북" };

    public string UserId { get; set; } = "";
    public string DataDir { get; set; } = "";
    public string MessengerExe { get; set; } = DefaultMessengerExe;
    public string ProcessName { get; set; } = "TUC";
    public string PcName { get; set; } = "";
    public string DeviceId { get; set; } = Guid.NewGuid().ToString("N");

    public StorageMode Storage { get; set; } = StorageMode.GoogleApi;
    public string FolderPath { get; set; } = "";
    public string ClientSecretPath { get; set; } = "";
    public string GoogleAccountHint { get; set; } = "";
    public string DriveRootFolderId { get; set; } = "";
    public DateTime? GoogleLoginUtc { get; set; }

    public int KeepVersions { get; set; } = 20;
    public int KeepDailyDays { get; set; } = 30;
    public int KeepLocalBackups { get; set; } = 10;
    public bool SyncOnLock { get; set; } = true;
    public bool RelaunchOnUnlock { get; set; } = true;
    public bool LaunchMessengerOnStart { get; set; } = true;
    public bool AskBeforeDownload { get; set; } = true;
    public int DownloadWaitSeconds { get; set; } = 120;
    public int CloseTimeoutSeconds { get; set; } = 15;
    public bool AutoCheckUpdates { get; set; } = true;

    // 자리 비움: 입력이 없으면(또는 화면 보호기가 켜지면) 메신저 종료 → 업로드
    public bool IdleEnabled { get; set; } = true;
    public int IdleMinutes { get; set; } = 10;
    /// <summary>설정 화면에서 자리 비움 시간을 직접 저장했는지. 아니면 기본값이 바뀔 때 따라간다.</summary>
    public bool IdleMinutesSetByUser { get; set; }
    public bool IdleSkipFullscreen { get; set; } = true;

    // Ctrl+Alt+L: 동기화한 뒤 화면 잠금
    public bool HotkeyEnabled { get; set; } = true;

    // 다른 PC에 업로드 요청했을 때 기다리는 시간
    public int RemoteWaitSeconds { get; set; } = 180;
    public int RemotePollSeconds { get; set; } = 5;
    public DateTime? LastUpdateCheckUtc { get; set; }
    public string NotifiedUpdateVersion { get; set; } = "";

    [JsonIgnore] public string DbFileName => UserId.Trim() + ".db";
    [JsonIgnore] public string DbPath => Path.Combine(DataDir, DbFileName);

    /// <summary>exe 옆의 client_secret.json 을 기본으로 쓴다.</summary>
    [JsonIgnore]
    public string EffectiveClientSecretPath => string.IsNullOrWhiteSpace(ClientSecretPath)
        ? Path.Combine(AppContext.BaseDirectory, "client_secret.json")
        : ClientSecretPath;

    public List<string> Validate()
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(UserId)) problems.Add("아이디가 비어 있습니다.");
        if (string.IsNullOrWhiteSpace(DataDir) || !Directory.Exists(DataDir)) problems.Add("쪽지 폴더가 없습니다.");
        if (string.IsNullOrWhiteSpace(MessengerExe) || !File.Exists(MessengerExe)) problems.Add("메신저 실행파일을 찾을 수 없습니다.");
        if (string.IsNullOrWhiteSpace(ProcessName)) problems.Add("프로세스 이름이 비어 있습니다.");
        if (string.IsNullOrWhiteSpace(PcName)) problems.Add("이 PC 이름이 비어 있습니다.");
        if (Storage == StorageMode.Folder && (string.IsNullOrWhiteSpace(FolderPath) || !Directory.Exists(FolderPath)))
            problems.Add("동기화 폴더가 없습니다.");
        if (Storage == StorageMode.GoogleApi && !File.Exists(EffectiveClientSecretPath))
            problems.Add("client_secret.json 파일을 찾을 수 없습니다.");
        return problems;
    }

    [JsonIgnore] public bool IsComplete => Validate().Count == 0;

    public static AppSettings Load()
    {
        var s = JsonUtil.LoadFile<AppSettings>(Paths.SettingsFile);
        if (string.IsNullOrWhiteSpace(s.DeviceId)) s.DeviceId = Guid.NewGuid().ToString("N");
        // v0.1.8의 기본값 5분은 메신저가 너무 자주 꺼져서 10분(화면 보호기와 같은 시간)으로 바꿨다.
        // 사용자가 직접 정한 값이 아니면 새 기본값을 따른다.
        if (!s.IdleMinutesSetByUser && s.IdleMinutes == 5) s.IdleMinutes = 10;
        return s;
    }

    public void Save() => JsonUtil.SaveFile(Paths.SettingsFile, this);

    public AppSettings Clone() => JsonUtil.Deserialize<AppSettings>(JsonUtil.Serialize(this))!;
}
