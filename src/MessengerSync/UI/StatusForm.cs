using MessengerSync.Core;

namespace MessengerSync.UI;

/// <summary>상태 화면: 클라우드 최신 버전, PC별 기록, 버전 목록과 되돌리기.</summary>
public sealed class StatusForm : Form
{
    private readonly SyncEngine _engine;
    private readonly Label _summary = new() { AutoSize = true, Padding = new Padding(4) };
    private readonly ListView _devices = NewList(("PC", 90), ("마지막 동작", 220), ("시각", 120), ("버전", 60));
    private readonly ListView _versions = NewList(("버전 파일", 300), ("크기", 80), ("올린 시각", 120));
    private readonly Button _restore = new() { Text = "선택한 버전으로 되돌리기", AutoSize = true, Enabled = false };

    public StatusForm(SyncEngine engine)
    {
        _engine = engine;
        Text = "메신저 동기화 상태";
        Font = new Font("Malgun Gothic", 9f);
        AutoScaleMode = AutoScaleMode.Font;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(640, 620);
        MinimumSize = new Size(560, 480);
        Icon = IconFactory.Get(TrayState.Normal);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(10) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 35));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 65));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        layout.Controls.Add(_summary);
        layout.Controls.Add(Title("PC별 마지막 동기화"));
        layout.Controls.Add(_devices);
        layout.Controls.Add(Title("클라우드에 보관된 버전"));
        layout.Controls.Add(_versions);

        var refresh = new Button { Text = "새로 고침", AutoSize = true };
        var close = new Button { Text = "닫기", AutoSize = true };
        refresh.Click += async (_, _) => await LoadAsync();
        close.Click += (_, _) => Close();
        _restore.Click += async (_, _) => await RestoreAsync();
        _versions.SelectedIndexChanged += (_, _) => _restore.Enabled = _versions.SelectedItems.Count == 1;
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.AddRange(new Control[] { close, refresh, _restore });
        layout.Controls.Add(buttons);

        Controls.Add(layout);
        Shown += async (_, _) => await LoadAsync();
    }

    private static Label Title(string t) => new() { Text = t, AutoSize = true, Font = new Font("Malgun Gothic", 9f, FontStyle.Bold), Margin = new Padding(0, 10, 0, 4) };

    private static ListView NewList(params (string, int)[] cols)
    {
        var lv = new ListView { View = View.Details, FullRowSelect = true, MultiSelect = false, Dock = DockStyle.Fill, HideSelection = false };
        foreach (var (name, w) in cols) lv.Columns.Add(name, w);
        return lv;
    }

    private async Task LoadAsync()
    {
        _summary.Text = "불러오는 중...";
        UseWaitCursor = true;
        try
        {
            var o = await _engine.GetOverviewAsync();
            var c = o.Cloud;
            var lines = new List<string>
            {
                c == null ? "클라우드 최신: (아직 올린 적 없음)"
                          : $"클라우드 최신: v{c.Version} · {c.PcName} · {FileOps.When(c.UploadedUtc)} · {FileOps.Human(c.Size)} (압축 {FileOps.Human(c.CompressedSize)})",
                $"이 PC 기준: v{o.State.BaseVersion} · 마지막 동기화 {FileOps.When(o.State.LastSyncUtc)}",
                o.Local.Exists ? $"이 PC 쪽지 파일: {FileOps.Human(o.Local.Size)} · {o.Local.ModifiedLocal:MM/dd HH:mm} 수정" : "이 PC 쪽지 파일: 없음",
                o.Lock == null ? "사용 중인 PC: 없음" : $"사용 중인 PC: {o.Lock.PcName} ({FileOps.When(o.Lock.SinceUtc)}부터)",
            };
            if (o.State.PendingUpload) lines.Add("⚠ 올리지 못한 변경이 있습니다 (자동으로 다시 시도합니다)");
            if (o.State.PendingConflict) lines.Add("⚠ 충돌이 있습니다. 다음에 메신저를 실행할 때 선택합니다.");
            if (!string.IsNullOrEmpty(o.State.LastError)) lines.Add($"마지막 오류: {o.State.LastError}");
            _summary.Text = string.Join("\n", lines);

            _devices.Items.Clear();
            foreach (var d in o.Devices.Values.OrderByDescending(d => d.LastUtc))
                _devices.Items.Add(new ListViewItem(new[] { d.PcName, d.LastAction, FileOps.When(d.LastUtc), $"v{d.Version}" }));

            _versions.Items.Clear();
            foreach (var b in o.Versions)
            {
                var item = new ListViewItem(new[] { b.Name, FileOps.Human(b.Size), FileOps.When(b.ModifiedUtc) }) { Tag = b };
                if (c != null && b.Name == c.BlobName) item.Font = new Font(_versions.Font, FontStyle.Bold);
                _versions.Items.Add(item);
            }
        }
        catch (NeedLoginException)
        {
            _summary.Text = "구글 로그인이 필요합니다. 설정 화면에서 '구글 로그인'을 눌러 주세요.";
        }
        catch (Exception ex)
        {
            _summary.Text = "불러오지 못했습니다: " + ex.Message;
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    private async Task RestoreAsync()
    {
        if (_versions.SelectedItems.Count != 1 || _versions.SelectedItems[0].Tag is not BlobInfo b) return;
        var r = Dialogs.Ask("이 버전으로 되돌릴까요?",
            $"{b.Name}\n\n지금 내용을 먼저 새 버전으로 올린 뒤, 고른 버전으로 되돌리고 그것을 다시 최신 버전으로 올립니다.\n" +
            "메신저가 켜져 있으면 잠시 꺼졌다가 다시 켜집니다. 다른 PC는 다음 동기화 때 이 내용을 받습니다.",
            TaskDialogIcon.Warning, "되돌리기", "취소");
        if (r != 0) return;
        Enabled = false;
        try
        {
            await _engine.RestoreVersionAsync(b);
        }
        finally
        {
            Enabled = true;
        }
        await LoadAsync();
    }
}
