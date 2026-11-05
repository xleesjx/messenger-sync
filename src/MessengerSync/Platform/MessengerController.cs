using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using MessengerSync.Core;

namespace MessengerSync.Platform;

/// <summary>
/// 메신저(TUC.exe) 실행과 종료. 종료는 강제 종료 후 정밀 검사 방식이다.
/// 화면이 멈추지 않도록 백그라운드에서 처리한다.
/// </summary>
public sealed class MessengerController : IMessenger
{
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_QUERYENDSESSION = 0x0011;
    private const uint WM_ENDSESSION = 0x0016;
    private const uint WM_COMMAND = 0x0111;
    private const uint ENDSESSION_CLOSEAPP = 0x1;
    private const uint SMTO_ABORTIFHUNG = 0x2;
    private const uint MF_BYPOSITION = 0x400;

    private static readonly string[] ExitWords = { "종료", "끝내기", "프로그램 종료", "exit", "quit" };

    private readonly AppSettings _s;

    public MessengerController(AppSettings settings) => _s = settings;

    private Process[] Find()
    {
        var name = Path.GetFileNameWithoutExtension(_s.ProcessName.Trim());
        return string.IsNullOrEmpty(name) ? Array.Empty<Process>() : Process.GetProcessesByName(name);
    }

    public bool IsRunning()
    {
        var ps = Find();
        foreach (var p in ps) p.Dispose();
        return ps.Length > 0;
    }

    public void Launch()
    {
        var exe = _s.MessengerExe;
        Process.Start(new ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
        })?.Dispose();
    }

    public Task<CloseReport> CloseAsync(bool allowKill, CancellationToken ct) => Task.Run(() => CloseCoreAsync(allowKill, ct), ct);

    private async Task<CloseReport> CloseCoreAsync(bool allowKill, CancellationToken ct)
    {
        // 울산교육메신저는 종료 신호·창 닫기·트레이 메뉴 방식으로 꺼지지 않는 것을 확인했다 (v0.1.0~0.1.2 시험).
        // 그래서 바로 강제 종료하고, 업로드 전에 정밀 검사로 파일 상태를 확인한다.
        var sw = Stopwatch.StartNew();
        var procs = Find();
        try
        {
            if (procs.Length == 0) return new CloseReport(CloseResult.NotRunning, 0);
            if (!allowKill) return new CloseReport(CloseResult.Failed, 0, "강제 종료 허용 안 됨");

            foreach (var p in procs.Where(p => !HasExited(p)))
            {
                try { p.Kill(); }
                catch (Exception ex) { Log.Warn($"강제 종료 실패: {ex.Message}"); }
            }
            if (await WaitExitAsync(procs, TimeSpan.FromSeconds(10), ct))
            {
                await Task.Delay(1000, ct); // 파일 잠금이 풀릴 시간
                Log.Info($"메신저 종료 (강제 종료, {sw.Elapsed.TotalSeconds:0.0}초)");
                return new CloseReport(CloseResult.Killed, sw.Elapsed.TotalSeconds, "강제 종료");
            }
            return new CloseReport(CloseResult.Failed, sw.Elapsed.TotalSeconds, "강제 종료 실패");
        }
        finally
        {
            foreach (var p in procs) p.Dispose();
        }
    }

    /// <summary>종료 시험용: 메신저 창 구조(창 종류, 제목, 메뉴)를 글로 정리한다.</summary>
    public string Describe()
    {
        var sb = new StringBuilder();
        var procs = Find();
        try
        {
            foreach (var p in procs)
            {
                sb.AppendLine($"[프로세스 {p.Id}]");
                var n = 0;
                foreach (var h in WindowsOf(p.Id))
                {
                    if (ClassOf(h) == "tooltips_class32") continue;
                    if (++n > 60) { sb.AppendLine("  ... (이하 생략)"); break; }
                    var cls = new StringBuilder(256);
                    var title = new StringBuilder(256);
                    GetClassName(h, cls, cls.Capacity);
                    GetWindowText(h, title, title.Capacity);
                    var menu = GetMenu(h);
                    var items = menu != IntPtr.Zero ? MenuTexts(menu, 0) : new List<string>();
                    sb.AppendLine($"  창: {cls} \"{title}\" {(IsWindowVisible(h) ? "보임" : "숨김")}" +
                                  (items.Count > 0 ? $" 메뉴: {string.Join(", ", items.Take(30))}" : ""));
                }
            }
        }
        finally
        {
            foreach (var p in procs) p.Dispose();
        }
        return sb.Length == 0 ? "(메신저 창 없음)" : sb.ToString().TrimEnd();
    }

    private static List<string> MenuTexts(IntPtr menu, int depth)
    {
        var list = new List<string>();
        if (depth > 2) return list;
        var count = GetMenuItemCount(menu);
        for (var i = 0; i < count && i < 40; i++)
        {
            var text = MenuText(menu, i);
            var sub = GetSubMenu(menu, i);
            if (sub != IntPtr.Zero)
                list.Add($"{text}[{string.Join("/", MenuTexts(sub, depth + 1))}]");
            else if (text.Length > 0)
                list.Add(text);
        }
        return list;
    }

    private static string MenuText(IntPtr menu, int pos)
    {
        var sb = new StringBuilder(256);
        GetMenuString(menu, (uint)pos, sb, sb.Capacity, MF_BYPOSITION);
        var t = sb.ToString().Replace("&", "");
        var tab = t.IndexOf('\t');
        return (tab >= 0 ? t[..tab] : t).Trim();
    }

    /// <summary>창의 메뉴(하위 메뉴 포함)에서 '종료' 같은 항목을 찾는다. 로그아웃은 제외.</summary>
    private static IEnumerable<(IntPtr Hwnd, uint Id, string Text)> FindExitCommands(IntPtr hwnd)
    {
        var found = new List<(IntPtr, uint, string)>();
        var menu = GetMenu(hwnd);
        if (menu == IntPtr.Zero) return found;
        void Walk(IntPtr m, int depth)
        {
            if (depth > 2) return;
            var count = GetMenuItemCount(m);
            for (var i = 0; i < count && i < 60; i++)
            {
                var sub = GetSubMenu(m, i);
                if (sub != IntPtr.Zero) { Walk(sub, depth + 1); continue; }
                var text = MenuText(m, i);
                var id = GetMenuItemID(m, i);
                if (id == 0 || id == uint.MaxValue || text.Contains("로그아웃")) continue;
                if (ExitWords.Any(w => text.Equals(w, StringComparison.OrdinalIgnoreCase)
                                       || text.StartsWith(w, StringComparison.OrdinalIgnoreCase)))
                    found.Add((hwnd, id, text));
            }
        }
        Walk(menu, 0);
        return found;
    }

    private static bool HasExited(Process p)
    {
        try { return p.HasExited; }
        catch { return true; }
    }

    private static async Task<bool> WaitExitAsync(Process[] procs, TimeSpan timeout, CancellationToken ct)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (procs.All(HasExited)) return true;
            await Task.Delay(200, ct);
        }
        return procs.All(HasExited);
    }

    private static string ClassOf(IntPtr h)
    {
        var sb = new StringBuilder(256);
        GetClassName(h, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string TextOf(IntPtr h)
    {
        var sb = new StringBuilder(256);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public int cbSize;
        public uint flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public RECT rcCaret;
    }

    [DllImport("user32.dll")]
    private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO info);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

    /// <summary>프로세스의 최상위 창(숨겨진 트레이 창 포함) 목록.</summary>
    private static List<IntPtr> WindowsOf(int pid)
    {
        var list = new List<IntPtr>();
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var owner);
            if (owner == pid) list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetMenu(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetMenuItemCount(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern IntPtr GetSubMenu(IntPtr hMenu, int nPos);

    [DllImport("user32.dll")]
    private static extern uint GetMenuItemID(IntPtr hMenu, int nPos);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMenuString(IntPtr hMenu, uint uIDItem, StringBuilder lpString, int nMaxCount, uint uFlag);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);
}
