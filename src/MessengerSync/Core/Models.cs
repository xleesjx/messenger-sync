namespace MessengerSync.Core;

/// <summary>이 PC가 마지막으로 클라우드와 맞춘 상태. 충돌 판단의 기준이 된다.</summary>
public sealed class LocalState
{
    public string UserId { get; set; } = "";

    /// <summary>마지막으로 받거나 올린 클라우드 버전 번호. 0이면 아직 한 번도 맞춘 적 없음.</summary>
    public int BaseVersion { get; set; }

    /// <summary>그때의 db 파일 해시값. null이면 이 PC에서 처음 사용.</summary>
    public string? BaseHash { get; set; }

    public DateTime? LastSyncUtc { get; set; }
    public bool PendingUpload { get; set; }
    public bool PendingConflict { get; set; }
    public string? LastError { get; set; }

    public static LocalState Load(string userId)
    {
        var st = JsonUtil.LoadFile<LocalState>(Paths.StateFile(userId));
        st.UserId = userId;
        return st;
    }

    public void Save() => JsonUtil.SaveFile(Paths.StateFile(UserId), this);
}

/// <summary>클라우드 current.json: 지금 최신 버전이 무엇인지.</summary>
public sealed class CloudState
{
    public string UserId { get; set; } = "";
    public int Version { get; set; }
    public string BlobName { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    public long CompressedSize { get; set; }
    public string PcName { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public DateTime UploadedUtc { get; set; }
    public string AppVersion { get; set; } = "";
}

/// <summary>클라우드 lock.json: 지금 메신저를 쓰고 있는 PC.</summary>
public sealed class LockInfo
{
    public string DeviceId { get; set; } = "";
    public string PcName { get; set; } = "";
    public DateTime SinceUtc { get; set; }
}

/// <summary>클라우드 devices.json 의 한 줄: PC별 마지막 동작.</summary>
public sealed class DeviceRecord
{
    public string DeviceId { get; set; } = "";
    public string PcName { get; set; } = "";
    public string LastAction { get; set; } = "";
    public DateTime LastUtc { get; set; }
    public int Version { get; set; }
}

public sealed class BlobInfo
{
    public string Name { get; set; } = "";
    public long Size { get; set; }
    public DateTime? ModifiedUtc { get; set; }

    /// <summary>"v0015_20261104-1802_..." 형식에서 올린 날짜를 읽는다.</summary>
    public DateTime? Date
    {
        get
        {
            var parts = Name.Split('_');
            return parts.Length >= 2 && parts[1].Length >= 8 &&
                   DateTime.TryParseExact(parts[1][..8], "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var d)
                ? d : null;
        }
    }

    /// <summary>"v0015_..." 형식에서 버전 번호를 읽는다. 형식이 다르면 0.</summary>
    public int Version
    {
        get
        {
            if (Name.Length < 2 || Name[0] != 'v') return 0;
            var end = Name.IndexOf('_');
            return end > 1 && int.TryParse(Name[1..end], out var v) ? v : 0;
        }
    }
}

public sealed class LocalInfo
{
    public bool Exists { get; set; }
    public long Size { get; set; }
    public DateTime? ModifiedLocal { get; set; }
    public int BaseVersion { get; set; }
}

public sealed class Overview
{
    public CloudState? Cloud { get; set; }
    public LockInfo? Lock { get; set; }
    public Dictionary<string, DeviceRecord> Devices { get; set; } = new();
    public List<BlobInfo> Versions { get; set; } = new();
    public LocalState State { get; set; } = new();
    public LocalInfo Local { get; set; } = new();
}

public enum StepState { Pending, Running, Done, Skipped, Failed }

/// <summary>진행 창에 보여줄 단계 목록과 현재 단계의 진행률.</summary>
public sealed class ProgressInfo
{
    public string Title { get; set; } = "";
    public List<string> Steps { get; set; } = new();
    public List<StepState> States { get; set; } = new();
    public int Current { get; set; } = -1;
    /// <summary>현재 단계의 진행률 0~1. 알 수 없으면 null (움직이는 막대).</summary>
    public double? Percent { get; set; }
    public string Detail { get; set; } = "";
    public bool Finished { get; set; }
    public bool Success { get; set; }

    public ProgressInfo Clone() => new()
    {
        Title = Title, Steps = new(Steps), States = new(States), Current = Current,
        Percent = Percent, Detail = Detail, Finished = Finished, Success = Success,
    };
}

/// <summary>구글 로그인이 없거나 만료되었을 때.</summary>
public sealed class NeedLoginException : Exception
{
    public NeedLoginException(string message = "구글 로그인이 필요합니다.") : base(message) { }
}
