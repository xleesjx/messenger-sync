using MessengerSync.Core;

namespace MessengerSync.UI;

/// <summary>
/// 화면 오른쪽 아래에 뜨는 진행 창. 전체 단계 목록과 현재 단계의 진행률을 보여준다.
///   ✔ 클라우드 확인
///   ✔ 쪽지 파일 비교
///   ▶ 받기                 45%
///     [██████░░░░░░░]
///   ○ 메신저 실행
/// </summary>
public sealed class ProgressForm : Form
{
    private readonly Label _title = new() { AutoSize = false, Height = 26, Dock = DockStyle.Top };
    private readonly TableLayoutPanel _steps = new() { ColumnCount = 3, AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(0, 4, 0, 4) };
    private readonly ProgressBar _bar = new() { Dock = DockStyle.Top, Height = 14, Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30 };
    private readonly Label _detail = new() { AutoSize = false, Height = 38, Dock = DockStyle.Top, ForeColor = Color.DimGray, Padding = new Padding(0, 6, 0, 0) };
    private readonly List<(Label Icon, Label Name, Label Pct)> _rows = new();
    private readonly Font _bold;

    public ProgressForm()
    {
        Text = "메신저 동기화";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        ControlBox = false;
        AutoScaleMode = AutoScaleMode.Font;
        Font = new Font("Malgun Gothic", 9f);
        _bold = new Font(Font, FontStyle.Bold);
        Padding = new Padding(12, 8, 12, 10);
        Width = 380;

        _title.Font = new Font("Malgun Gothic", 10f, FontStyle.Bold);
        _title.Text = "동기화 준비 중...";
        _steps.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 24));
        _steps.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _steps.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 48));

        // Dock=Top 은 나중에 추가한 것이 위에 오므로 역순으로 넣는다
        Controls.Add(_detail);
        Controls.Add(_bar);
        Controls.Add(_steps);
        Controls.Add(_title);
        PlaceBottomRight();
    }

    private void PlaceBottomRight()
    {
        var content = _title.Height + _steps.PreferredSize.Height + _bar.Height + _detail.Height + Padding.Vertical;
        ClientSize = new Size(ClientSize.Width, content);
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
        Location = new Point(wa.Right - Width - 16, wa.Bottom - Height - 16);
    }

    /// <summary>예전 방식(글자만)으로 상태를 바꿀 때.</summary>
    public void SetText(string text)
    {
        if (IsDisposed) return;
        _detail.Text = text;
    }

    public void Apply(ProgressInfo p)
    {
        if (IsDisposed) return;
        SuspendLayout();
        _title.Text = p.Finished
            ? (p.Success ? "✔ " : "⚠ ") + p.Title
            : $"{p.Title}  ({Math.Max(1, p.Current + 1)}/{p.Steps.Count}단계)";
        _title.ForeColor = p.Finished && !p.Success ? Color.Firebrick : SystemColors.ControlText;

        if (_rows.Count != p.Steps.Count)
        {
            _steps.Controls.Clear();
            _steps.RowStyles.Clear();
            _rows.Clear();
            for (var i = 0; i < p.Steps.Count; i++)
            {
                var icon = new Label { AutoSize = true, Margin = new Padding(0, 2, 0, 2) };
                var name = new Label { AutoSize = true, Margin = new Padding(0, 2, 0, 2) };
                var pct = new Label { AutoSize = true, Margin = new Padding(0, 2, 0, 2), Anchor = AnchorStyles.Right };
                _steps.Controls.Add(icon, 0, i);
                _steps.Controls.Add(name, 1, i);
                _steps.Controls.Add(pct, 2, i);
                _rows.Add((icon, name, pct));
            }
        }

        for (var i = 0; i < p.Steps.Count; i++)
        {
            var (icon, name, pct) = _rows[i];
            var state = p.States[i];
            name.Text = p.Steps[i];
            (icon.Text, icon.ForeColor) = state switch
            {
                StepState.Done => ("✔", Color.SeaGreen),
                StepState.Running => ("▶", Color.RoyalBlue),
                StepState.Skipped => ("–", Color.Gray),
                StepState.Failed => ("✖", Color.Firebrick),
                _ => ("○", Color.Silver),
            };
            name.Font = state == StepState.Running ? _bold : Font;
            name.ForeColor = state is StepState.Pending or StepState.Skipped ? Color.Gray : SystemColors.ControlText;
            pct.Text = state == StepState.Running && p.Percent is { } v ? $"{v * 100:0}%" : "";
        }

        if (p.Finished)
        {
            _bar.Style = ProgressBarStyle.Continuous;
            _bar.Value = p.Success ? 100 : 0;
        }
        else if (p.Percent is { } v)
        {
            if (_bar.Style != ProgressBarStyle.Continuous) _bar.Style = ProgressBarStyle.Continuous;
            _bar.Value = (int)Math.Clamp(v * 100, 0, 100);
        }
        else if (_bar.Style != ProgressBarStyle.Marquee)
        {
            _bar.Style = ProgressBarStyle.Marquee;
        }

        _detail.Text = p.Detail;
        ResumeLayout(true);
        PlaceBottomRight();
    }
}
