using System.Diagnostics;
using SecondScreen.Core.Protocol;

namespace SecondScreen.Core.Transport;

public static class AdbHelper
{
    public static string? FindAdbPath()
    {
        // 1. Check PATH
        string pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (string dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(dir, "adb.exe");
            if (File.Exists(candidate))
                return candidate;
        }

        // 2. Check ANDROID_HOME or ANDROID_SDK_ROOT
        string? androidHome = Environment.GetEnvironmentVariable("ANDROID_HOME")
            ?? Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT");
        if (!string.IsNullOrEmpty(androidHome))
        {
            string candidate = Path.Combine(androidHome, "platform-tools", "adb.exe");
            if (File.Exists(candidate))
                return candidate;
        }

        // 3. Check %LOCALAPPDATA%\Android\Sdk\platform-tools\adb.exe
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string defaultSdkAdb = Path.Combine(localAppData, "Android", "Sdk", "platform-tools", "adb.exe");
        if (File.Exists(defaultSdkAdb))
            return defaultSdkAdb;

        return null;
    }

    public static async Task<(bool Success, string Output)> RunAdbCommandAsync(
        string arguments,
        CancellationToken cancellationToken = default)
    {
        string? adbPath = FindAdbPath();
        if (string.IsNullOrEmpty(adbPath))
        {
            return (false, "adb.exe bulunamadı. Lütfen Android Platform-Tools'un yüklü ve PATH'e ekli olduğundan emin olun.");
        }

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = adbPath,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();

            string stdOut = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            string stdErr = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            string combined = (stdOut + Environment.NewLine + stdErr).Trim();
            return (process.ExitCode == 0, combined);
        }
        catch (Exception ex)
        {
            return (false, $"adb çalıştırma hatası: {ex.Message}");
        }
    }

    public static async Task<(bool Success, string Message)> ReversePortAsync(
        int port = ProtocolConstants.DefaultPort,
        CancellationToken cancellationToken = default)
    {
        string arg = $"reverse tcp:{port} tcp:{port}";
        var (success, output) = await RunAdbCommandAsync(arg, cancellationToken).ConfigureAwait(false);
        if (success)
        {
            return (true, $"adb reverse tcp:{port} başarıyla kuruldu.");
        }

        return (false, $"adb reverse başarısız oldu: {output}");
    }

    public static async Task<string> GetConnectedDevicesAsync(CancellationToken cancellationToken = default)
    {
        var (_, output) = await RunAdbCommandAsync("devices", cancellationToken).ConfigureAwait(false);
        return output;
    }
}
