using Microsoft.Data.Sqlite;
using MessengerSync.Core;

// PC 두 대(창의센터, 교수부)를 흉내 내서 폴더 저장소로 동기화 흐름을 시험한다.

var root = Path.Combine(Path.GetTempPath(), "msync-test-" + Guid.NewGuid().ToString("N")[..8]);
var cloudDir = Path.Combine(root, "cloud");
Directory.CreateDirectory(cloudDir);
var failures = 0;

void Check(bool cond, string name)
{
    Console.WriteLine($"{(cond ? "PASS" : "FAIL")}  {name}");
    if (!cond) failures++;
}

// ── 1. 판단 표 ──
{
    var cloud = new CloudState { Version = 5, Sha256 = "C5" };
    Check(SyncEngine.Decide(new LocalState(), null, null) == SyncAction.None, "판단: 양쪽 다 없음 → None");
    Check(SyncEngine.Decide(new LocalState(), cloud, null) == SyncAction.Download, "판단: 이 PC에 파일 없음 → Download");
    Check(SyncEngine.Decide(new LocalState(), null, "L") == SyncAction.InitialUpload, "판단: 처음 + 클라우드 비어 있음 → InitialUpload");
    Check(SyncEngine.Decide(new LocalState(), cloud, "C5") == SyncAction.AdoptCloud, "판단: 처음 + 내용 같음 → AdoptCloud");
    Check(SyncEngine.Decide(new LocalState(), cloud, "X") == SyncAction.FirstRunAsk, "판단: 처음 + 내용 다름 → FirstRunAsk");
    var st4 = new LocalState { BaseVersion = 4, BaseHash = "C4" };
    Check(SyncEngine.Decide(st4, cloud, "C4") == SyncAction.Download, "판단: 클라우드만 바뀜 → Download");
    Check(SyncEngine.Decide(st4, cloud, "B-new") == SyncAction.Conflict, "판단: 양쪽 다 바뀜(질문하신 11/4 상황) → Conflict");
    var st5 = new LocalState { BaseVersion = 5, BaseHash = "C5" };
    Check(SyncEngine.Decide(st5, cloud, "C5") == SyncAction.None, "판단: 그대로 → None");
    Check(SyncEngine.Decide(st5, cloud, "L-new") == SyncAction.Upload, "판단: 이 PC만 바뀜 → Upload");
    Check(SyncEngine.Decide(st5, null, "C5") == SyncAction.Upload, "판단: 클라우드가 사라짐 → Upload");
}

// ── 1-2. 버전 보관 규칙: 최근 N개 + 지난 30일은 하루 1개 ──
{
    var list = new List<BlobInfo>();
    var v = 0;
    var start = new DateTime(2026, 10, 1);
    for (var day = 0; day < 40; day++)        // 40일 동안 하루 5번씩 잠금
        for (var k = 0; k < 5; k++)
            list.Add(new BlobInfo { Name = $"v{++v:D4}_{start.AddDays(day):yyyyMMdd}-{9 + k:D2}00_창의센터.db.gz" });
    var today = start.AddDays(39);
    var current = list[^1].Name;
    var del = SyncEngine.SelectBlobsToDelete(list, current, 20, 30, today).Select(b => b.Name).ToHashSet();
    var kept = list.Where(b => !del.Contains(b.Name)).ToList();
    Check(kept.Any(b => b.Name == current), "보관: 최신 버전은 항상 남김");
    Check(list.TakeLast(20).All(b => !del.Contains(b.Name)), "보관: 최근 20개는 모두 남김");
    var since = today.AddDays(-30);
    var days = kept.Where(b => b.Date >= since).Select(b => b.Date).Distinct().Count();
    Check(days == 31, $"보관: 지난 30일은 날짜마다 하나씩 남김 ({days}일)");
    Check(kept.Where(b => b.Date < since.AddDays(-1)).Count() == 0, "보관: 30일보다 오래된 것은 지움");
    Check(kept.Count == 20 + 27, $"보관: 전체 {kept.Count}개 (최근 20 + 그 이전 날짜별 27)");
}

// ── 2. PC 두 대 시나리오 ──
var pcA = new FakePc(root, "창의센터", cloudDir);
var pcB = new FakePc(root, "교수부", cloudDir);
pcA.CreateDb(10);           // A에 쪽지 10개
pcB.CreateDb(3);            // B에는 예전 쪽지 3개

// A 처음 실행 → 클라우드 비어 있으니 올림
pcA.Use();
Check(await pcA.Engine.PullAndLaunchAsync(true), "A 첫 실행 성공");
Check(pcA.Messenger.Running, "A 메신저 켜짐");
Check(pcA.ReadCloud()?.Version == 1, "A 첫 업로드 → 클라우드 v1");
Check(pcA.ReadLock()?.PcName == "창의센터", "A 사용 중 표시");

// A 쪽지 추가 후 Win+L
pcA.AddMessages(5);
var o = await pcA.Engine.CloseAndPushAsync("화면 잠금", unattended: true);
Check(o == PushOutcome.Uploaded, $"A 잠금 → 업로드 ({o})");
Check(!pcA.Messenger.Running, "A 메신저 꺼짐");
Check(pcA.ReadCloud()?.Version == 2, "클라우드 v2");
Check(pcA.ReadLock() == null, "A 사용 중 표시 해제");

// B 처음 실행 → 처음 + 내용 다름 → 클라우드 사용 선택
pcB.Use();
pcB.Ui.ConflictAnswer = ConflictChoice.UseCloud;
Check(await pcB.Engine.PullAndLaunchAsync(true), "B 첫 실행 성공");
Check(pcB.Ui.Asked.Contains("FirstRun"), "B: 처음 설정 질문을 받음");
Check(pcB.CountMessages() == 15, $"B: A의 쪽지 15개를 받음 ({pcB.CountMessages()})");
Check(Directory.GetFiles(Path.Combine(pcB.Root, "conflicts")).Length == 1, "B: 원래 B 파일은 conflicts에 보관");

// B에서 작업 후 잠금 → 업로드 v3
pcB.AddMessages(2);
Check(await pcB.Engine.CloseAndPushAsync("화면 잠금", true) == PushOutcome.Uploaded, "B 잠금 → 업로드");
Check(pcB.ReadCloud()?.Version == 3 && pcB.ReadCloud()?.PcName == "교수부", "클라우드 v3 (교수부)");

// A 잠금 해제 → 클라우드가 새것 → 받기 질문 → 받기
pcA.Use();
pcA.Ui.Asked.Clear();
Check(await pcA.Engine.PullAndLaunchAsync(true), "A 잠금 해제 → 실행");
Check(pcA.Ui.Asked.Contains("Download"), "A: '최신 데이터를 받을까요' 질문");
Check(pcA.CountMessages() == 17, $"A: 17개로 최신화 ({pcA.CountMessages()})");
Check(Directory.GetFiles(Path.Combine(pcA.Root, "backup"), "*받기전*").Length == 1, "A: 덮어쓰기 전 로컬 백업");

// 변경 없이 잠금 → 업로드 안 함
Check(await pcA.Engine.CloseAndPushAsync("화면 잠금", true) == PushOutcome.NoChange, "A: 바뀐 게 없으면 올리지 않음");
Check(pcA.ReadCloud()?.Version == 3, "클라우드는 그대로 v3");

// ── 진행 단계 표시 ──
{
    var seen = new List<ProgressInfo>();
    pcA.Engine.Progress += seen.Add;
    pcA.Use();
    await pcA.Engine.PullAndLaunchAsync(true);   // 변경 없음 → 실행
    var last = seen[^1];
    Console.WriteLine("      받기: " + string.Join(" | ", last.Steps.Zip(last.States, (a, b) => $"{a}:{b}")));
    Check(last.Finished && last.Success, "진행: 실행 작업 성공으로 끝남");
    Check(last.States[2] == StepState.Skipped && last.States[3] == StepState.Done, "진행: 변경 없음이면 동기화 건너뜀, 메신저 실행 완료");
    seen.Clear();
    pcA.AddMessages(1);
    await pcA.Engine.CloseAndPushAsync("화면 잠금", true);
    last = seen[^1];
    Console.WriteLine("      잠금: " + string.Join(" | ", last.Steps.Zip(last.States, (a, b) => $"{a}:{b}")));
    Check(last.Success && last.States.All(x => x == StepState.Done), "진행: 잠금 업로드는 5단계 모두 완료");
    Check(seen.Any(x => x.Current == 3 && x.Percent is > 0 and <= 1), "진행: 업로드 단계에 진행률 표시");
    seen.Clear();
    await pcA.Engine.PullAndLaunchAsync(true);
    await pcA.Engine.CloseAndPushAsync("화면 잠금", true); // 변경 없음
    last = seen[^1];
    Console.WriteLine("      변경없음: " + string.Join(" | ", last.Steps.Zip(last.States, (a, b) => $"{a}:{b}")));
    Check(last.States[2] == StepState.Skipped && last.States[3] == StepState.Skipped, "진행: 변경 없으면 압축·업로드 건너뜀");
    pcA.Engine.Progress -= seen.Add;
}

// ── 질문하신 상황: A에서 올린 뒤, B가 동기화 없이 메신저를 켜서 부재중 쪽지를 받음 ──
pcA.Use();
await pcA.Engine.PullAndLaunchAsync(true);
pcA.AddMessages(4);                                   // 11/3 A에서 작업
await pcA.Engine.CloseAndPushAsync("퇴근 잠금", true); // v4 업로드
pcB.Use();
pcB.Messenger.Running = true;                          // B: 런처 없이 메신저가 먼저 켜짐
pcB.AddMessages(1);                                    // 부재중 쪽지 1개 들어옴
pcB.Ui.Asked.Clear();
pcB.Ui.AlreadyRunningAnswer = true;                    // "끄고 동기화할까요?" → 예
pcB.Ui.ConflictAnswer = ConflictChoice.UseCloud;
Check(await pcB.Engine.PullAndLaunchAsync(true), "B: 동기화 없이 켜진 메신저 감지 후 처리");
Check(pcB.Ui.Asked.Contains("AlreadyRunning"), "B: '이미 켜져 있음' 질문");
Check(pcB.Ui.Asked.Contains("Conflict"), "B: 시간으로 판단하지 않고 충돌로 처리");
Check(pcB.CountMessages() == 22, $"B: 클라우드(A) 선택 → 22개 ({pcB.CountMessages()})");
Check(Directory.GetFiles(Path.Combine(pcB.Root, "conflicts")).Length == 2, "B: 부재중 쪽지가 든 B 파일은 conflicts에 보관");

// ── 잠금 중 충돌: 무인 상태에서는 덮어쓰지 않음 ──
pcB.AddMessages(1);                                    // B 작업 중 (메신저 켜짐)
pcA.Use();
pcA.Messenger.Running = false;
pcA.AddMessages(1);                                    // A도 몰래 바뀜
var oa = await pcA.Engine.CloseAndPushAsync("화면 잠금", true);
Check(oa == PushOutcome.Uploaded, "A가 먼저 올림 (v5)");
pcB.Use();
var ob = await pcB.Engine.CloseAndPushAsync("화면 잠금", true);
Check(ob == PushOutcome.Conflict, $"B 잠금 업로드 → 충돌로 보류 ({ob})");
Check(pcB.ReadCloud()?.PcName == "창의센터", "클라우드는 A 버전 그대로 (덮어쓰지 않음)");
Check(pcB.Ui.Notes.Any(n => n.Contains("충돌")), "B: 충돌 알림");
pcB.Ui.ConflictAnswer = ConflictChoice.UseLocal;
Check(await pcB.Engine.PullAndLaunchAsync(true), "B 다음 실행 → 이 PC 선택");
Check(pcB.ReadCloud()?.Version == 7 && pcB.ReadCloud()?.PcName == "교수부", "B 버전이 v7로 올라감");

// ── 다른 PC 사용 중 경고 ──
pcA.Use();
pcA.Ui.Asked.Clear();
pcA.Ui.LockAnswer = false;
Check(!await pcA.Engine.PullAndLaunchAsync(true), "A: 교수부 사용 중 경고에서 취소하면 실행 안 함");
Check(pcA.Ui.Asked.Contains("Lock"), "A: 사용 중 경고를 받음");
pcB.Use();
await pcB.Engine.CloseAndPushAsync("화면 잠금", true);

// ── 손상된 db는 올리지 않음 ──
pcA.Use();
pcA.Ui.LockAnswer = true;
await pcA.Engine.PullAndLaunchAsync(true);
await pcA.Engine.CloseAndPushAsync("잠금", true);
var before = pcA.ReadCloud()!.Version;
pcA.Corrupt();
Check(await pcA.Engine.CloseAndPushAsync("잠금", true) == PushOutcome.Corrupt, "손상된 db 감지 → 업로드 중단");
Check(pcA.ReadCloud()!.Version == before, "클라우드는 그대로");

// ── 버전 보관 개수 ──
var pcC = new FakePc(root, "노트북", cloudDir, keepVersions: 3);
pcC.S.KeepDailyDays = 0;
pcC.CreateDb(1);
pcC.Use();
pcC.Ui.ConflictAnswer = ConflictChoice.UseLocal;
await pcC.Engine.PullAndLaunchAsync(true);
for (var i = 0; i < 4; i++) { pcC.AddMessages(1); await pcC.Engine.CloseAndPushAsync("잠금", true); await pcC.Engine.PullAndLaunchAsync(true); }
await pcC.Engine.CloseAndPushAsync("잠금", true);
var blobs = Directory.GetFiles(Path.Combine(cloudDir, "SN200662", "versions"), "*.db.gz");
Check(blobs.Length == 3, $"버전은 최근 3개만 보관 ({blobs.Length}개)");

Console.WriteLine(failures == 0 ? "\n모든 시험 통과" : $"\n실패 {failures}건");
try { Directory.Delete(root, true); } catch { }
return failures == 0 ? 0 : 1;

// ───────────── 시험용 가짜 PC ─────────────

sealed class FakePc
{
    public string Root { get; }
    public AppSettings S { get; }
    public FakeMessenger Messenger { get; } = new();
    public FakeUi Ui { get; } = new();
    public SyncEngine Engine { get; }

    public FakePc(string baseDir, string pcName, string cloudDir, int keepVersions = 20)
    {
        Root = Path.Combine(baseDir, pcName);
        var data = Path.Combine(Root, "history");
        Directory.CreateDirectory(data);
        S = new AppSettings
        {
            UserId = "SN200662", DataDir = data, PcName = pcName, Storage = StorageMode.Folder,
            FolderPath = cloudDir, MessengerExe = typeof(FakePc).Assembly.Location, KeepVersions = keepVersions,
            DownloadWaitSeconds = 10,
        };
        Engine = new SyncEngine(S, () => new FolderStorage(S.FolderPath, S.UserId), Messenger, Ui);
    }

    /// <summary>이 PC로 "자리를 옮김": 로컬 폴더 위치를 이 PC 것으로 바꾼다.</summary>
    public void Use() => Paths.Root = Root;

    private string Cs => new SqliteConnectionStringBuilder { DataSource = S.DbPath, Pooling = false }.ToString();

    public void CreateDb(int n)
    {
        using var c = new SqliteConnection(Cs);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "CREATE TABLE SENDMSG7 (id INTEGER PRIMARY KEY, body TEXT);";
        cmd.ExecuteNonQuery();
        AddMessages(n);
    }

    public void AddMessages(int n)
    {
        using var c = new SqliteConnection(Cs);
        c.Open();
        for (var i = 0; i < n; i++)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO SENDMSG7 (body) VALUES ($b)";
            cmd.Parameters.AddWithValue("$b", $"{S.PcName} 쪽지 {Guid.NewGuid()}");
            cmd.ExecuteNonQuery();
        }
    }

    public int CountMessages()
    {
        using var c = new SqliteConnection(Cs);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM SENDMSG7";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void Corrupt()
    {
        var bytes = File.ReadAllBytes(S.DbPath);
        for (var i = 100; i < bytes.Length; i += 7) bytes[i] = 0xFF;
        File.WriteAllBytes(S.DbPath, bytes);
    }

    public CloudState? ReadCloud()
        => JsonUtil.Deserialize<CloudState>(new FolderStorage(S.FolderPath, S.UserId).ReadTextAsync(CloudFiles.Current, default).Result);

    public LockInfo? ReadLock()
        => JsonUtil.Deserialize<LockInfo>(new FolderStorage(S.FolderPath, S.UserId).ReadTextAsync(CloudFiles.Lock, default).Result);
}

sealed class FakeMessenger : IMessenger
{
    public bool Running { get; set; }
    public bool IsRunning() => Running;
    public void Launch() => Running = true;
    public Task<CloseReport> CloseAsync(bool allowKill, CancellationToken ct)
    {
        var was = Running;
        Running = false;
        return Task.FromResult(new CloseReport(was ? CloseResult.GracefulSession : CloseResult.NotRunning, 0.1));
    }
}

sealed class FakeUi : IUserInteraction
{
    public List<string> Asked { get; } = new();
    public List<string> Notes { get; } = new();
    public ConflictChoice ConflictAnswer { get; set; } = ConflictChoice.UseCloud;
    public bool AlreadyRunningAnswer { get; set; } = true;
    public bool LockAnswer { get; set; } = true;

    public Task<bool> AskMessengerAlreadyRunningAsync() { Asked.Add("AlreadyRunning"); return Task.FromResult(AlreadyRunningAnswer); }
    public Task<LoginChoice> AskLoginAsync(bool expired) { Asked.Add("Login"); return Task.FromResult(LoginChoice.Login); }
    public Task<bool> AskRenewLoginAsync(int days) { Asked.Add("Renew"); return Task.FromResult(false); }
    public Task<bool> AskLaunchOfflineAsync(string error) { Asked.Add("Offline:" + error); return Task.FromResult(false); }
    public Task<bool> ConfirmLockOverrideAsync(LockInfo other) { Asked.Add("Lock"); return Task.FromResult(LockAnswer); }
    public Task<DownloadChoice> AskDownloadAsync(CloudState c, LocalInfo l) { Asked.Add("Download"); return Task.FromResult(DownloadChoice.Download); }
    public Task<ConflictChoice> AskConflictAsync(CloudState c, LocalInfo l, bool first) { Asked.Add(first ? "FirstRun" : "Conflict"); return Task.FromResult(ConflictAnswer); }
    public Task<bool> ConfirmAsync(string h, string t) { Asked.Add("Confirm:" + h); return Task.FromResult(true); }
    public void Notify(string title, string text, bool error = false) { Notes.Add($"{title}: {text}"); Console.WriteLine($"      [알림] {title}"); }
}
