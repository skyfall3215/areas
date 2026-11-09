using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SecondScreen.App;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
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
