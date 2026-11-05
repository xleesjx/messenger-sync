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
        return s;
    }

    public void Save() => JsonUtil.SaveFile(Paths.SettingsFile, this);

    public AppSettings Clone() => JsonUtil.Deserialize<AppSettings>(JsonUtil.Serialize(this))!;
}
