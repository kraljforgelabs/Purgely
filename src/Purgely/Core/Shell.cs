using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Purgely.Core;

public static class Shell
{
    /// <summary>Runs a console tool hidden and returns its exit code and output.</summary>
    public static (int Code, string Output) Run(string file, string args, int timeoutMs = 60_000)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(true); } catch (Exception) { }
                return (-1, "timed out");
            }
            return (p.ExitCode, (stdout.Result + stderr.Result).Trim());
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    /// <summary>Like <see cref="Run"/>, but keeps stdout and stderr apart and reads them as UTF-8.</summary>
    public static (int Code, string StdOut, string StdErr) RunSeparate(string file, string args, int timeoutMs = 60_000)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            };
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(true); } catch (Exception) { }
                return (-1, "", "timed out");
            }
            return (p.ExitCode, stdout.Result, stderr.Result);
        }
        catch (Exception ex)
        {
            return (-1, "", ex.Message);
        }
    }

    private const int MOVEFILE_DELAY_UNTIL_REBOOT = 0x4;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileEx(string lpExistingFileName, string? lpNewFileName, int dwFlags);

    /// <summary>Asks Windows to delete a locked file or empty folder at the next restart.</summary>
    public static bool DeleteOnReboot(string path) => MoveFileEx(path, null, MOVEFILE_DELAY_UNTIL_REBOOT);
}
