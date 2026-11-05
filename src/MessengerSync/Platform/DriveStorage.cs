using System.Text;
using Google;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Upload;
using MessengerSync.Core;
using DFile = Google.Apis.Drive.v3.Data.File;

namespace MessengerSync.Platform;

/// <summary>
/// 구글 드라이브 API 저장소. 권한은 drive.file(이 앱이 만든 파일만)이다.
/// 처음에 "내 드라이브/messenger-sync-data" 폴더를 만들며, 사용자가 이 폴더를 다른 곳으로 옮겨도 계속 찾는다.
/// </summary>
public sealed class DriveStorage : ICloudStorage
{
    public const string RootFolderName = "messenger-sync-data";
    private const string FolderMime = "application/vnd.google-apps.folder";
    private const string User = "user";

    private const string ClosePage =
        "<html><head><meta charset='utf-8'><title>메신저 동기화</title></head>" +
        "<body style='font-family:Malgun Gothic,sans-serif;text-align:center;padding-top:80px'>" +
        "<h2>구글 로그인이 끝났습니다.</h2><p>이 창을 닫고 메신저 동기화 프로그램으로 돌아가세요.</p></body></html>";

    private readonly AppSettings _s;
    private DriveService? _svc;
    private string? _userFolderId;
    private string? _versionsId;

    public DriveStorage(AppSettings settings) => _s = settings;

    public string Description => "구글 드라이브 (API)";
    public bool RequiresLogin => true;

    // ───────────── 로그인 ─────────────

    private GoogleAuthorizationCodeFlow CreateFlow(bool forceConsent)
    {
        var secretPath = _s.EffectiveClientSecretPath;
        if (!File.Exists(secretPath))
            throw new FileNotFoundException("client_secret.json 파일을 찾을 수 없습니다.", secretPath);
        using var fs = File.OpenRead(secretPath);
        var init = new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = GoogleClientSecrets.FromStream(fs).Secrets,
            Scopes = new[] { DriveService.Scope.DriveFile },
            DataStore = new DpapiDataStore(Paths.TokenDir),
        };
        // 다시 로그인할 때 새 갱신 토큰을 받으려면 동의 화면을 다시 거쳐야 한다.
        if (forceConsent) init.Prompt = "consent select_account";
        if (!string.IsNullOrWhiteSpace(_s.GoogleAccountHint)) init.LoginHint = _s.GoogleAccountHint.Trim();
        return new GoogleAuthorizationCodeFlow(init);
    }

    public async Task<bool> HasLoginAsync(CancellationToken ct)
    {
        if (_svc != null) return true;
        var flow = CreateFlow(false);
        var token = await flow.LoadTokenAsync(User, ct);
        if (token?.RefreshToken == null) return false;
        var cred = new UserCredential(flow, User, token);
        try
        {
            await cred.GetAccessTokenForRequestAsync(cancellationToken: ct);
        }
        catch (TokenResponseException ex) when (ex.Error?.Error is "invalid_grant" or "unauthorized_client")
        {
            Log.Warn($"구글 로그인이 만료되었습니다: {ex.Error?.Error}");
            return false;
        }
        SetCredential(cred);
        return true;
    }

    public async Task LoginAsync(CancellationToken ct)
    {
        var flow = CreateFlow(forceConsent: true);
        await flow.DeleteTokenAsync(User, ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(5));
        var app = new AuthorizationCodeInstalledApp(flow, new LocalServerCodeReceiver(ClosePage));
        var cred = await app.AuthorizeAsync(User, cts.Token);
        SetCredential(cred);
        await EnsureFoldersAsync(ct); // 학교 계정에서 드라이브 접근이 되는지 바로 확인
    }

    public async Task LogoutAsync()
    {
        await new DpapiDataStore(Paths.TokenDir).ClearAsync();
        _svc = null;
        _userFolderId = _versionsId = null;
    }

    private void SetCredential(UserCredential cred)
    {
        _svc = new DriveService(new BaseClientService.Initializer
        {
            HttpClientInitializer = cred,
            ApplicationName = "MessengerSync",
        });
        _userFolderId = _versionsId = null;
    }

    private async Task<T> Call<T>(Func<DriveService, Task<T>> action, CancellationToken ct)
    {
        if (_svc == null && !await HasLoginAsync(ct)) throw new NeedLoginException();
        try
        {
            return await action(_svc!);
        }
        catch (TokenResponseException ex) when (ex.Error?.Error is "invalid_grant" or "unauthorized_client")
        {
            _svc = null;
            throw new NeedLoginException("구글 로그인이 만료되었습니다.");
        }
    }

    // ───────────── 폴더 ─────────────

    private static string Esc(string s) => s.Replace("\\", "\\\\").Replace("'", "\\'");

    private static async Task<DFile?> FindAsync(DriveService svc, string name, string? parentId, bool folder, CancellationToken ct)
    {
        var q = $"name='{Esc(name)}' and trashed=false and mimeType{(folder ? "=" : "!=")}'{FolderMime}'";
        if (parentId != null) q += $" and '{parentId}' in parents";
        var req = svc.Files.List();
        req.Q = q;
        req.Spaces = "drive";
        req.Fields = "files(id,name,size,modifiedTime,md5Checksum)";
        req.OrderBy = "createdTime";
        req.PageSize = 10;
        var res = await req.ExecuteAsync(ct);
        return res.Files?.FirstOrDefault();
    }

    private static async Task<string> EnsureFolderAsync(DriveService svc, string name, string? parentId, CancellationToken ct)
    {
        var found = await FindAsync(svc, name, parentId, folder: true, ct);
        if (found != null) return found.Id;
        var meta = new DFile { Name = name, MimeType = FolderMime };
        if (parentId != null) meta.Parents = new List<string> { parentId };
        var req = svc.Files.Create(meta);
        req.Fields = "id";
        var created = await req.ExecuteAsync(ct);
        Log.Info($"구글 드라이브 폴더 생성: {name}");
        return created.Id;
    }

    private async Task<string> RootIdAsync(DriveService svc, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(_s.DriveRootFolderId))
        {
            try
            {
                var g = svc.Files.Get(_s.DriveRootFolderId);
                g.Fields = "id,trashed";
                var f = await g.ExecuteAsync(ct);
                if (f.Trashed != true) return f.Id;
            }
            catch (GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // 지워졌으면 다시 찾거나 만든다
            }
        }
        // 위치와 상관없이 이 앱이 만든 폴더를 찾는다 (사용자가 다른 폴더로 옮겼어도 찾음)
        var id = await EnsureFolderAsync(svc, RootFolderName, null, ct);
        _s.DriveRootFolderId = id;
        try { _s.Save(); } catch { /* 다음에 다시 찾으면 된다 */ }
        return id;
    }

    private async Task EnsureFoldersAsync(CancellationToken ct)
    {
        if (_userFolderId != null && _versionsId != null) return;
        await Call(async svc =>
        {
            var root = await RootIdAsync(svc, ct);
            _userFolderId = await EnsureFolderAsync(svc, _s.UserId.Trim(), root, ct);
            _versionsId = await EnsureFolderAsync(svc, "versions", _userFolderId, ct);
            return true;
        }, ct);
    }

    // ───────────── 작은 파일 (json) ─────────────

    public async Task<string?> ReadTextAsync(string name, CancellationToken ct)
    {
        await EnsureFoldersAsync(ct);
        return await Call(async svc =>
        {
            var f = await FindAsync(svc, name, _userFolderId, false, ct);
            if (f == null) return null;
            using var ms = new MemoryStream();
            var prog = await svc.Files.Get(f.Id).DownloadAsync(ms, ct);
            if (prog.Status != Google.Apis.Download.DownloadStatus.Completed)
                throw prog.Exception ?? new IOException($"{name} 을(를) 받지 못했습니다.");
            return Encoding.UTF8.GetString(ms.ToArray());
        }, ct);
    }

    public async Task WriteTextAsync(string name, string content, CancellationToken ct)
    {
        await EnsureFoldersAsync(ct);
        await Call(async svc =>
        {
            var existing = await FindAsync(svc, name, _userFolderId, false, ct);
            using var ms = new MemoryStream(Encoding.UTF8.GetBytes(content));
            IUploadProgress prog;
            if (existing != null)
            {
                prog = await svc.Files.Update(new DFile(), existing.Id, ms, "application/json").UploadAsync(ct);
            }
            else
            {
                var meta = new DFile { Name = name, Parents = new List<string> { _userFolderId! } };
                prog = await svc.Files.Create(meta, ms, "application/json").UploadAsync(ct);
            }
            if (prog.Status != UploadStatus.Completed)
                throw prog.Exception ?? new IOException($"{name} 을(를) 올리지 못했습니다.");
            return true;
        }, ct);
    }

    public async Task DeleteTextAsync(string name, CancellationToken ct)
    {
        await EnsureFoldersAsync(ct);
        await Call(async svc =>
        {
            var f = await FindAsync(svc, name, _userFolderId, false, ct);
            if (f != null) await svc.Files.Delete(f.Id).ExecuteAsync(ct);
            return true;
        }, ct);
    }

    // ───────────── 쪽지 파일 (versions) ─────────────

    public async Task UploadBlobAsync(string localPath, string blobName, IProgress<double>? progress, CancellationToken ct)
    {
        await EnsureFoldersAsync(ct);
        var localMd5 = FileOps.Md5(localPath);
        await Call(async svc =>
        {
            // 지난번에 중간에 끊긴 같은 이름의 파일이 있으면 지운다
            var old = await FindAsync(svc, blobName, _versionsId, false, ct);
            if (old != null) await svc.Files.Delete(old.Id).ExecuteAsync(ct);

            await using var fs = File.OpenRead(localPath);
            var total = fs.Length;
            var meta = new DFile { Name = blobName, Parents = new List<string> { _versionsId! } };
            var req = svc.Files.Create(meta, fs, "application/gzip");
            req.Fields = "id,md5Checksum,size";
            req.ChunkSize = ResumableUpload.MinimumChunkSize * 20; // 5MB 단위, 끊기면 이어서 올림
            req.ProgressChanged += p =>
            {
                if (total > 0) progress?.Report(Math.Min(1.0, (double)p.BytesSent / total));
            };
            var prog = await req.UploadAsync(ct);
            if (prog.Status != UploadStatus.Completed)
                throw prog.Exception ?? new IOException("업로드가 끝나지 않았습니다.");

            // 구글이 계산한 해시값과 비교해서, 서버에 온전히 저장됐는지 확인한다
            var remoteMd5 = req.ResponseBody?.Md5Checksum;
            if (!string.Equals(remoteMd5, localMd5, StringComparison.OrdinalIgnoreCase))
            {
                if (req.ResponseBody?.Id != null) await svc.Files.Delete(req.ResponseBody.Id).ExecuteAsync(ct);
                throw new IOException($"업로드한 파일이 원본과 다릅니다 (md5 {remoteMd5} ≠ {localMd5}).");
            }
            progress?.Report(1.0);
            return true;
        }, ct);
    }

    public async Task<bool> TryDownloadBlobAsync(string blobName, string localPath, IProgress<double>? progress, CancellationToken ct)
    {
        await EnsureFoldersAsync(ct);
        return await Call(async svc =>
        {
            var f = await FindAsync(svc, blobName, _versionsId, false, ct);
            if (f == null) return false;
            await using var fs = File.Create(localPath);
            var total = f.Size ?? 0;
            var get = svc.Files.Get(f.Id);
            get.MediaDownloader.ChunkSize = 1 << 20;
            get.MediaDownloader.ProgressChanged += p =>
            {
                if (total > 0) progress?.Report(Math.Min(1.0, (double)p.BytesDownloaded / total));
            };
            var prog = await get.DownloadAsync(fs, ct);
            if (prog.Status != Google.Apis.Download.DownloadStatus.Completed)
                throw prog.Exception ?? new IOException("다운로드가 끝나지 않았습니다.");
            return true;
        }, ct);
    }

    public async Task<List<BlobInfo>> ListBlobsAsync(CancellationToken ct)
    {
        await EnsureFoldersAsync(ct);
        return await Call(async svc =>
        {
            var list = new List<BlobInfo>();
            string? page = null;
            do
            {
                var req = svc.Files.List();
                req.Q = $"'{_versionsId}' in parents and trashed=false";
                req.Spaces = "drive";
                req.Fields = "nextPageToken, files(id,name,size,modifiedTime)";
                req.PageSize = 200;
                req.PageToken = page;
                var res = await req.ExecuteAsync(ct);
                foreach (var f in res.Files ?? new List<DFile>())
                    list.Add(new BlobInfo { Name = f.Name, Size = f.Size ?? 0, ModifiedUtc = f.ModifiedTimeDateTimeOffset?.UtcDateTime });
                page = res.NextPageToken;
            } while (page != null);
            return list;
        }, ct);
    }

    public async Task DeleteBlobAsync(string blobName, CancellationToken ct)
    {
        await EnsureFoldersAsync(ct);
        await Call(async svc =>
        {
            var f = await FindAsync(svc, blobName, _versionsId, false, ct);
            if (f != null) await svc.Files.Delete(f.Id).ExecuteAsync(ct);
            return true;
        }, ct);
    }
}
