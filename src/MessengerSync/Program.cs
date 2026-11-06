using MessengerSync.Core;
using MessengerSync.UI;

namespace MessengerSync;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // 업데이트 후 다시 시작한 경우: 이전 프로그램이 완전히 끝날 때까지 기다린다
        var wait = Array.IndexOf(args, "--wait-pid");
        if (wait >= 0 && wait + 1 < args.Length && int.TryParse(args[wait + 1], out var oldPid))
        {
            try { System.Diagnostics.Process.GetProcessById(oldPid).WaitForExit(30000); }
            catch { /* 이미 끝남 */ }
        }

        var user = Environment.UserName;
        using var mutex = new Mutex(true, $"Local\\MessengerSync_{user}", out var first);
        using var launchSignal = new EventWaitHandle(false, EventResetMode.AutoReset, $"Local\\MessengerSync_Launch_{user}");
        using var syncLockSignal = new EventWaitHandle(false, EventResetMode.AutoReset, $"Local\\MessengerSync_SyncLock_{user}");
        if (!first)
        {
            // 이미 트레이에 떠 있으면, 그쪽에 요청하고 끝낸다.
            if (args.Contains("--sync-lock")) syncLockSignal.Set();      // 바탕화면 "동기화 후 잠금" 아이콘
            else if (!args.Contains("--startup")) launchSignal.Set();    // "메신저 실행"
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            Log.Error("처리되지 않은 오류", e.Exception);
            Dialogs.Error("오류가 발생했습니다", e.Exception.Message + "\n\n자세한 내용은 기록 폴더의 로그를 확인해 주세요.");
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("치명적 오류", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Error("백그라운드 오류", e.Exception); e.SetObserved(); };

        Application.Run(new TrayApp(args, launchSignal, syncLockSignal));
    }
}
