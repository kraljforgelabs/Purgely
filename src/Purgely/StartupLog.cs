using System.IO;

namespace Purgely;

/// <summary>
/// A few lines about each start (%LOCALAPPDATA%\Purgely\startup.log): how far Purgely got, so a report like "nothing
/// happens when I open it" can be traced. Only steps and times – nothing about the PC's programs or files.
/// </summary>
public static class StartupLog
{
    private static readonly string File_ = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Purgely", "startup.log");

    public static void Step(string what)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
            var info = new FileInfo(File_);
            if (info.Exists && info.Length > 32_000) info.Delete(); // keep it small
            File.AppendAllText(File_, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  [{Environment.ProcessId}]  " +
                                      $"Purgely {typeof(StartupLog).Assembly.GetName().Version?.ToString(3)}  {what}\n");
        }
        catch (Exception) { }
    }
}
