using System.Runtime.InteropServices;

namespace MessengerSync.UI;

/// <summary>자리 비움 감지, 화면 보호기·전체 화면 확인, 화면 잠금에 쓰는 윈도우 기능.</summary>
public static class Win32
{
    /// <summary>마지막 키보드·마우스 입력 이후 지난 시간.</summary>
    public static TimeSpan IdleTime()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
        var ms = unchecked((uint)Environment.TickCount - info.dwTime);
        return TimeSpan.FromMilliseconds(ms);
    }

    public static bool ScreenSaverRunning()
    {
        var running = false;
        return SystemParametersInfo(0x0072 /* SPI_GETSCREENSAVERRUNNING */, 0, ref running, 0) && running;
    }

    /// <summary>발표 모드, 전체 화면 프로그램(영상·게임), "방해 금지" 상태인지.</summary>
    public static bool BusyFullscreen()
    {
        try
        {
            if (SHQueryUserNotificationState(out var state) != 0) return false;
            return state is 2 /* QUNS_BUSY */ or 3 /* QUNS_RUNNING_D3D_FULL_SCREEN */ or 4 /* QUNS_PRESENTATION_MODE */;
        }
        catch
        {
            return false;
        }
    }

    public static bool LockScreen() => LockWorkStation();

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, ref bool vparam, uint init);

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    [DllImport("user32.dll")]
    private static extern bool LockWorkStation();
}

/// <summary>전역 단축키(Ctrl+Alt+L)를 받는 보이지 않는 창.</summary>
public sealed class HotkeyWindow : NativeWindow, IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;
    private const int Id = 0x4D53; // "MS"
    private readonly Action _onPressed;
    private bool _registered;

    public HotkeyWindow(Action onPressed)
    {
        _onPressed = onPressed;
        CreateHandle(new CreateParams());
    }

    /// <summary>Ctrl+Alt+L 등록. 다른 프로그램이 이미 쓰고 있으면 false.</summary>
    public bool Register()
    {
        if (_registered) return true;
        _registered = RegisterHotKey(Handle, Id, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, (uint)Keys.L);
        return _registered;
    }

    public void Unregister()
    {
        if (!_registered) return;
        UnregisterHotKey(Handle, Id);
        _registered = false;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && (int)m.WParam == Id) _onPressed();
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        Unregister();
        DestroyHandle();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}

/// <summary>자리 비움 30초 전 카운트다운 창. 마우스를 움직이면 취소된다.</summary>
public sealed class CountdownForm : Form
{
    private readonly Label _text = new() { Dock = DockStyle.Top, Height = 46, Padding = new Padding(0, 4, 0, 0) };
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 500 };
    private readonly DateTime _deadline;

    public event Action? UploadNow;
    public event Action? KeepUsing;

    public CountdownForm(DateTime deadlineLocal, int idleMinutes)
    {
        _deadline = deadlineLocal;
        Text = "메신저 동기화";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        ControlBox = false;
        AutoScaleMode = AutoScaleMode.Font;
        Font = new Font("Malgun Gothic", 9f);
        Padding = new Padding(12, 8, 12, 10);
        ClientSize = new Size(360, 120);

        var title = new Label
        {
            Text = $"{idleMinutes}분 동안 입력이 없었습니다",
            Dock = DockStyle.Top,
            Height = 24,
            Font = new Font("Malgun Gothic", 10f, FontStyle.Bold),
        };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 34, FlowDirection = FlowDirection.RightToLeft };
        var keep = new Button { Text = "계속 사용", AutoSize = true };
        var now = new Button { Text = "지금 업로드", AutoSize = true };
        keep.Click += (_, _) => KeepUsing?.Invoke();
        now.Click += (_, _) => UploadNow?.Invoke();
        buttons.Controls.Add(keep);
        buttons.Controls.Add(now);

        Controls.Add(_text);
        Controls.Add(title);
        Controls.Add(buttons);

        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
        Location = new Point(wa.Right - Width - 16, wa.Bottom - Height - 16);
        _tick.Tick += (_, _) => Refresh_();
        _tick.Start();
        Refresh_();
    }

    private void Refresh_()
    {
        var left = Math.Max(0, (int)Math.Ceiling((_deadline - DateTime.Now).TotalSeconds));
        _text.Text = $"{left}초 뒤 메신저를 종료하고 업로드합니다.\n마우스를 움직이거나 키를 누르면 취소됩니다.";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tick.Dispose();
        base.Dispose(disposing);
    }
}
