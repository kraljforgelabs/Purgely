using System.IO;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Purgely.Core;

/// <summary>Parses and runs a program's official uninstall command.</summary>
public static partial class UninstallCommand
{
    /// <summary>Returns the executable path from an uninstall command line, or null.</summary>
    public static string? ExtractExecutable(string command)
    {
        var (file, _) = Split(command);
        return file.Length > 0 ? file : null;
    }

    /// <summary>Starts the official uninstaller and returns the started process (may exit early).</summary>
    public static Process? Run(InstalledProgram p)
    {
        if (p.IsMsi && p.MsiProductCode is not null)
            return Process.Start(new ProcessStartInfo("msiexec.exe", $"/x {p.MsiProductCode}") { UseShellExecute = true });

        var command = p.UninstallString;
        if (command.Length == 0)
            throw new InvalidOperationException("This program has no official uninstaller.");

        var (file, args) = Split(command);
        // "msiexec /I{GUID}" opens the repair/modify dialog – /X uninstalls.
        if (Path.GetFileName(file).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase) ||
            file.Equals("msiexec", StringComparison.OrdinalIgnoreCase))
            args = MsiInstallSwitch().Replace(args, "/X{");

        return Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true });
    }

    /// <summary>
    /// Starts the uninstaller without questions when the program supports it: Windows Installer (MSI), the program's own
    /// QuietUninstallString, Inno Setup (/VERYSILENT) or NSIS (/S, only when the file really is an NSIS uninstaller).
    /// Returns null when there is no known quiet way – the normal uninstaller must be used then.
    /// </summary>
    public static (Process? Process, string Method)? RunQuiet(InstalledProgram p)
    {
        if (p.IsMsi && p.MsiProductCode is not null && MsiCode().IsMatch(p.MsiProductCode))
            return (Process.Start(new ProcessStartInfo("msiexec.exe", $"/x {p.MsiProductCode} /qn /norestart")
                { UseShellExecute = false, CreateNoWindow = true }), "Windows Installer, no questions");

        if (p.QuietUninstallString.Trim().Length > 0)
        {
            var (qf, qa) = Split(p.QuietUninstallString);
            if (qf.Length > 0 && (File.Exists(qf) || qf.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase) || qf.Equals("msiexec", StringComparison.OrdinalIgnoreCase)))
            {
                if (Path.GetFileName(qf).StartsWith("msiexec", StringComparison.OrdinalIgnoreCase)) qa = MsiInstallSwitch().Replace(qa, "/X{");
                return (Process.Start(new ProcessStartInfo(qf, qa) { UseShellExecute = true }), "the program's own quiet uninstall");
            }
        }

        var (file, args) = Split(p.UninstallString);
        if (file.Length == 0 || !File.Exists(file)) return null;
        var name = Path.GetFileName(file);
        if (InnoUninstaller().IsMatch(name))
            return (Process.Start(new ProcessStartInfo(file, $"{args} /VERYSILENT /SUPPRESSMSGBOXES /NORESTART".Trim()) { UseShellExecute = true }),
                    "Inno Setup, no questions");
        if (IsNsis(file))
            return (Process.Start(new ProcessStartInfo(file, $"{args} /S".Trim()) { UseShellExecute = true }), "NSIS, no questions");
        return null;
    }

    /// <summary>NSIS uninstallers carry "Nullsoft" in their first megabyte.</summary>
    private static bool IsNsis(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            var buffer = new byte[Math.Min(1024 * 1024, stream.Length)];
            var read = stream.Read(buffer, 0, buffer.Length);
            var text = System.Text.Encoding.ASCII.GetString(buffer, 0, read);
            return text.Contains("Nullsoft", StringComparison.Ordinal) || text.Contains("NSIS", StringComparison.Ordinal);
        }
        catch (Exception) { return false; }
    }

    [GeneratedRegex(@"^\{[0-9A-Fa-f\-]{36}\}$")]
    private static partial Regex MsiCode();

    [GeneratedRegex(@"^unins\d{3}\.exe$", RegexOptions.IgnoreCase)]
    private static partial Regex InnoUninstaller();

    /// <summary>
    /// Runs the uninstaller and waits for it. MSI packages run with a progress bar only (no questions),
    /// so a whole suite can be removed one after another without "another installation is in progress" errors.
    /// Returns false if the uninstaller exited so quickly that it probably handed over to a second process.
    /// </summary>
    public static async Task<bool> RunAndWaitAsync(InstalledProgram p)
    {
        var started = DateTime.Now;
        Process? process;
        if (p.IsMsi && p.MsiProductCode is not null)
            process = Process.Start(new ProcessStartInfo("msiexec.exe", $"/x {p.MsiProductCode} /passive /norestart") { UseShellExecute = true });
        else
            process = Run(p);
        if (process is null) return false;
        await process.WaitForExitAsync();
        return p.IsMsi || DateTime.Now - started > TimeSpan.FromSeconds(8);
    }

    private static (string File, string Args) Split(string command)
    {
        command = Environment.ExpandEnvironmentVariables(command.Trim());
        if (command.Length == 0) return ("", "");

        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            return end > 0 ? (command[1..end], command[(end + 1)..].Trim()) : (command.Trim('"'), "");
        }

        var m = ExeRegex().Match(command);
        if (m.Success) return (m.Groups[1].Value, m.Groups[2].Value.Trim());

        var space = command.IndexOf(' ');
        return space > 0 ? (command[..space], command[(space + 1)..]) : (command, "");
    }

    [GeneratedRegex(@"^(.+?\.exe)(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex ExeRegex();

    [GeneratedRegex(@"/I\s*\{", RegexOptions.IgnoreCase)]
    private static partial Regex MsiInstallSwitch();
}
