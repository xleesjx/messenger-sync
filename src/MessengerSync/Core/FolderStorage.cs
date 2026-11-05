namespace MessengerSync.Core;

/// <summary>
/// 일반 폴더(구글 드라이브 데스크톱의 G: 폴더, NAS 등)를 저장소로 쓰는 방식.
/// 업로드가 실제로 끝났는지는 알 수 없으므로, 받는 쪽에서 해시값으로 확인한다.
/// </summary>
public sealed class FolderStorage : ICloudStorage
{
    private readonly string _userDir;
    private readonly string _versionsDir;

    public FolderStorage(string rootPath, string userId)
    {
        _userDir = Path.Combine(rootPath, userId);
        _versionsDir = Path.Combine(_userDir, "versions");
    }

    public string Description => $"폴더 ({_userDir})";
    public bool RequiresLogin => false;
    public Task<bool> HasLoginAsync(CancellationToken ct) => Task.FromResult(true);
    public Task LoginAsync(CancellationToken ct) => Task.CompletedTask;

    private string UserDir => Paths.Ensure(_userDir);
    private string VersionsDir => Paths.Ensure(_versionsDir);

    public async Task<string?> ReadTextAsync(string name, CancellationToken ct)
    {
        var path = Path.Combine(_userDir, name);
        return File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : null;
    }

    public async Task WriteTextAsync(string name, string content, CancellationToken ct)
    {
        var path = Path.Combine(UserDir, name);
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, content, ct);
        File.Move(tmp, path, overwrite: true);
    }

    public Task DeleteTextAsync(string name, CancellationToken ct)
    {
        var path = Path.Combine(_userDir, name);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    public async Task UploadBlobAsync(string localPath, string blobName, IProgress<double>? progress, CancellationToken ct)
    {
        var dest = Path.Combine(VersionsDir, blobName);
        var partial = dest + ".partial";
        await using (var src = File.OpenRead(localPath))
        await using (var dst = File.Create(partial))
        {
            var buffer = new byte[1 << 20];
            long done = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                progress?.Report(src.Length == 0 ? 1 : (double)done / src.Length);
            }
        }
        if (FileOps.Sha256(partial) != FileOps.Sha256(localPath))
        {
            File.Delete(partial);
            throw new IOException("폴더에 복사한 파일이 원본과 다릅니다.");
        }
        File.Move(partial, dest, overwrite: true);
    }

    public async Task<bool> TryDownloadBlobAsync(string blobName, string localPath, IProgress<double>? progress, CancellationToken ct)
    {
        var src = Path.Combine(_versionsDir, blobName);
        if (!File.Exists(src)) return false;
        await using var s = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        await using var d = File.Create(localPath);
        var buffer = new byte[1 << 20];
        long done = 0;
        int read;
        while ((read = await s.ReadAsync(buffer, ct)) > 0)
        {
            await d.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;
            progress?.Report(s.Length == 0 ? 1 : (double)done / s.Length);
        }
        return true;
    }

    public Task<List<BlobInfo>> ListBlobsAsync(CancellationToken ct)
    {
        var list = new List<BlobInfo>();
        if (Directory.Exists(_versionsDir))
        {
            foreach (var f in new DirectoryInfo(_versionsDir).GetFiles("*.db.gz"))
                list.Add(new BlobInfo { Name = f.Name, Size = f.Length, ModifiedUtc = f.LastWriteTimeUtc });
        }
        return Task.FromResult(list);
    }

    public Task DeleteBlobAsync(string blobName, CancellationToken ct)
    {
        var path = Path.Combine(_versionsDir, blobName);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }
}
