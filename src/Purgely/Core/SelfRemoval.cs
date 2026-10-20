using System.Diagnostics;
using System.IO;
using Purgely.Core.Trial;

namespace Purgely.Core;

/// <summary>What removing Purgely would delete – shown to the user before anything happens.</summary>
public sealed record SelfRemovalPlan(
    List<string> Folders, long Bytes, int UndoPackages, int OpenTrials, int DelayedStartupItems, string? ExePath);

/// <summary>
/// "Remove Purgely and its data": a good uninstaller leaves nothing behind itself. Deletes only Purgely's own
/// folders (C:\ProgramData\Purgely, %LOCALAPPDATA%\Purgely, X:\Purgely Undo) and, if asked, Purgely.exe.
/// Changes Purgely made to Windows (turned-off ads, startup items) stay as they are – they are the user's choices.
/// </summary>
public static class SelfRemoval
{
    public static SelfRemovalPlan Plan()
    {
        var folders = new[] { DataFolders.Shared, DataFolders.PerUser }
            .Concat(DataFolders.DriveUndoFolders())
            .Where(Directory.Exists)
            .Where(IsPurgelyFolder)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var bytes = folders.Sum(f => FileMover.SizeOf(f));
        var undo = UndoStore.List().Count(e => !e.Manifest.Restored);
        var trials = TrialEngine.List().Count(r => r.State is TrialState.Installing or TrialState.Analyzed);
        var delayed = Startup.StartupManager.LoadState().Managed.Values.Count(m => m.DelayedTask is { Length: > 0 });
        var exe = Environment.ProcessPath;
        if (exe is null || !Path.GetFileName(exe).Equals("Purgely.exe", StringComparison.OrdinalIgnoreCase) ||
            PathUtil.IsUnder(exe, PathUtil.WindowsDir) || !File.Exists(exe))
            exe = null;
        return new SelfRemovalPlan(folders, bytes, undo, trials, delayed, exe);
    }

    /// <summary>Only folders with exactly Purgely's names are ever deleted here.</summary>
    private static bool IsPurgelyFolder(string folder)
    {
        var name = Path.GetFileName(folder.TrimEnd('\\'));
        return (name.Equals("Purgely", StringComparison.OrdinalIgnoreCase) ||
                name.Equals(Cleaner.DriveUndoFolderName, StringComparison.OrdinalIgnoreCase)) &&
               !FileMover.IsReparsePoint(folder);
    }

    /// <summary>
    /// Deletes the planned folders. Returns how many files Windows will remove at the next restart (locked ones).
    /// The caller exits Purgely right after.
    /// </summary>
    public static int RemoveData(SelfRemovalPlan plan)
    {
        var scheduled = 0;
        ExplorerMenu.TurnOff(); // the optional right-click entry – only Purgely's own keys
        foreach (var folder in plan.Folders.Where(IsPurgelyFolder))
        {
            try { scheduled += FileMover.DeleteBestEffort(folder, true); }
            catch (Exception) { }
        }
        return scheduled;
    }

    /// <summary>
    /// Deletes Purgely.exe a few seconds after Purgely has exited (a running program can't delete itself).
    /// Only the .exe file itself – never its folder.
    /// </summary>
    public static bool ScheduleExeDelete(string exe)
    {
        if (!Path.GetFileName(exe).Equals("Purgely.exe", StringComparison.OrdinalIgnoreCase) || exe.Contains('"') || exe.Contains('%')) return false;
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                // Waits for Purgely to close, then tries for about 30 seconds (Windows may hold the file a moment longer).
                $"/d /c ping 127.0.0.1 -n 4 >nul & for /l %i in (1,1,15) do @if exist \"{exe}\" (del /f /q \"{exe}\" 2>nul & ping 127.0.0.1 -n 3 >nul)")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetTempPath(),
            };
            Process.Start(psi)?.Dispose();
            return true;
        }
        catch (Exception) { return false; }
    }
}
