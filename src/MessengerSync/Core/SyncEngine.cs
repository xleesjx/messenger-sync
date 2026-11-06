using System.Diagnostics;
using System.Reflection;

namespace MessengerSync.Core;

public enum SyncAction
{
    /// <summary>클라우드와 이 PC가 같다.</summary>
    None,
    /// <summary>클라우드가 더 새것이고 이 PC는 그대로.</summary>
    Download,
    /// <summary>이 PC만 바뀌었다 (지난번에 못 올린 경우 등).</summary>
    Upload,
    /// <summary>클라우드와 이 PC 모두 바뀌었다.</summary>
    Conflict,
    /// <summary>이 PC에서 처음 쓰는데 클라우드에 다른 내용이 있다.</summary>
    FirstRunAsk,
    /// <summary>클라우드가 비어 있다. 처음 올린다.</summary>
    InitialUpload,
    /// <summary>번호는 다르지만 내용이 같다. 기록만 맞춘다.</summary>
    AdoptCloud,
}

public enum PushOutcome { NoChange, Uploaded, Adopted, Conflict, Pending, Corrupt, NoDb, Failed }

/// <summary>
/// 동기화의 판단과 순서를 담당한다. 파일 수정 시각은 쓰지 않고,
/// "이 PC가 마지막으로 맞춘 버전/해시"와 클라우드 current.json 을 비교한다.
/// </summary>
public sealed class SyncEngine
{
    public static readonly string AppVersion =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    private readonly AppSettings _s;
    private readonly Func<ICloudStorage> _storage;
    private readonly IMessenger _m;
    private readonly IUserInteraction _ui;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SyncEngine(AppSettings settings, Func<ICloudStorage> storageFactory, IMessenger messenger, IUserInteraction ui)
    {
        _s = settings;
        _storage = storageFactory;
        _m = messenger;
        _ui = ui;
    }

    /// <summary>동기화 작업(또는 그 안의 확인 창)이 진행 중인지.</summary>
    public bool Busy => _gate.CurrentCount == 0;

    /// <summary>이 프로그램이 메신저 실행을 맡고 있는지 (메신저가 꺼지면 업로드한다).</summary>
    public bool LaunchedByUs { get; private set; }

    public event Action<string>? Status;

    public void ForgetLaunch() => LaunchedByUs = false;

    /// <summary>설정을 바꿔 엔진을 새로 만들 때, 이미 켜져 있는 메신저를 계속 맡는다.</summary>
    public void AdoptRunning() => LaunchedByUs = true;

    private void Report(string message)
    {
        Log.Info(message);
        Status?.Invoke(message);
        if (_p != null && !_p.Finished) { _p.Detail = message; EmitProgress(); }
    }

    // ───────────────────────── 진행 단계 ─────────────────────────

    private enum Op { None, Pull, Push }

    /// <summary>진행 창용: 단계 목록과 현재 단계, 진행률.</summary>
    public event Action<ProgressInfo>? Progress;

    private ProgressInfo? _p;
    private Op _op = Op.None;

    private static readonly string[] PullSteps = { "클라우드 확인", "쪽지 파일 비교", "동기화", "메신저 실행" };
    private static readonly string[] PushSteps = { "메신저 종료", "쪽지 파일 검사", "압축", "업로드", "마무리" };

    private void BeginSteps(Op op, string title, string[] steps)
    {
        _op = op;
        _p = new ProgressInfo
        {
            Title = title,
            Steps = steps.ToList(),
            States = steps.Select(_ => StepState.Pending).ToList(),
        };
        EmitProgress();
    }

    private void EmitProgress()
    {
        if (_p != null) Progress?.Invoke(_p.Clone());
    }

    /// <summary>i 번째 단계를 시작한다. 그 앞의 진행 중 단계는 완료로 바꾼다.</summary>
    private void Step(int i, string detail, double? percent = null, string? label = null)
    {
        Log.Info(detail);
        Status?.Invoke(detail);
        if (_p == null || i < 0 || i >= _p.Steps.Count) return;
        for (var k = 0; k < i; k++)
            if (_p.States[k] is StepState.Pending or StepState.Running) _p.States[k] = StepState.Done;
        if (label != null) _p.Steps[i] = label;
        _p.States[i] = StepState.Running;
        _p.Current = i;
        _p.Percent = percent;
        _p.Detail = detail;
        EmitProgress();
    }

    private void StepPercent(double percent, string? detail = null)
    {
        if (_p == null || _p.Finished) return;
        _p.Percent = Math.Clamp(percent, 0, 1);
        if (detail != null) { _p.Detail = detail; Status?.Invoke(detail); }
        EmitProgress();
    }

    private void SkipStep(int i, string? label = null)
    {
        if (_p == null || i < 0 || i >= _p.Steps.Count) return;
        if (label != null) _p.Steps[i] = label;
        _p.States[i] = StepState.Skipped;
        EmitProgress();
    }

    private void FinishSteps(bool success, string detail)
    {
        if (_p == null) return;
        for (var k = 0; k < _p.Steps.Count; k++)
        {
            if (_p.States[k] == StepState.Running) _p.States[k] = success ? StepState.Done : StepState.Failed;
            else if (_p.States[k] == StepState.Pending && success) _p.States[k] = StepState.Skipped;
        }
        _p.Finished = true;
        _p.Success = success;
        _p.Percent = null;
        _p.Detail = detail;
        EmitProgress();
        _op = Op.None;
    }

    /// <summary>
    /// 올리기 단계 표시. 단독 업로드(잠금 등)면 검사/압축/업로드/마무리 단계를 각각 쓰고,
    /// 받기 작업 중에 올리는 경우에는 "동기화" 단계 하나 안에서 세부 내용만 바꾼다.
    /// </summary>
    private void SkipPushUpload(string why)
    {
        if (_op != Op.Push) return;
        SkipStep(2, $"압축 ({why})");
        SkipStep(3, $"업로드 ({why})");
    }

    private void PushPhase(int phase, string detail, double? percent = null)
    {
        if (_op == Op.Pull) Step(2, detail, percent, "올리기");
        else Step(phase, detail, percent);
    }

    private LocalState LoadState() => LocalState.Load(_s.UserId.Trim());

    // ───────────────────────── 판단 ─────────────────────────

    public static SyncAction Decide(LocalState st, CloudState? cloud, string? localHash)
    {
        if (localHash == null) // 이 PC에 db가 없음
            return cloud == null ? SyncAction.None : SyncAction.Download;

        if (st.BaseHash == null) // 이 PC에서 처음
        {
            if (cloud == null) return SyncAction.InitialUpload;
            return cloud.Sha256 == localHash ? SyncAction.AdoptCloud : SyncAction.FirstRunAsk;
        }

        if (cloud == null) // 클라우드가 비었음 (폴더를 지웠거나 저장소를 바꿈)
            return SyncAction.Upload;

        var cloudChanged = cloud.Version > st.BaseVersion;
        var localChanged = localHash != st.BaseHash;

        if (cloud.Sha256 == localHash)
            return cloud.Version == st.BaseVersion && !localChanged ? SyncAction.None : SyncAction.AdoptCloud;
        if (cloudChanged && localChanged) return SyncAction.Conflict;
        if (cloudChanged) return SyncAction.Download;
        if (localChanged) return SyncAction.Upload;
        return SyncAction.None;
    }

    // ───────────────────────── 받기 → 메신저 실행 ─────────────────────────

    /// <summary>클라우드를 확인해 필요하면 받고(또는 올리고), launch 가 true 면 메신저를 켠다.</summary>
    public async Task<bool> PullAndLaunchAsync(bool launch, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            BeginSteps(Op.Pull, launch ? "메신저 실행 준비" : "최신 데이터 받기", PullSteps);
            if (!launch) SkipStep(3);
            var ok = await PullAndLaunchCoreAsync(launch, ct);
            FinishSteps(ok, ok ? (launch ? "메신저를 실행했습니다" : "동기화를 마쳤습니다") : "취소되었습니다");
            return ok;
        }
        catch (Exception ex)
        {
            Log.Error("동기화 중 오류", ex);
            FinishSteps(false, "오류: " + ex.Message);
            _ui.Notify("동기화 오류", ex.Message, error: true);
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> PullAndLaunchCoreAsync(bool launch, CancellationToken ct)
    {
        var problems = _s.Validate();
        if (problems.Count > 0)
        {
            _ui.Notify("설정이 필요합니다", string.Join("\n", problems), error: true);
            return false;
        }

        if (_m.IsRunning())
        {
            if (LaunchedByUs) return true;
            if (!await _ui.AskMessengerAlreadyRunningAsync())
            {
                LaunchedByUs = true; // 그대로 쓰되, 꺼지면 업로드한다
                await TryWriteLockAsync(_storage(), ct);
                return true;
            }
            Step(0, "동기화 전에 켜진 메신저를 종료하는 중...");
            var close = await _m.CloseAsync(allowKill: true, ct);
            if (!close.Closed)
            {
                _ui.Notify("메신저를 끄지 못했습니다", "작업 관리자에서 메신저를 종료한 뒤 다시 시도해 주세요.", error: true);
                return false;
            }
        }

        var storage = _storage();
        CloudState? cloud;
        LockInfo? otherLock;
        try
        {
            switch (await EnsureLoginAsync(storage, ct))
            {
                case LoginChoice.Cancel:
                    return false;
                case LoginChoice.SkipSync:
                    Log.Warn("사용자가 동기화 없이 메신저를 실행했습니다.");
                    if (launch) DoLaunch();
                    return true;
            }

            // 부팅 직후 등 인터넷이 아직 안 붙었을 수 있으니 몇 번 더 시도한다
            LockInfo? lk = null;
            cloud = null;
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    Step(0, attempt == 1 ? "클라우드의 최신 버전 정보를 확인하는 중..." : $"클라우드 확인 중... (재시도 {attempt - 1})");
                    cloud = await ReadJsonAsync<CloudState>(storage, CloudFiles.Current, ct);
                    lk = await ReadJsonAsync<LockInfo>(storage, CloudFiles.Lock, ct);
                    break;
                }
                catch (Exception ex) when (attempt < 3 && ex is not (OperationCanceledException or NeedLoginException))
                {
                    Log.Warn($"클라우드 확인 실패, 5초 후 다시 시도: {ex.Message}");
                    await Task.Delay(5000, ct);
                }
            }
            otherLock = lk != null && lk.DeviceId != _s.DeviceId ? lk : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error("클라우드 확인 실패", ex);
            if (launch && await _ui.AskLaunchOfflineAsync(ex.Message))
            {
                DoLaunch();
                return true;
            }
            return false;
        }

        if (otherLock != null)
        {
            if (!await ResolveOtherLockAsync(storage, otherLock, ct)) return false;
            cloud = await ReadJsonAsync<CloudState>(storage, CloudFiles.Current, ct); // 그 PC가 방금 올렸을 수 있다
        }

        var st = LoadState();
        var db = _s.DbPath;
        Step(1, cloud == null ? "클라우드가 비어 있습니다. 이 PC 파일을 확인하는 중..."
                              : $"클라우드 v{cloud.Version}({cloud.PcName})와 이 PC 파일을 비교하는 중...");
        var localHash = File.Exists(db) ? await Task.Run(() => FileOps.Sha256(db), ct) : null;
        var action = Decide(st, cloud, localHash);
        Log.Info($"판단: {action} (클라우드 v{cloud?.Version ?? 0}, 이 PC 기준 v{st.BaseVersion})");

        switch (action)
        {
            case SyncAction.None:
                SkipStep(2, "동기화 (이미 최신)");
                Report("이미 최신 상태입니다.");
                break;

            case SyncAction.AdoptCloud:
                SkipStep(2, "동기화 (내용 같음)");
                st.BaseVersion = cloud!.Version;
                st.BaseHash = cloud.Sha256;
                st.LastSyncUtc = DateTime.UtcNow;
                st.PendingConflict = false;
                st.Save();
                break;

            case SyncAction.InitialUpload:
            case SyncAction.Upload:
                Step(2, action == SyncAction.InitialUpload ? "클라우드가 비어 있어 이 PC 파일을 처음으로 올립니다"
                                                           : "이 PC에만 있는 변경을 먼저 올립니다", label: "올리기");
                if (!await PushOrAskAsync(storage, ct)) return false;
                break;

            case SyncAction.Download:
                if (_s.AskBeforeDownload)
                {
                    var choice = await _ui.AskDownloadAsync(cloud!, GetLocalInfo(st));
                    if (choice == DownloadChoice.Cancel) return false;
                    if (choice == DownloadChoice.Skip)
                    {
                        Log.Warn("사용자가 최신 버전을 받지 않고 시작했습니다. 다음 업로드 때 충돌로 처리됩니다.");
                        SkipStep(2, "받기 (건너뜀)");
                        break;
                    }
                }
                await DownloadIntoLocalAsync(storage, cloud!, st, "받기전", ct);
                break;

            case SyncAction.Conflict:
            case SyncAction.FirstRunAsk:
                var c = await _ui.AskConflictAsync(cloud!, GetLocalInfo(st), action == SyncAction.FirstRunAsk);
                if (c == ConflictChoice.Cancel) return false;
                Step(2, c == ConflictChoice.UseCloud ? "클라우드 내용을 받는 중..." : "이 PC 내용을 새 버전으로 올리는 중...",
                    label: c == ConflictChoice.UseCloud ? "받기 (클라우드 선택)" : "올리기 (이 PC 선택)");
                if (c == ConflictChoice.UseCloud)
                {
                    var saved = FileOps.Backup(db, Paths.ConflictDir, _s.UserId, "이PC-선택안함", 50);
                    Log.Info($"충돌: 클라우드 선택. 이 PC 파일 보관 → {saved}");
                    await DownloadIntoLocalAsync(storage, cloud!, st, "충돌", ct);
                }
                else
                {
                    await SaveCloudCopyToConflictsAsync(storage, cloud!, ct);
                    st.BaseVersion = cloud!.Version; // 클라우드 다음 번호로 올린다
                    st.BaseHash = "";
                    st.PendingConflict = false;
                    st.Save();
                    if (!await PushOrAskAsync(storage, ct)) return false;
                }
                break;
        }

        if (!launch) return true;

        await TryWriteLockAsync(storage, ct);
        DoLaunch();
        await TryUpdateDeviceAsync(storage, "메신저 시작", ct);
        return true;
    }

    private void DoLaunch()
    {
        Step(3, "메신저를 실행합니다");
        _m.Launch();
        LaunchedByUs = true;
    }

    private async Task<bool> PushOrAskAsync(ICloudStorage storage, CancellationToken ct)
    {
        PushOutcome o;
        try
        {
            o = await PushCoreAsync(storage, fullCheck: false, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error("업로드 실패", ex);
            MarkPending(ex.Message);
            o = PushOutcome.Pending;
        }
        if (o is PushOutcome.Uploaded or PushOutcome.NoChange or PushOutcome.Adopted) return true;
        return await _ui.ConfirmAsync("업로드하지 못했습니다",
            $"이 PC의 쪽지를 클라우드에 올리지 못했습니다 ({o}).\n그래도 메신저를 실행할까요?");
    }

    private async Task<LoginChoice> EnsureLoginAsync(ICloudStorage storage, CancellationToken ct)
    {
        if (!storage.RequiresLogin) return LoginChoice.Login;

        if (!await storage.HasLoginAsync(ct))
        {
            var choice = await _ui.AskLoginAsync(expired: _s.GoogleLoginUtc != null);
            if (choice != LoginChoice.Login) return choice;
            await DoLoginAsync(storage, ct);
            return LoginChoice.Login;
        }

        if (_s.GoogleLoginUtc is { } t)
        {
            var days = (int)(DateTime.UtcNow - t).TotalDays;
            if (days >= 6 && await _ui.AskRenewLoginAsync(days))
            {
                try
                {
                    await DoLoginAsync(storage, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Warn($"로그인 갱신 실패, 기존 로그인으로 계속합니다: {ex.Message}");
                }
            }
        }
        return LoginChoice.Login;
    }

    private async Task DoLoginAsync(ICloudStorage storage, CancellationToken ct)
    {
        Report("브라우저에서 구글 로그인을 해 주세요...");
        await storage.LoginAsync(ct);
        _s.GoogleLoginUtc = DateTime.UtcNow;
        _s.Save();
        Log.Info("구글 로그인 완료");
    }

    private async Task DownloadIntoLocalAsync(ICloudStorage storage, CloudState cloud, LocalState st, string reason, CancellationToken ct)
    {
        var db = _s.DbPath;
        var gz = Path.Combine(Paths.TempDir, $"{_s.UserId}-download.db.gz");
        var tmpDb = Path.Combine(Paths.TempDir, $"{_s.UserId}-download.db");
        var until = DateTime.UtcNow.AddSeconds(Math.Max(10, _s.DownloadWaitSeconds));

        while (true)
        {
            Step(2, $"받는 중... (v{cloud.Version}, {cloud.PcName}, {FileOps.Human(cloud.CompressedSize)})", 0, "받기");
            var size = cloud.CompressedSize;
            var dlProgress = new Progress<double>(p => StepPercent(p,
                $"받는 중... {FileOps.Human((long)(size * p))} / {FileOps.Human(size)} (v{cloud.Version}, {cloud.PcName})"));
            var ok = await storage.TryDownloadBlobAsync(cloud.BlobName, gz, dlProgress, ct);
            if (ok)
            {
                try
                {
                    Report("받은 파일 압축을 푸는 중...");
                    await Task.Run(() => FileOps.Gunzip(gz, tmpDb), ct);
                    if (await Task.Run(() => FileOps.Sha256(tmpDb), ct) == cloud.Sha256) break;
                }
                catch (InvalidDataException)
                {
                    // 아직 다 내려오지 않은 파일
                }
            }
            if (DateTime.UtcNow > until)
                throw new IOException("클라우드 파일이 아직 다 도착하지 않았습니다. 잠시 후 다시 시도해 주세요.");
            Report("클라우드 파일이 도착하기를 기다리는 중...");
            await Task.Delay(5000, ct);
        }

        if (_m.IsRunning()) throw new InvalidOperationException("메신저가 켜져 있어 덮어쓸 수 없습니다.");
        if (!await FileOps.WaitFileFreeAsync(db, TimeSpan.FromSeconds(20), ct))
            throw new IOException("쪽지 파일을 다른 프로그램이 사용 중입니다.");

        Report("이 PC 파일을 백업하고 덮어쓰는 중...");
        var backup = FileOps.Backup(db, Paths.BackupDir, _s.UserId, reason, _s.KeepLocalBackups);
        if (backup != null) Log.Info($"덮어쓰기 전 백업: {backup}");

        // 옛 파일의 보조 파일(-journal 등)이 새 파일에 적용되면 db가 깨지므로 반드시 지운다.
        FileOps.DeleteCompanionFiles(db);
        File.Copy(tmpDb, db, overwrite: true);
        if (FileOps.Sha256(db) != cloud.Sha256) throw new IOException("덮어쓴 파일이 클라우드와 다릅니다.");

        st.BaseVersion = cloud.Version;
        st.BaseHash = cloud.Sha256;
        st.LastSyncUtc = DateTime.UtcNow;
        st.PendingUpload = false;
        st.PendingConflict = false;
        st.LastError = null;
        st.Save();
        TryDelete(gz);
        TryDelete(tmpDb);
        Report($"최신 버전을 받았습니다 (v{cloud.Version}, {cloud.PcName} {FileOps.When(cloud.UploadedUtc)})");
    }

    private async Task SaveCloudCopyToConflictsAsync(ICloudStorage storage, CloudState cloud, CancellationToken ct)
    {
        try
        {
            var gz = Path.Combine(Paths.TempDir, "conflict.db.gz");
            if (!await storage.TryDownloadBlobAsync(cloud.BlobName, gz, null, ct)) return;
            var dest = Path.Combine(Paths.ConflictDir,
                $"{_s.UserId}_{DateTime.Now:yyyyMMdd-HHmmss}_클라우드v{cloud.Version}-{FileOps.SafeName(cloud.PcName)}-선택안함.db");
            await Task.Run(() => FileOps.Gunzip(gz, dest), ct);
            TryDelete(gz);
            Log.Info($"충돌: 이 PC 선택. 클라우드 파일 보관 → {dest}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"클라우드 사본을 보관하지 못했습니다 (클라우드 versions 폴더에는 남아 있습니다): {ex.Message}");
        }
    }

    // ───────────────────────── 메신저 종료 → 올리기 ─────────────────────────

    /// <summary>메신저를 끄고(켜져 있으면) 바뀐 내용을 올린다. unattended 면 확인 창을 띄우지 않는다.</summary>
    public async Task<PushOutcome> CloseAndPushAsync(string reason, bool unattended, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        PushOutcome result = PushOutcome.Failed;
        BeginSteps(Op.Push, $"{reason} → 업로드", PushSteps);
        try
        {
            var killed = false;
            if (!_m.IsRunning()) SkipStep(0, "메신저 종료 (이미 꺼짐)");
            if (_m.IsRunning())
            {
                LaunchedByUs = false;
                Step(0, $"메신저 종료 중 ({reason})...");
                var close = await _m.CloseAsync(allowKill: true, ct);
                if (!close.Closed)
                {
                    _ui.Notify("메신저를 끄지 못했습니다", "업로드하지 못했습니다.", error: true);
                    return result = PushOutcome.Failed;
                }
                killed = close.Result == CloseResult.Killed;
                if (killed) Log.Info("강제 종료했으므로 정밀 검사를 합니다.");
            }
            LaunchedByUs = false;

            if (!_s.IsComplete) return result = PushOutcome.Failed;

            var storage = _storage();
            PushOutcome o;
            try
            {
                if (storage.RequiresLogin && !await storage.HasLoginAsync(ct))
                    throw new NeedLoginException();
                o = await PushCoreAsync(storage, killed, ct);
            }
            catch (NeedLoginException ex)
            {
                MarkPending(ex.Message);
                _ui.Notify("업로드 대기 중", "구글 로그인이 필요합니다. 다음에 메신저를 실행할 때 로그인하면 올라갑니다.", error: true);
                return result = PushOutcome.Pending;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Error("업로드 실패", ex);
                MarkPending(ex.Message);
                _ui.Notify("업로드 실패", $"{ex.Message}\n잠시 후 자동으로 다시 시도합니다.", error: true);
                return result = PushOutcome.Pending;
            }

            if (o is PushOutcome.Uploaded or PushOutcome.NoChange or PushOutcome.Adopted)
            {
                await TryReleaseLockAsync(storage, ct);
                await TryUpdateDeviceAsync(storage, o == PushOutcome.Uploaded ? $"{reason} → 업로드" : $"{reason} (변경 없음)", ct);
            }

            switch (o)
            {
                case PushOutcome.Uploaded:
                    _ui.Notify("업로드 완료", $"쪽지 데이터를 올렸습니다 (v{LoadState().BaseVersion}).");
                    break;
                case PushOutcome.Conflict:
                    _ui.Notify("충돌 발생",
                        "다른 PC에서 먼저 올린 버전이 있어 업로드하지 않았습니다.\n다음에 메신저를 실행할 때 어느 쪽을 쓸지 묻습니다.", error: true);
                    break;
            }
            return result = o;
        }
        finally
        {
            FinishSteps(result is PushOutcome.Uploaded or PushOutcome.NoChange or PushOutcome.Adopted, result switch
            {
                PushOutcome.Uploaded => $"업로드를 마쳤습니다 (v{LoadState().BaseVersion})",
                PushOutcome.NoChange => "바뀐 내용이 없어 올리지 않았습니다",
                PushOutcome.Adopted => "클라우드와 같은 상태입니다",
                PushOutcome.Conflict => "충돌: 다른 PC가 먼저 올렸습니다. 다음 실행 때 선택합니다",
                PushOutcome.Pending => "업로드 실패: 나중에 다시 시도합니다",
                PushOutcome.Corrupt => "쪽지 파일 이상: 업로드를 중단했습니다",
                _ => "업로드하지 못했습니다",
            });
            _gate.Release();
        }
    }

    private async Task<PushOutcome> PushCoreAsync(ICloudStorage storage, bool fullCheck, CancellationToken ct)
    {
        var db = _s.DbPath;
        if (!File.Exists(db))
        {
            Log.Warn($"쪽지 파일이 없습니다: {db}");
            return PushOutcome.NoDb;
        }
        if (!await FileOps.WaitFileFreeAsync(db, TimeSpan.FromSeconds(20), ct))
            throw new IOException("쪽지 파일이 아직 사용 중입니다.");

        fullCheck |= FileOps.HasCompanionFiles(db);
        PushPhase(1, fullCheck ? "쪽지 파일 정밀 검사 중 (강제 종료 후)..." : "쪽지 파일 검사 중...");
        var sw = Stopwatch.StartNew();
        var (ok, msg) = await Task.Run(() => FileOps.CheckSqlite(db, fullCheck), ct);
        Log.Info($"검사 결과: {msg} ({sw.Elapsed.TotalSeconds:0.0}초, {(fullCheck ? "정밀" : "빠른")} 검사)");
        var st = LoadState();
        if (!ok)
        {
            st.LastError = "db 검사 실패: " + msg;
            st.Save();
            _ui.Notify("쪽지 파일 이상 — 업로드 중단",
                $"손상이 의심되어 올리지 않았습니다.\n{msg}\n백업 폴더의 이전 파일로 복구할 수 있습니다.", error: true);
            return PushOutcome.Corrupt;
        }

        Report("바뀐 내용이 있는지 클라우드와 비교하는 중...");
        var hash = await Task.Run(() => FileOps.Sha256(db), ct);
        var cloud = await ReadJsonAsync<CloudState>(storage, CloudFiles.Current, ct);

        if (cloud != null && cloud.Sha256 == hash)
        {
            var same = cloud.Version == st.BaseVersion && st.BaseHash == hash;
            st.BaseVersion = cloud.Version;
            st.BaseHash = hash;
            st.PendingUpload = false;
            st.PendingConflict = false;
            st.Save();
            SkipPushUpload("변경 없음");
            return same ? PushOutcome.NoChange : PushOutcome.Adopted;
        }
        if (cloud != null && hash == st.BaseHash)
        {
            if (st.PendingUpload) { st.PendingUpload = false; st.Save(); }
            SkipPushUpload("변경 없음");
            return PushOutcome.NoChange;
        }
        if (cloud != null && cloud.Version > st.BaseVersion)
        {
            Log.Warn($"충돌: 클라우드 v{cloud.Version}({cloud.PcName}) > 이 PC 기준 v{st.BaseVersion}, 이 PC도 바뀜");
            st.PendingUpload = false;
            st.PendingConflict = true;
            st.Save();
            SkipPushUpload("충돌로 보류");
            return PushOutcome.Conflict;
        }

        var newVersion = (cloud?.Version ?? 0) + 1;
        FileOps.Backup(db, Paths.BackupDir, _s.UserId, $"올리기v{newVersion}", _s.KeepLocalBackups);

        var gz = Path.Combine(Paths.TempDir, $"{_s.UserId}-upload.db.gz");
        PushPhase(2, $"압축 중... (원본 {FileOps.Human(new FileInfo(db).Length)})");
        await Task.Run(() => FileOps.Gzip(db, gz), ct);
        var size = new FileInfo(db).Length;
        var gzSize = new FileInfo(gz).Length;

        var blob = $"v{newVersion:D4}_{DateTime.Now:yyyyMMdd-HHmm}_{FileOps.SafeName(_s.PcName)}.db.gz";
        var lastPct = -1;
        var progress = new Progress<double>(p =>
        {
            var pct = (int)(p * 100);
            if (pct != lastPct) { lastPct = pct; StepPercent(p, $"업로드 중... {FileOps.Human((long)(gzSize * p))} / {FileOps.Human(gzSize)}"); }
        });
        PushPhase(3, $"업로드 중... (원본 {FileOps.Human(size)} → 압축 {FileOps.Human(gzSize)})", 0);
        sw.Restart();
        await storage.UploadBlobAsync(gz, blob, progress, ct);
        Log.Info($"업로드 끝: {blob} ({sw.Elapsed.TotalSeconds:0.0}초)");

        var newState = new CloudState
        {
            UserId = _s.UserId.Trim(),
            Version = newVersion,
            BlobName = blob,
            Sha256 = hash,
            Size = size,
            CompressedSize = gzSize,
            PcName = _s.PcName,
            DeviceId = _s.DeviceId,
            UploadedUtc = DateTime.UtcNow,
            AppVersion = AppVersion,
        };
        PushPhase(4, "최신 버전 정보 기록 중...");
        await storage.WriteTextAsync(CloudFiles.Current, JsonUtil.Serialize(newState), ct);

        st.BaseVersion = newVersion;
        st.BaseHash = hash;
        st.LastSyncUtc = DateTime.UtcNow;
        st.PendingUpload = false;
        st.PendingConflict = false;
        st.LastError = null;
        st.Save();
        TryDelete(gz);

        await PruneAsync(storage, newState, ct);
        Report($"업로드 완료 (v{newVersion})");
        return PushOutcome.Uploaded;
    }

    private void MarkPending(string error)
    {
        try
        {
            var st = LoadState();
            st.PendingUpload = true;
            st.LastError = error;
            st.Save();
        }
        catch (Exception ex)
        {
            Log.Error("상태 저장 실패", ex);
        }
    }

    /// <summary>
    /// 지울 버전 고르기: 최신(current)은 항상 남기고, 최근 keepRecent 개 + 지난 keepDailyDays 일은 날짜별로 하나(그날 마지막 것)씩 남긴다.
    /// 메신저는 로그인만 해도 파일이 바뀌어 잠글 때마다 버전이 생기므로, 개수만으로 자르면 기록이 너무 짧아진다.
    /// </summary>
    public static List<BlobInfo> SelectBlobsToDelete(IEnumerable<BlobInfo> blobs, string currentName, int keepRecent, int keepDailyDays, DateTime today)
    {
        var sorted = blobs.OrderByDescending(b => b.Version).ThenByDescending(b => b.Name, StringComparer.Ordinal).ToList();
        var keep = new HashSet<string> { currentName };
        foreach (var b in sorted.Take(Math.Max(1, keepRecent))) keep.Add(b.Name);
        if (keepDailyDays > 0)
        {
            var since = today.Date.AddDays(-keepDailyDays);
            foreach (var g in sorted.Where(b => b.Date >= since).GroupBy(b => b.Date))
                keep.Add(g.First().Name); // 정렬이 최신순이므로 그날의 마지막 버전
        }
        return sorted.Where(b => !keep.Contains(b.Name)).ToList();
    }

    private async Task PruneAsync(ICloudStorage storage, CloudState current, CancellationToken ct)
    {
        try
        {
            var blobs = await storage.ListBlobsAsync(ct);
            var old = SelectBlobsToDelete(blobs, current.BlobName, Math.Max(3, _s.KeepVersions), Math.Max(0, _s.KeepDailyDays), DateTime.Now);
            foreach (var b in old)
            {
                await storage.DeleteBlobAsync(b.Name, ct);
                Log.Info($"오래된 버전 삭제: {b.Name}");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"오래된 버전 정리 실패: {ex.Message}");
        }
    }

    // ───────────────────────── 기타 기능 ─────────────────────────

    /// <summary>메신저가 꺼진 것을 감지했을 때 (트레이에서 직접 종료한 경우 등).</summary>
    public Task<PushOutcome> OnMessengerExitedAsync() => CloseAndPushAsync("메신저 종료", unattended: true);

    /// <summary>예전 버전으로 되돌린다. 지금 내용을 먼저 올린 뒤, 고른 버전을 새 버전으로 다시 올린다.</summary>
    public async Task<bool> RestoreVersionAsync(BlobInfo blob, CancellationToken ct = default)
    {
        var wasRunning = _m.IsRunning();
        var first = await CloseAndPushAsync("되돌리기 준비", unattended: false, ct);
        if (first is PushOutcome.Failed or PushOutcome.Corrupt or PushOutcome.Conflict or PushOutcome.Pending)
        {
            _ui.Notify("되돌리기 취소", $"지금 내용을 먼저 올리지 못해 되돌리지 않았습니다 ({first}).", error: true);
            return false;
        }

        await _gate.WaitAsync(ct);
        try
        {
            var storage = _storage();
            var gz = Path.Combine(Paths.TempDir, "restore.db.gz");
            var tmpDb = Path.Combine(Paths.TempDir, "restore.db");
            Report($"{blob.Name} 받는 중...");
            if (!await storage.TryDownloadBlobAsync(blob.Name, gz, null, ct))
                throw new FileNotFoundException("클라우드에서 그 버전을 찾지 못했습니다.", blob.Name);
            await Task.Run(() => FileOps.Gunzip(gz, tmpDb), ct);
            var (ok, msg) = await Task.Run(() => FileOps.CheckSqlite(tmpDb, true), ct);
            if (!ok) throw new InvalidDataException("그 버전의 파일이 손상되어 있습니다: " + msg);

            var db = _s.DbPath;
            FileOps.Backup(db, Paths.BackupDir, _s.UserId, "되돌리기전", _s.KeepLocalBackups);
            FileOps.DeleteCompanionFiles(db);
            File.Copy(tmpDb, db, overwrite: true);
            TryDelete(gz);
            TryDelete(tmpDb);
            Log.Info($"되돌리기: {blob.Name} → 이 PC에 적용");

            var o = await PushCoreAsync(storage, fullCheck: false, ct);
            await TryReleaseLockAsync(storage, ct);
            await TryUpdateDeviceAsync(storage, $"되돌리기 ({blob.Name})", ct);
            _ui.Notify("되돌리기 완료", $"{blob.Name} 내용으로 되돌리고 새 버전으로 올렸습니다 ({o}).");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error("되돌리기 실패", ex);
            _ui.Notify("되돌리기 실패", ex.Message, error: true);
            return false;
        }
        finally
        {
            _gate.Release();
        }

        if (wasRunning) await PullAndLaunchAsync(true, ct);
        return true;
    }

    public async Task<Overview> GetOverviewAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            return await GetOverviewCoreAsync(ct);
        }
        finally
        {
            Log.Info($"상태 불러오기 {sw.Elapsed.TotalSeconds:0.0}초");
        }
    }

    private async Task<Overview> GetOverviewCoreAsync(CancellationToken ct)
    {
        var storage = _storage();
        if (storage.RequiresLogin && !await storage.HasLoginAsync(ct)) throw new NeedLoginException();
        var st = LoadState();
        return new Overview
        {
            Cloud = await ReadJsonAsync<CloudState>(storage, CloudFiles.Current, ct),
            Lock = await ReadJsonAsync<LockInfo>(storage, CloudFiles.Lock, ct),
            Devices = await ReadJsonAsync<Dictionary<string, DeviceRecord>>(storage, CloudFiles.Devices, ct) ?? new(),
            Versions = (await storage.ListBlobsAsync(ct)).OrderByDescending(b => b.Version).ToList(),
            State = st,
            Local = GetLocalInfo(st),
        };
    }

    public LocalInfo GetLocalInfo(LocalState? st = null)
    {
        st ??= LoadState();
        var fi = new FileInfo(_s.DbPath);
        return new LocalInfo
        {
            Exists = fi.Exists,
            Size = fi.Exists ? fi.Length : 0,
            ModifiedLocal = fi.Exists ? fi.LastWriteTime : null,
            BaseVersion = st.BaseVersion,
        };
    }

    public bool HasPendingUpload()
    {
        try
        {
            var st = LoadState();
            return st.PendingUpload && !st.PendingConflict;
        }
        catch
        {
            return false;
        }
    }

    // ───────────────────────── 클라우드 작은 파일들 ─────────────────────────

    private static async Task<T?> ReadJsonAsync<T>(ICloudStorage storage, string name, CancellationToken ct)
    {
        var text = await storage.ReadTextAsync(name, ct);
        try
        {
            return JsonUtil.Deserialize<T>(text);
        }
        catch (Exception ex)
        {
            Log.Warn($"{name} 내용을 읽지 못했습니다: {ex.Message}");
            return default;
        }
    }

    // ───────────────────────── 다른 PC에 업로드 요청 ─────────────────────────

    /// <summary>
    /// 다른 PC가 사용 중일 때. 업로드를 요청하면 그 PC 프로그램이 1분 안에 메신저를 끄고 올린다.
    /// 그 PC의 "사용 중" 표시가 풀리면 계속한다. true = 계속, false = 취소.
    /// </summary>
    private async Task<bool> ResolveOtherLockAsync(ICloudStorage storage, LockInfo other, CancellationToken ct)
    {
        var noResponse = false;
        while (true)
        {
            var choice = await _ui.AskLockAsync(other, noResponse);
            if (choice == LockChoice.Cancel) return false;
            if (choice == LockChoice.Continue)
            {
                Log.Warn($"{other.PcName} PC 사용 중 표시를 무시하고 계속합니다.");
                return true;
            }

            var req = new UploadRequest
            {
                FromDeviceId = _s.DeviceId,
                FromPcName = _s.PcName,
                TargetDeviceId = other.DeviceId,
                RequestedUtc = DateTime.UtcNow,
            };
            await storage.WriteTextAsync(CloudFiles.Request, JsonUtil.Serialize(req), ct);
            Log.Info($"{other.PcName} PC에 업로드 요청");

            var total = Math.Max(5, _s.RemoteWaitSeconds);
            var started = DateTime.UtcNow;
            while (true)
            {
                var elapsed = (DateTime.UtcNow - started).TotalSeconds;
                if (elapsed > total) break;
                Step(0, $"{other.PcName} PC에 업로드를 요청했습니다. 그 PC가 메신저를 끄고 올리기를 기다리는 중... ({elapsed:0}초)",
                    elapsed / total, $"{other.PcName} 업로드 기다리기");
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _s.RemotePollSeconds)), ct);
                var lk = await ReadJsonAsync<LockInfo>(storage, CloudFiles.Lock, ct);
                if (lk == null || lk.DeviceId != other.DeviceId)
                {
                    Log.Info($"{other.PcName} PC가 요청에 응답했습니다 ({(DateTime.UtcNow - started).TotalSeconds:0}초)");
                    await TryDeleteRequestAsync(storage, ct);
                    Step(0, $"{other.PcName} PC가 업로드를 마쳤습니다", 1, $"{other.PcName} 업로드 완료");
                    return true;
                }
            }
            await TryDeleteRequestAsync(storage, ct);
            Log.Warn($"{other.PcName} PC가 {total}초 동안 응답하지 않았습니다");
            noResponse = true;
        }
    }

    private async Task TryDeleteRequestAsync(ICloudStorage storage, CancellationToken ct)
    {
        try { await storage.DeleteTextAsync(CloudFiles.Request, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn($"요청 표시를 지우지 못했습니다: {ex.Message}"); }
    }

    /// <summary>
    /// (요청받는 쪽) 다른 PC가 이 PC에 업로드를 요청했는지 확인한다. 요청이 있으면 표시를 지우고 돌려준다.
    /// 업로드 자체는 호출한 쪽에서 CloseAndPushAsync 로 한다.
    /// </summary>
    public async Task<UploadRequest?> TakeUploadRequestAsync(CancellationToken ct = default)
    {
        if (Busy || !_s.IsComplete) return null;
        var storage = _storage();
        if (storage.RequiresLogin && !await storage.HasLoginAsync(ct)) return null;
        var req = await ReadJsonAsync<UploadRequest>(storage, CloudFiles.Request, ct);
        if (req == null || req.TargetDeviceId != _s.DeviceId) return null;
        await TryDeleteRequestAsync(storage, ct);
        if (DateTime.UtcNow - req.RequestedUtc > TimeSpan.FromMinutes(10))
        {
            Log.Info($"오래된 업로드 요청은 무시합니다 ({req.FromPcName}, {FileOps.When(req.RequestedUtc)})");
            return null;
        }
        Log.Info($"{req.FromPcName} PC에서 업로드 요청을 받았습니다");
        return req;
    }

    /// <summary>메신저를 쓰는 동안 "아직 사용 중" 신호를 남긴다. 다른 PC가 사용 중 표시를 넘겨받았으면 건드리지 않는다.</summary>
    public async Task HeartbeatAsync(CancellationToken ct = default)
    {
        if (Busy || !LaunchedByUs || !_m.IsRunning() || !_s.IsComplete) return;
        try
        {
            var storage = _storage();
            if (storage.RequiresLogin && !await storage.HasLoginAsync(ct)) return;
            var lk = await ReadJsonAsync<LockInfo>(storage, CloudFiles.Lock, ct);
            if (lk != null && lk.DeviceId != _s.DeviceId) return;
            var now = DateTime.UtcNow;
            var updated = new LockInfo { DeviceId = _s.DeviceId, PcName = _s.PcName, SinceUtc = lk?.SinceUtc ?? now, LastSeenUtc = now };
            await storage.WriteTextAsync(CloudFiles.Lock, JsonUtil.Serialize(updated), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"사용 중 신호를 남기지 못했습니다: {ex.Message}");
        }
    }

    private async Task TryWriteLockAsync(ICloudStorage storage, CancellationToken ct)
    {
        try
        {
            var now = DateTime.UtcNow;
            var lk = new LockInfo { DeviceId = _s.DeviceId, PcName = _s.PcName, SinceUtc = now, LastSeenUtc = now };
            await storage.WriteTextAsync(CloudFiles.Lock, JsonUtil.Serialize(lk), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"사용 중 표시를 남기지 못했습니다: {ex.Message}");
        }
    }

    private async Task TryReleaseLockAsync(ICloudStorage storage, CancellationToken ct)
    {
        try
        {
            var lk = await ReadJsonAsync<LockInfo>(storage, CloudFiles.Lock, ct);
            if (lk != null && lk.DeviceId == _s.DeviceId)
                await storage.DeleteTextAsync(CloudFiles.Lock, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"사용 중 표시를 지우지 못했습니다: {ex.Message}");
        }
    }

    private async Task TryUpdateDeviceAsync(ICloudStorage storage, string action, CancellationToken ct)
    {
        try
        {
            var all = await ReadJsonAsync<Dictionary<string, DeviceRecord>>(storage, CloudFiles.Devices, ct) ?? new();
            all[_s.DeviceId] = new DeviceRecord
            {
                DeviceId = _s.DeviceId,
                PcName = _s.PcName,
                LastAction = action,
                LastUtc = DateTime.UtcNow,
                Version = LoadState().BaseVersion,
            };
            await storage.WriteTextAsync(CloudFiles.Devices, JsonUtil.Serialize(all), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"기기 기록을 남기지 못했습니다: {ex.Message}");
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* 임시 파일 */ }
    }
}
