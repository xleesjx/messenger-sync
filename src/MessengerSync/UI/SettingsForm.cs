using MessengerSync.Core;

namespace MessengerSync.UI;

/// <summary>설정 화면. PC마다 따로 저장된다.</summary>
public sealed class SettingsForm : Form
{
    private readonly AppSettings _s;
    private readonly Func<AppSettings, Task<string>> _googleLogin;
    private readonly Func<AppSettings, Task<string>> _closeTest;

    private readonly TextBox _userId = new();
    private readonly Label _userIdInfo = new() { AutoSize = true, ForeColor = Color.DimGray };
    private readonly TextBox _dataDir = new();
    private readonly TextBox _exe = new();
    private readonly TextBox _proc = new();
    private readonly ComboBox _pcName = new() { DropDownStyle = ComboBoxStyle.DropDown };
    private readonly RadioButton _modeApi = new() { Text = "구글 드라이브 API (추천)", AutoSize = true };
    private readonly RadioButton _modeFolder = new() { Text = "폴더 지정 (G: 드라이브 등)", AutoSize = true };
    private readonly TextBox _secret = new();
    private readonly TextBox _hint = new();
    private readonly Label _loginInfo = new() { AutoSize = true, ForeColor = Color.DimGray };
    private readonly TextBox _folder = new();
    private readonly NumericUpDown _keepVersions = new() { Minimum = 3, Maximum = 200, Width = 70 };
    private readonly NumericUpDown _keepDaily = new() { Minimum = 0, Maximum = 365, Width = 70 };
    private readonly NumericUpDown _keepBackups = new() { Minimum = 1, Maximum = 100, Width = 70 };
    private readonly CheckBox _syncOnLock = new() { Text = "화면 잠금(Win+L) 시 메신저를 끄고 업로드", AutoSize = true };
    private readonly CheckBox _relaunch = new() { Text = "잠금 해제 시 최신 데이터를 확인하고 메신저 다시 실행", AutoSize = true };
    private readonly CheckBox _launchOnStart = new() { Text = "이 프로그램이 시작될 때 메신저도 실행 (동기화 후)", AutoSize = true };
    private readonly CheckBox _askDownload = new() { Text = "클라우드가 더 새로우면 받기 전에 묻기", AutoSize = true };
    private readonly CheckBox _autoStart = new() { Text = "윈도우 시작 시 이 프로그램 실행", AutoSize = true };
    private readonly CheckBox _autoUpdate = new() { Text = "새 버전 자동 확인 (하루 1번, 알림만)", AutoSize = true };

    private Control[] _apiRows = Array.Empty<Control>();
    private Control[] _folderRows = Array.Empty<Control>();

    public AppSettings Result => _s;

    public SettingsForm(AppSettings current, Func<AppSettings, Task<string>> googleLogin, Func<AppSettings, Task<string>> closeTest)
    {
        _s = current.Clone();
        _googleLogin = googleLogin;
        _closeTest = closeTest;

        Text = "메신저 동기화 설정";
        Font = new Font("Malgun Gothic", 9f);
        AutoScaleMode = AutoScaleMode.Font;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);
        Icon = IconFactory.Get(TrayState.Normal);

        var grid = new TableLayoutPanel
        {
            ColumnCount = 3,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 380));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        foreach (var t in new[] { _userId, _dataDir, _exe, _proc, _secret, _hint, _folder })
            t.Dock = DockStyle.Fill;
        _pcName.Dock = DockStyle.Fill;
        _pcName.Items.AddRange(AppSettings.SuggestedPcNames);

        void Section(string title)
        {
            var l = new Label { Text = title, AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 12, 0, 4) };
            grid.Controls.Add(l);
            grid.SetColumnSpan(l, 3);
        }
        Control[] Row(string label, Control input, Control? extra = null)
        {
            var l = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0) };
            grid.Controls.Add(l);
            grid.Controls.Add(input);
            var e = extra ?? new Label { AutoSize = true };
            grid.Controls.Add(e);
            return new[] { l, input, e };
        }
        Control Span(Control c)
        {
            grid.Controls.Add(new Label { AutoSize = true });
            grid.Controls.Add(c);
            grid.SetColumnSpan(c, 2);
            return c;
        }
        FlowLayoutPanel Buttons(params Button[] bs)
        {
            var p = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            p.Controls.AddRange(bs);
            return p;
        }
        Button Btn(string text, EventHandler onClick)
        {
            var b = new Button { Text = text, AutoSize = true };
            b.Click += onClick;
            return b;
        }

        Section("쪽지 파일");
        Row("아이디", _userId);
        Span(_userIdInfo);
        Row("쪽지 폴더", _dataDir, Buttons(Btn("자동 찾기", (_, _) => AutoFind()), Btn("찾아보기", (_, _) => BrowseFolder(_dataDir))));
        Row("메신저 실행파일", _exe, Btn("찾아보기", (_, _) => BrowseExe()));
        Row("프로세스 이름", _proc);
        Row("이 PC 이름", _pcName);

        Section("클라우드 저장 방식");
        var modes = new FlowLayoutPanel { AutoSize = true, Margin = Padding.Empty };
        modes.Controls.Add(_modeApi);
        modes.Controls.Add(_modeFolder);
        Span(modes);
        var r1 = Row("client_secret.json", _secret, Btn("찾아보기", (_, _) => BrowseJson()));
        var r2 = Row("구글 계정 (선택)", _hint);
        var loginBtn = Btn("구글 로그인", async (_, _) => await DoLogin());
        var r3 = Row("로그인", Buttons(loginBtn), _loginInfo);
        _apiRows = r1.Concat(r2).Concat(r3).ToArray();
        _folderRows = Row("동기화 폴더", _folder, Btn("찾아보기", (_, _) => BrowseFolder(_folder)));

        Section("보관");
        Row("클라우드 버전 수", _keepVersions, new Label { Text = "개 (최근 것부터)", AutoSize = true, Anchor = AnchorStyles.Left });
        Row("하루 1개씩 보관", _keepDaily, new Label { Text = "일 (최근 것 이전의 기록)", AutoSize = true, Anchor = AnchorStyles.Left });
        Row("이 PC 백업 수", _keepBackups, new Label { Text = "개", AutoSize = true, Anchor = AnchorStyles.Left });

        Section("자동 동작");
        foreach (var c in new Control[] { _syncOnLock, _relaunch, _launchOnStart, _askDownload, _autoStart, _autoUpdate })
            Span(c);

        Section("시험");
        Span(Buttons(Btn("메신저 종료 시험", async (_, _) => await DoCloseTest()),
            Btn("기록 폴더 열기", (_, _) => OpenFolder(Paths.LogDir)),
            Btn("백업 폴더 열기", (_, _) => OpenFolder(Paths.BackupDir))));

        var ok = new Button { Text = "저장", AutoSize = true, DialogResult = DialogResult.None };
        var cancel = new Button { Text = "취소", AutoSize = true, DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) => Save();
        var bottom = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0, 14, 0, 0) };
        bottom.Controls.Add(cancel);
        bottom.Controls.Add(ok);
        grid.Controls.Add(bottom);
        grid.SetColumnSpan(bottom, 3);

        Controls.Add(grid);
        AcceptButton = ok;
        CancelButton = cancel;

        LoadValues();
        _userId.TextChanged += (_, _) => UpdateUserInfo();
        _dataDir.TextChanged += (_, _) => UpdateUserInfo();
        _modeApi.CheckedChanged += (_, _) => UpdateMode();
        UpdateMode();
        UpdateUserInfo();
        UpdateLoginInfo();
    }

    private void LoadValues()
    {
        _userId.Text = _s.UserId;
        _dataDir.Text = string.IsNullOrWhiteSpace(_s.DataDir) ? DataDirFinder.Find(_s.UserId) ?? "" : _s.DataDir;
        _exe.Text = _s.MessengerExe;
        _proc.Text = _s.ProcessName;
        _pcName.Text = _s.PcName;
        _modeApi.Checked = _s.Storage == StorageMode.GoogleApi;
        _modeFolder.Checked = _s.Storage == StorageMode.Folder;
        _secret.Text = _s.EffectiveClientSecretPath;
        _hint.Text = _s.GoogleAccountHint;
        _folder.Text = _s.FolderPath;
        _keepVersions.Value = Math.Clamp(_s.KeepVersions, 3, 200);
        _keepBackups.Value = Math.Clamp(_s.KeepLocalBackups, 1, 100);
        _keepDaily.Value = Math.Clamp(_s.KeepDailyDays, 0, 365);
        _syncOnLock.Checked = _s.SyncOnLock;
        _relaunch.Checked = _s.RelaunchOnUnlock;
        _launchOnStart.Checked = _s.LaunchMessengerOnStart;
        _askDownload.Checked = _s.AskBeforeDownload;
        _autoUpdate.Checked = _s.AutoCheckUpdates;
        try { _autoStart.Checked = AutoStart.IsEnabled(); } catch { _autoStart.Checked = false; }
    }

    private void ReadValues(AppSettings s)
    {
        s.UserId = _userId.Text.Trim();
        s.DataDir = _dataDir.Text.Trim();
        s.MessengerExe = _exe.Text.Trim();
        s.ProcessName = Path.GetFileNameWithoutExtension(_proc.Text.Trim());
        s.PcName = _pcName.Text.Trim();
        s.Storage = _modeApi.Checked ? StorageMode.GoogleApi : StorageMode.Folder;
        var defaultSecret = Path.Combine(AppContext.BaseDirectory, "client_secret.json");
        s.ClientSecretPath = string.Equals(_secret.Text.Trim(), defaultSecret, StringComparison.OrdinalIgnoreCase) ? "" : _secret.Text.Trim();
        s.GoogleAccountHint = _hint.Text.Trim();
        s.FolderPath = _folder.Text.Trim();
        s.KeepVersions = (int)_keepVersions.Value;
        s.KeepLocalBackups = (int)_keepBackups.Value;
        s.KeepDailyDays = (int)_keepDaily.Value;
        s.SyncOnLock = _syncOnLock.Checked;
        s.RelaunchOnUnlock = _relaunch.Checked;
        s.LaunchMessengerOnStart = _launchOnStart.Checked;
        s.AskBeforeDownload = _askDownload.Checked;
        s.AutoCheckUpdates = _autoUpdate.Checked;
    }

    private void UpdateMode()
    {
        foreach (var c in _apiRows) c.Visible = _modeApi.Checked;
        foreach (var c in _folderRows) c.Visible = !_modeApi.Checked;
    }

    private void UpdateUserInfo()
    {
        var id = _userId.Text.Trim();
        if (id.Length == 0) { _userIdInfo.Text = "예: SN200662 (쪽지 폴더의 SN200662.db)"; _userIdInfo.ForeColor = Color.DimGray; return; }
        var path = Path.Combine(_dataDir.Text.Trim(), id + ".db");
        if (File.Exists(path))
        {
            var fi = new FileInfo(path);
            _userIdInfo.Text = $"✔ {id}.db 있음 · {FileOps.Human(fi.Length)} · {fi.LastWriteTime:MM/dd HH:mm} 수정";
            _userIdInfo.ForeColor = Color.SeaGreen;
        }
        else
        {
            _userIdInfo.Text = $"✖ 쪽지 폴더에 {id}.db 가 없습니다";
            _userIdInfo.ForeColor = Color.Firebrick;
        }
    }

    private void UpdateLoginInfo()
    {
        if (_s.GoogleLoginUtc is not { } t) { _loginInfo.Text = "아직 로그인하지 않았습니다"; return; }
        var days = (int)(DateTime.UtcNow - t).TotalDays;
        _loginInfo.Text = days >= 7 ? $"{FileOps.When(t)} 로그인 · 만료됨" : $"{FileOps.When(t)} 로그인 · {7 - days}일 남음";
    }

    private void AutoFind()
    {
        var found = DataDirFinder.Find(_userId.Text.Trim());
        if (found != null) _dataDir.Text = found;
        else Dialogs.Info("쪽지 폴더를 찾지 못했습니다", "사용자 폴더(AppData) 아래에서 UlsanEdu Messenger\\history 폴더를 찾지 못했습니다.\n'찾아보기'로 직접 지정해 주세요.");
    }

    private void BrowseFolder(TextBox target)
    {
        using var d = new FolderBrowserDialog { SelectedPath = target.Text, UseDescriptionForTitle = true, Description = "폴더 선택" };
        if (d.ShowDialog(this) == DialogResult.OK) target.Text = d.SelectedPath;
    }

    private void BrowseExe()
    {
        using var d = new OpenFileDialog { Filter = "실행 파일 (*.exe)|*.exe", FileName = _exe.Text };
        if (d.ShowDialog(this) == DialogResult.OK)
        {
            _exe.Text = d.FileName;
            _proc.Text = Path.GetFileNameWithoutExtension(d.FileName);
        }
    }

    private void BrowseJson()
    {
        using var d = new OpenFileDialog { Filter = "client_secret.json|*.json" };
        if (d.ShowDialog(this) == DialogResult.OK) _secret.Text = d.FileName;
    }

    private async Task DoLogin()
    {
        var temp = _s.Clone();
        ReadValues(temp);
        if (string.IsNullOrWhiteSpace(temp.UserId)) { Dialogs.Error("아이디를 먼저 입력해 주세요", "드라이브에 아이디 이름의 폴더를 만듭니다."); return; }
        if (!File.Exists(temp.EffectiveClientSecretPath)) { Dialogs.Error("client_secret.json 이 없습니다", temp.EffectiveClientSecretPath); return; }
        UseWaitCursor = true;
        Enabled = false;
        try
        {
            var msg = await _googleLogin(temp);
            _s.GoogleLoginUtc = temp.GoogleLoginUtc;
            _s.DriveRootFolderId = temp.DriveRootFolderId;
            UpdateLoginInfo();
            Dialogs.Info("구글 로그인 성공", msg);
        }
        catch (Exception ex)
        {
            Log.Error("구글 로그인 실패", ex);
            var hint = ex.Message.Contains("admin_policy_enforced") || ex.Message.Contains("access_denied")
                ? "\n\n학교 관리자가 외부 앱 접근을 막았을 수 있습니다. 이 경우 '폴더 지정' 방식을 써 주세요."
                : "";
            Dialogs.Error("구글 로그인 실패", ex.Message + hint);
        }
        finally
        {
            Enabled = true;
            UseWaitCursor = false;
        }
    }

    private async Task DoCloseTest()
    {
        var temp = _s.Clone();
        ReadValues(temp);
        var r = Dialogs.Ask("메신저 종료 시험",
            "켜져 있는 메신저를 어떤 방법으로 끌 수 있는지 시험합니다.\n" +
            "1단계(종료 신호) → 2단계(창 닫기) → 3단계(강제 종료) 순서로 시도하고 결과를 알려드립니다.\n\n" +
            "메신저가 켜져 있어야 합니다. 시험 중에는 업로드하지 않습니다.",
            TaskDialogIcon.Information, "시험 시작", "취소");
        if (r != 0) return;
        Enabled = false;
        try
        {
            Dialogs.Info("메신저 종료 시험 결과", await _closeTest(temp));
        }
        catch (Exception ex)
        {
            Dialogs.Error("시험 실패", ex.Message);
        }
        finally
        {
            Enabled = true;
        }
    }

    private void Save()
    {
        ReadValues(_s);
        var problems = _s.Validate();
        if (problems.Count > 0)
        {
            var r = Dialogs.Ask("확인이 필요한 항목이 있습니다", string.Join("\n", problems) + "\n\n이대로 저장할까요? (동기화는 이 항목을 고친 뒤에 동작합니다)",
                TaskDialogIcon.Warning, "그래도 저장", "돌아가기");
            if (r != 0) return;
        }
        try
        {
            AutoStart.Set(_autoStart.Checked);
        }
        catch (Exception ex)
        {
            Log.Warn($"자동 실행 설정 실패: {ex.Message}");
        }
        DialogResult = DialogResult.OK;
        Close();
    }

    private static void OpenFolder(string path)
    {
        try { System.Diagnostics.Process.Start("explorer.exe", $"\"{path}\"")?.Dispose(); }
        catch { /* 무시 */ }
    }
}
