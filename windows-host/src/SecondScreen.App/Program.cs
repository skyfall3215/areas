using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SecondScreen.App;

public static class Program
{
    private const string AppMutexName = "Global\\AreasSecondScreenServerMutex";

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [STAThread]
    public static void Main(string[] args)
    {
        try { SecondScreen.Media.Capture.GdiScreenCapture.EnsureThreadDesktopAttached(); } catch { }
        try { SetProcessDpiAwarenessContext((IntPtr)(-4)); } catch { } // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2

        using var mutex = new Mutex(true, AppMutexName, out bool createdNew);
        if (!createdNew)
        {
            // Another instance is already running! Bring it to front and exit cleanly
            var current = Process.GetCurrentProcess();
            var existing = Process.GetProcessesByName(current.ProcessName)
                                  .FirstOrDefault(p => p.Id != current.Id);
            if (existing != null && existing.MainWindowHandle != IntPtr.Zero)
            {
                ShowWindowAsync(existing.MainWindowHandle, 9); // SW_RESTORE
                SetForegroundWindow(existing.MainWindowHandle);
            }
            return;
        }

        try
        {
            bool isConsole = args.Any(a => a.Equals("--console", StringComparison.OrdinalIgnoreCase) ||
                                           a.Equals("-c", StringComparison.OrdinalIgnoreCase) ||
                                           a.Equals("--headless", StringComparison.OrdinalIgnoreCase));

            if (isConsole)
            {
                RunConsoleAsync(args).GetAwaiter().GetResult();
            }
            else
            {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                ApplicationConfiguration.Initialize();
                Application.Run(new ServerDashboardForm());
            }
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"), ex.ToString());
            MessageBox.Show(ex.ToString(), "Areas Startup Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static async Task RunConsoleAsync(string[] args)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        using var server = new SecondScreenServer();
        server.OnLog += msg => Console.WriteLine(msg);

        await server.StartAsync();
        Console.WriteLine("[*] Sunucu konsol modunda çalışıyor. Çıkmak için Ctrl+C'ye basın.");

        try
        {
            await Task.Delay(-1, cts.Token);
        }
        catch (OperationCanceledException) { }

        server.Stop();
    }
}
