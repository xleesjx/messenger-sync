using Microsoft.Win32;
using MessengerSync.Core;
using MessengerSync.Platform;

namespace MessengerSync.UI;

/// <summary>
/// 트레이에 상주하며 다음을 처리한다.
///  - 시작 시: 동기화 후 메신저 실행
///  - 화면 잠금(Win+L): 메신저 종료 → 업로드
///  - 잠금 해제: 최신 확인 → 메신저 다시 실행
///  - 메신저를 직접 끄거나 켠 경우 감지
///  - 업로드 실패 시 5분마다 재시도
/// </summary>
public sealed class TrayApp : ApplicationContext
{
    private readonly Control _ui = new();
    private readonly NotifyIcon _tray = new();
    private readonly System.Windows.Forms.Timer _watch = new() { Interval = 3000 };
    private readonly ToolStripMenuItem _header = new() { Enabled = false };
    private readonly ToolStripMenuItem _pauseItem = new("자동 동기화 일시 중지") { CheckOnClick = true };
    private readonly ToolStripMenuItem _updateItem = new("업데이트 확인");
    private readonly System.Windows.Forms.Timer _updateTimer = new() { Interval = 60 * 60 * 1000 };
    private UpdateInfo? _pendingUpdate;
    private bool _updating;

    private AppSettings _s;
    private ICloudStorage? _storage;
    private MessengerController _messenger;
    private readonly WinUi _winUi;
    private SyncEngine _engine;

    private ProgressForm? _progress;
    private ProgressInfo? _lastProgress;
    private bool _screenLocked;
    private bool _closedByLock;
    private bool _testing;
    private bool _launching;
    private DateTime _lastRetryUtc = DateTime.MinValue;
    private SettingsForm? _settingsForm;
    private StatusForm? _statusForm;

    public TrayApp(string[] args, EventWaitHandle launchSignal)
    {
        _ui.CreateControl(); // UI 스레드로 작업을 넘기기 위한 핸들
        _s = AppSettings.Load();
        _s.Save();
        _winUi = new WinUi(ShowBalloon);
        _messenger = new MessengerController(_s);
        _engine = CreateEngine();

        BuildTray();
        SystemEvents.SessionSwitch += OnSessionSwitch;
        _watch.Tick += (_, _) => Watch();
        _watch.Start();

        // 바로가기로 한 번 더 실행하면 → 이미 떠 있는 프로그램이 메신저를 실행
        ThreadPool.RegisterWaitForSingleObject(launchSignal, (_, _) => RunOnUi(LaunchMessengerAsync), null, -1, false);

        Log.Info($"프로그램 시작 v{SyncEngine.AppVersion} ({_s.PcName}, {_s.Storage}){(args.Contains("--updated") ? " - 업데이트 후 재시작" : "")}");
        Updater.CleanupOld();
        if (args.Contains("--updated"))
            ShowBalloon("업데이트 완료", $"메신저 동기화 v{SyncEngine.AppVersion}(으)로 업데이트했습니다.", false);
        _ui.BeginInvoke(new Action(async () => await StartupAsync(args)));

        // 새 버전 자동 확인: 시작 1분 뒤 한 번, 이후 1시간마다 "마지막 확인이 하루 지났는지" 본다
        _updateTimer.Tick += (_, _) => RunOnUi(() => CheckForUpdateAsync(interactive: false));
        _updateTimer.Start();
        _ = Task.Delay(TimeSpan.FromMinutes(1)).ContinueWith(_ => RunOnUi(() => CheckForUpdateAsync(interactive: false)));
    }

    private SyncEngine CreateEngine()
    {
        var e = new SyncEngine(_s, GetStorage, _messenger, _winUi);
        e.Status += msg => RunOnUi(() =>
        {
            SetTrayText(msg);
            return Task.CompletedTask;
        });
        e.Progress += p => RunOnUi(() =>
        {
            _lastProgress = p;
            _progress?.Apply(p);
            return Task.CompletedTask;
        });
        return e;
    }

    private ICloudStorage GetStorage()
        => _storage ??= new OffThreadStorage(_s.Storage == StorageMode.Folder
            ? new FolderStorage(_s.FolderPath, _s.UserId.Trim())
            : new DriveStorage(_s));

    private void RunOnUi(Func<Task> action)
    {
        if (_ui.IsDisposed) return;
        _ui.BeginInvoke(new Action(async () =>
        {
            try { await action(); }
            catch (Exception ex) { Log.Error("처리 중 오류", ex); }
        }));
    }

    // ───────────── 트레이 ─────────────

    private void BuildTray()
    {
        var menu = new ContextMenuStrip { Font = new Font("Malgun Gothic", 9f) };
        var launch = new ToolStripMenuItem("메신저 실행 (동기화 후)", null, async (_, _) => await LaunchMessengerAsync())
        {
            Font = new Font("Malgun Gothic", 9f, FontStyle.Bold),
        };
        menu.Items.Add(_header);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(launch);
        menu.Items.Add("메신저 종료 후 업로드", null, async (_, _) => await CloseAndUploadAsync());
        menu.Items.Add("지금 동기화", null, async (_, _) => await SyncNowAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("상태 / 버전 목록", null, (_, _) => ShowStatus());
        menu.Items.Add("설정", null, (_, _) => ShowSettings());
        _updateItem.Click += async (_, _) =>
        {
            if (_pendingUpdate != null) await InstallUpdateAsync(_pendingUpdate);
            else await CheckForUpdateAsync(interactive: true);
        };
        menu.Items.Add(_updateItem);
        menu.Items.Add("기록 폴더 열기", null, (_, _) => OpenFolder(Paths.LogDir));
        menu.Items.Add(new ToolStripSeparator());
        _pauseItem.CheckedChanged += (_, _) =>
        {
            UpdateTrayIcon();
            Log.Info(_pauseItem.Checked ? "자동 동기화 일시 중지" : "자동 동기화 다시 시작");
        };
        menu.Items.Add(_pauseItem);
        menu.Items.Add("프로그램 종료", null, async (_, _) => await ExitAsync());
        menu.Opening += (_, _) => _header.Text = $"메신저 동기화 v{SyncEngine.AppVersion} · {(_s.PcName.Length > 0 ? _s.PcName : "PC 이름 없음")} · {_s.UserId}";

        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += async (_, _) => await LaunchMessengerAsync();
        _tray.Visible = true;
        UpdateTrayIcon();
        SetTrayText("대기 중");
    }

    private void SetTrayText(string msg)
    {
        var t = "메신저 동기화: " + msg;
        _tray.Text = t.Length > 63 ? t[..63] : t;
        UpdateTrayIcon();
    }

    private void UpdateTrayIcon()
    {
        TrayState state;
        if (_pauseItem.Checked) state = TrayState.Paused;
        else if (_engine.Busy) state = TrayState.Busy;
        else if (_engine.HasPendingUpload() || !_s.IsComplete) state = TrayState.Warning;
        else state = TrayState.Normal;
        _tray.Icon = IconFactory.Get(state);
    }

    private void ShowBalloon(string title, string text, bool error)
    {
        RunOnUi(() =>
        {
            _tray.BalloonTipTitle = title;
            _tray.BalloonTipText = text.Length > 250 ? text[..250] : (text.Length == 0 ? " " : text);
            _tray.BalloonTipIcon = error ? ToolTipIcon.Warning : ToolTipIcon.Info;
            _tray.ShowBalloonTip(error ? 10000 : 4000);
            UpdateTrayIcon();
            return Task.CompletedTask;
        });
    }

    // ───────────── 동작 ─────────────

    private async Task StartupAsync(string[] args)
    {
        if (!_s.IsComplete)
        {
            ShowBalloon("처음 설정이 필요합니다", "설정 화면에서 아이디, 쪽지 폴더, PC 이름, 저장 방식을 지정해 주세요.", false);
            ShowSettings();
            return;
        }
        // 업데이트로 다시 시작한 경우에는, 업데이트 전에 메신저가 켜져 있었을 때만(--launch) 다시 켠다
        var launch = args.Contains("--launch") || (_s.LaunchMessengerOnStart && !args.Contains("--updated"));
        if (launch) await LaunchMessengerAsync();
    }

    // ───────────── 업데이트 ─────────────

    private async Task CheckForUpdateAsync(bool interactive)
    {
        if (_updating) return;
        if (!interactive)
        {
            if (!_s.AutoCheckUpdates) return;
            if (_s.LastUpdateCheckUtc is { } last && DateTime.UtcNow - last < TimeSpan.FromHours(24)) return;
        }
        UpdateInfo? info;
        try
        {
            info = await Task.Run(() => Updater.CheckAsync(CancellationToken.None));
            _s.LastUpdateCheckUtc = DateTime.UtcNow;
            _s.Save();
        }
        catch (Exception ex)
        {
            Log.Warn($"업데이트 확인 실패: {ex.Message}");
            if (interactive) Dialogs.Error("업데이트를 확인하지 못했습니다", ex.Message);
            return;
        }

        if (info == null)
        {
            if (interactive) Dialogs.Info("최신 버전입니다", $"지금 쓰는 버전 v{SyncEngine.AppVersion}이(가) 최신입니다.");
            return;
        }

        _pendingUpdate = info;
        _updateItem.Text = $"업데이트 설치 (v{info.Version})";
        _updateItem.Font = new Font(_updateItem.Font, FontStyle.Bold);
        if (interactive)
        {
            await InstallUpdateAsync(info);
        }
        else if (_s.NotifiedUpdateVersion != info.Version.ToString())
        {
            _s.NotifiedUpdateVersion = info.Version.ToString();
            _s.Save();
            ShowBalloon($"새 버전 v{info.Version}이(가) 있습니다", "트레이 아이콘 메뉴에서 [업데이트 설치]를 눌러 주세요.", false);
        }
    }

    private async Task InstallUpdateAsync(UpdateInfo info)
    {
        if (_updating) return;
        if (_engine.Busy || _launching)
        {
            Dialogs.Info("동기화 중입니다", "동기화가 끝난 뒤에 다시 눌러 주세요.");
            return;
        }
        var notes = string.IsNullOrWhiteSpace(info.Notes) ? "" : $"\n\n바뀐 점:\n{info.Notes}";
        var wasRunning = _messenger.IsRunning();
        var r = Dialogs.Ask($"새 버전 v{info.Version}(으)로 업데이트할까요?",
            $"지금 버전: v{SyncEngine.AppVersion}{notes}\n\n" +
            (wasRunning ? "메신저를 종료하고 업로드한 뒤 업데이트합니다. 끝나면 프로그램과 메신저가 다시 켜집니다." : "업데이트가 끝나면 프로그램이 다시 켜집니다."),
            TaskDialogIcon.Information, "지금 업데이트", "나중에");
        if (r != 0) return;

        _updating = true;
        try
        {
            if (wasRunning) await PushWithProgress("업데이트 전", unattended: false);

            var steps = new ProgressInfo
            {
                Title = $"업데이트 v{info.Version}",
                Steps = new() { "새 버전 받기", "파일 확인", "프로그램 교체", "다시 시작" },
                States = new() { StepState.Running, StepState.Pending, StepState.Pending, StepState.Pending },
                Current = 0,
                Percent = 0,
                Detail = "GitHub에서 새 버전을 받는 중...",
            };
            using var form = new ProgressForm();
            form.Show();
            form.Apply(steps);
            var progress = new Progress<(double Percent, string Text)>(p =>
            {
                var stage = p.Text.StartsWith("받는") ? 0 : p.Text.Contains("확인") ? 1 : 2;
                for (var i = 0; i < stage; i++) steps.States[i] = StepState.Done;
                steps.States[stage] = StepState.Running;
                steps.Current = stage;
                steps.Percent = stage == 0 ? p.Percent : null;
                steps.Detail = p.Text;
                form.Apply(steps.Clone());
            });
            try
            {
                await Updater.InstallAsync(info, progress, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Log.Error("업데이트 실패", ex);
                steps.States[steps.Current] = StepState.Failed;
                steps.Finished = true;
                steps.Detail = ex.Message;
                form.Apply(steps.Clone());
                Dialogs.Error("업데이트하지 못했습니다", ex.Message + "\n\n지금 버전을 그대로 씁니다.");
                if (wasRunning) await LaunchMessengerAsync();
                return;
            }
            for (var i = 0; i < 3; i++) steps.States[i] = StepState.Done;
            steps.States[3] = StepState.Running;
            steps.Current = 3;
            steps.Detail = "새 버전으로 다시 시작합니다...";
            form.Apply(steps.Clone());
            await Task.Delay(800);

            Updater.Restart(launchMessenger: wasRunning);
            Log.Info("업데이트를 위해 프로그램 종료");
            _tray.Visible = false;
            ExitThread();
        }
        finally
        {
            _updating = false;
        }
    }

    /// <summary>진행 창을 띄운 채로 작업을 하고, 끝나면 결과를 잠깐 보여준 뒤 닫는다.</summary>
    private async Task<T> WithProgress<T>(Func<Task<T>> work)
    {
        var mine = _progress == null;
        if (mine)
        {
            _lastProgress = null;
            _progress = new ProgressForm();
            _progress.Show();
        }
        try
        {
            return await work();
        }
        finally
        {
            if (mine && _progress != null)
            {
                var failed = _lastProgress is { Finished: true, Success: false };
                await Task.Delay(failed ? 5000 : 1500); // 결과를 잠깐 보여준다
                _progress.Close();
                _progress.Dispose();
                _progress = null;
            }
        }
    }

    private async Task LaunchMessengerAsync()
    {
        if (_launching) return;
        _launching = true;
        try
        {
            await WithProgress(() => _engine.PullAndLaunchAsync(launch: true));
        }
        finally
        {
            _launching = false;
            UpdateTrayIcon();
            SetTrayText(_engine.LaunchedByUs ? "메신저 사용 중" : "대기 중");
        }
    }

    private Task<PushOutcome> PushWithProgress(string reason, bool unattended)
        => WithProgress(() => _engine.CloseAndPushAsync(reason, unattended));

    private async Task CloseAndUploadAsync()
    {
        if (!_messenger.IsRunning() && !_engine.HasPendingUpload())
        {
            var o0 = await PushWithProgress("수동 업로드", unattended: false);
            if (o0 == PushOutcome.NoChange) ShowBalloon("바뀐 내용이 없습니다", "클라우드와 같은 상태입니다.", false);
            return;
        }
        var o = await PushWithProgress("메뉴에서 종료", unattended: false);
        if (o == PushOutcome.NoChange) ShowBalloon("업로드할 변경이 없습니다", "메신저를 종료했습니다. 클라우드와 같은 상태입니다.", false);
        SetTrayText("대기 중");
    }

    private async Task SyncNowAsync()
    {
        var wasRunning = _messenger.IsRunning();
        if (wasRunning && !await _winUi.ConfirmAsync("메신저를 잠시 종료합니다", "동기화하려면 메신저를 껐다가 다시 켜야 합니다. 계속할까요?"))
            return;
        await WithProgress(async () =>
        {
            await _engine.CloseAndPushAsync("지금 동기화", unattended: false);
            return await _engine.PullAndLaunchAsync(launch: wasRunning);
        });
        UpdateTrayIcon();
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        RunOnUi(async () =>
        {
            switch (e.Reason)
            {
                case SessionSwitchReason.SessionLock:
                    _screenLocked = true;
                    Log.Info("화면 잠금 감지");
                    if (_pauseItem.Checked || !_s.SyncOnLock || !_s.IsComplete) return;
                    // 받기 확인 창이 떠 있는 중이면 메신저는 아직 안 켜졌다. 여기서 업로드를 예약하면
                    // 잠금 해제 후 메신저가 켜지자마자 꺼지므로 건너뛴다.
                    if (_launching) return;
                    var running = _messenger.IsRunning();
                    if (!running && !_engine.HasPendingUpload()) return;
                    _closedByLock = running;
                    await PushWithProgress("화면 잠금", unattended: true);
                    UpdateTrayIcon();
                    break;

                case SessionSwitchReason.SessionUnlock:
                    _screenLocked = false;
                    Log.Info("잠금 해제 감지");
                    if (_pauseItem.Checked) return;
                    if (_closedByLock && _s.RelaunchOnUnlock)
                    {
                        _closedByLock = false;
                        await LaunchMessengerAsync();
                    }
                    break;
            }
        });
    }

    /// <summary>3초마다: 메신저를 직접 껐는지/켰는지, 올리지 못한 변경이 있는지 확인.</summary>
    private void Watch()
    {
        if (_pauseItem.Checked || _testing || _launching || _screenLocked || _engine.Busy || !_s.IsComplete) return;
        var running = _messenger.IsRunning();

        if (_engine.LaunchedByUs && !running)
        {
            Log.Info("메신저가 종료된 것을 감지 → 업로드");
            RunOnUi(async () => { await WithProgress(_engine.OnMessengerExitedAsync); SetTrayText("대기 중"); });
        }
        else if (!_engine.LaunchedByUs && running)
        {
            Log.Info("동기화를 거치지 않고 켜진 메신저 감지");
            RunOnUi(LaunchMessengerAsync);
        }
        else if (!running && _engine.HasPendingUpload() && DateTime.UtcNow - _lastRetryUtc > TimeSpan.FromMinutes(5))
        {
            _lastRetryUtc = DateTime.UtcNow;
            Log.Info("올리지 못한 변경 재시도");
            RunOnUi(async () => { await _engine.CloseAndPushAsync("재시도", unattended: true); UpdateTrayIcon(); });
        }
    }

    // ───────────── 창 ─────────────

    private void ShowSettings()
    {
        if (_settingsForm != null) { _settingsForm.Activate(); return; }
        using var f = new SettingsForm(_s, GoogleLoginForSettingsAsync, CloseTestAsync);
        _settingsForm = f;
        try
        {
            if (f.ShowDialog() != DialogResult.OK) return;
            var wasComplete = _s.IsComplete;
            var idChanged = f.Result.UserId != _s.UserId;
            _s = f.Result;
            _s.Save();
            _storage = null;
            _messenger = new MessengerController(_s);
            var launched = _engine.LaunchedByUs && !idChanged;
            _engine = CreateEngine();
            if (launched && _messenger.IsRunning()) _engine.AdoptRunning(); // 실행 중인 메신저를 계속 맡는다
            Log.Info($"설정 저장 ({_s.PcName}, {_s.Storage}, {_s.UserId})");
            UpdateTrayIcon();
            if (!wasComplete && _s.IsComplete)
                RunOnUi(LaunchMessengerAsync); // 처음 설정을 마치면 바로 첫 동기화
        }
        finally
        {
            _settingsForm = null;
        }
    }

    private void ShowStatus()
    {
        if (_statusForm != null) { _statusForm.Activate(); return; }
        _statusForm = new StatusForm(_engine);
        _statusForm.FormClosed += (_, _) => { _statusForm.Dispose(); _statusForm = null; };
        _statusForm.Show();
    }

    private async Task<string> GoogleLoginForSettingsAsync(AppSettings temp)
    {
        var storage = new OffThreadStorage(new DriveStorage(temp));
        await storage.LoginAsync(CancellationToken.None);
        temp.GoogleLoginUtc = DateTime.UtcNow;
        var blobs = await storage.ListBlobsAsync(CancellationToken.None);
        // 실제 설정에도 바로 반영 (저장 버튼을 누르지 않아도 로그인은 유지)
        _s.GoogleLoginUtc = temp.GoogleLoginUtc;
        _s.DriveRootFolderId = temp.DriveRootFolderId;
        _s.Save();
        _storage = null;
        return $"학교 구글 드라이브에 접근할 수 있습니다.\n\n" +
               $"드라이브의 '{DriveStorage.RootFolderName}' 폴더에 저장됩니다. 이 폴더를 원하는 위치(예: 00 dev\\03_messenger)로 옮겨도 됩니다.\n" +
               $"지금 보관된 버전: {blobs.Count}개";
    }

    private async Task<string> CloseTestAsync(AppSettings temp)
    {
        var m = new MessengerController(temp);
        if (!m.IsRunning()) return "메신저가 실행 중이 아닙니다.\n메신저를 켜고 로그인한 다음 다시 시험해 주세요.";
        _testing = true;
        try
        {
            var structure = await Task.Run(m.Describe); // 끄기 전에 창 구조를 기록
            var r = await m.CloseAsync(allowKill: true, CancellationToken.None);
            _engine.ForgetLaunch();
            var db = temp.DbPath;
            var leftovers = File.Exists(db) && FileOps.HasCompanionFiles(db) ? "\n⚠ 보조 파일(-journal 등)이 남아 있습니다." : "";
            var check = File.Exists(db)
                ? await Task.Run(() => FileOps.CheckSqlite(db, r.Result == CloseResult.Killed))
                : (true, "db 없음");
            var how = r.Result switch
            {
                CloseResult.GracefulMenu => $"✔ {r.Detail}(으)로 정상 종료되었습니다. 가장 좋은 결과입니다.",
                CloseResult.GracefulSession => "✔ 1단계(윈도우 종료 신호)로 정상 종료되었습니다.",
                CloseResult.GracefulClose => "✔ 2단계(창 닫기 신호)로 정상 종료되었습니다.",
                CloseResult.Killed => "✔ 메신저를 종료했습니다 (강제 종료 방식).",
                CloseResult.NotRunning => "메신저가 이미 꺼져 있었습니다.",
                _ => "✖ 메신저를 끄지 못했습니다.",
            };

            var report = $"메신저 종료 시험 {DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n결과: {r.Result} / {r.Detail} / {r.Seconds:0.0}초 / 검사 {check.Item2}\r\n\r\n[메신저 창 구조]\r\n{structure.Replace("\n", "\r\n")}";
            var reportPath = Path.Combine(Paths.LogDir, "close-test.txt");
            File.WriteAllText(reportPath, report);
            try { Clipboard.SetText(report); } catch { /* 클립보드 실패는 무시 */ }
            Log.Info($"메신저 종료 시험: {r.Result}, {r.Detail}, {r.Seconds:0.0}초, 검사 {check.Item2}\n{structure}");

            return $"{how}\n걸린 시간: {r.Seconds:0.0}초\n쪽지 파일 검사: {check.Item2}{leftovers}\n\n" +
                   "메신저 창 구조를 진단 파일(logs\\close-test.txt)에 저장하고 클립보드에도 복사했습니다.\n" +
                   "대화창에 Ctrl+V 로 붙여넣어 보내 주세요.\n\n" +
                   "메신저는 꺼진 상태입니다. 트레이 메뉴의 '메신저 실행'으로 다시 켜 주세요.";
        }
        finally
        {
            _testing = false;
        }
    }

    private async Task ExitAsync()
    {
        if (_messenger.IsRunning() && _engine.LaunchedByUs)
        {
            var r = Dialogs.Ask("메신저가 켜져 있습니다",
                "이 프로그램을 끄면 화면 잠금 시 자동 업로드가 되지 않습니다.",
                TaskDialogIcon.Warning,
                "메신저 종료 + 업로드 후 끝내기", "프로그램만 끝내기", "취소");
            if (r == 2 || r < 0) return;
            if (r == 0) await PushWithProgress("프로그램 종료", unattended: false);
        }
        else if (_engine.Busy)
        {
            Dialogs.Info("동기화 중입니다", "끝난 뒤에 다시 시도해 주세요.");
            return;
        }
        Log.Info("프로그램 종료");
        _tray.Visible = false;
        ExitThread();
    }

    private static void OpenFolder(string path)
    {
        try { System.Diagnostics.Process.Start("explorer.exe", $"\"{path}\"")?.Dispose(); }
        catch { /* 무시 */ }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            _watch.Dispose();
            _tray.Dispose();
            _ui.Dispose();
        }
        base.Dispose(disposing);
    }
}
