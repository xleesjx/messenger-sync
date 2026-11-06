namespace MessengerSync.Core;

/// <summary>
/// 클라우드 저장소. 한 아이디의 폴더 하나를 다룬다.
///   [아이디]/current.json, lock.json, devices.json
///   [아이디]/versions/v0015_20261104-1802_창의센터.db.gz
/// </summary>
public interface ICloudStorage
{
    string Description { get; }

    bool RequiresLogin { get; }
    Task<bool> HasLoginAsync(CancellationToken ct);
    Task LoginAsync(CancellationToken ct);

    Task<string?> ReadTextAsync(string name, CancellationToken ct);
    Task WriteTextAsync(string name, string content, CancellationToken ct);
    Task DeleteTextAsync(string name, CancellationToken ct);

    /// <summary>올린 뒤 내용이 일치하는지 확인까지 끝내야 반환한다.</summary>
    Task UploadBlobAsync(string localPath, string blobName, IProgress<double>? progress, CancellationToken ct);

    /// <summary>파일이 (아직) 없으면 false.</summary>
    Task<bool> TryDownloadBlobAsync(string blobName, string localPath, IProgress<double>? progress, CancellationToken ct);

    Task<List<BlobInfo>> ListBlobsAsync(CancellationToken ct);
    Task DeleteBlobAsync(string blobName, CancellationToken ct);
}

public static class CloudFiles
{
    public const string Current = "current.json";
    public const string Lock = "lock.json";
    public const string Devices = "devices.json";
    public const string Request = "request.json";
}
