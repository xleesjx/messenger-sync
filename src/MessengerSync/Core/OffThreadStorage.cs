namespace MessengerSync.Core;

/// <summary>
/// 클라우드 작업을 항상 백그라운드 스레드에서 실행한다.
/// (첫 인터넷 접속 때 프록시 확인 등으로 몇 초씩 멈추는 작업이 화면을 굳게 하지 않도록)
/// </summary>
public sealed class OffThreadStorage : ICloudStorage
{
    private readonly ICloudStorage _inner;

    public OffThreadStorage(ICloudStorage inner) => _inner = inner;

    public ICloudStorage Inner => _inner;
    public string Description => _inner.Description;
    public bool RequiresLogin => _inner.RequiresLogin;

    public Task<bool> HasLoginAsync(CancellationToken ct) => Task.Run(() => _inner.HasLoginAsync(ct), ct);
    public Task LoginAsync(CancellationToken ct) => Task.Run(() => _inner.LoginAsync(ct), ct);
    public Task<string?> ReadTextAsync(string name, CancellationToken ct) => Task.Run(() => _inner.ReadTextAsync(name, ct), ct);
    public Task WriteTextAsync(string name, string content, CancellationToken ct) => Task.Run(() => _inner.WriteTextAsync(name, content, ct), ct);
    public Task DeleteTextAsync(string name, CancellationToken ct) => Task.Run(() => _inner.DeleteTextAsync(name, ct), ct);

    public Task UploadBlobAsync(string localPath, string blobName, IProgress<double>? progress, CancellationToken ct)
        => Task.Run(() => _inner.UploadBlobAsync(localPath, blobName, progress, ct), ct);

    public Task<bool> TryDownloadBlobAsync(string blobName, string localPath, IProgress<double>? progress, CancellationToken ct)
        => Task.Run(() => _inner.TryDownloadBlobAsync(blobName, localPath, progress, ct), ct);

    public Task<List<BlobInfo>> ListBlobsAsync(CancellationToken ct) => Task.Run(() => _inner.ListBlobsAsync(ct), ct);
    public Task DeleteBlobAsync(string blobName, CancellationToken ct) => Task.Run(() => _inner.DeleteBlobAsync(blobName, ct), ct);
}
