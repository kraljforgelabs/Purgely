using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace Purgely.Core;

/// <summary>Reads where a shortcut (.lnk) or internet shortcut (.url) points. Read-only.</summary>
public static class ShellLink
{
    /// <summary>The target file and its arguments; empty target for shortcuts Windows resolves itself (MSI, Store apps).</summary>
    public static (string Target, string Arguments) ReadLnk(string path)
    {
        object? link = null;
        try
        {
            link = new CShellLink();
            ((IPersistFile)link).Load(path, 0); // STGM_READ
            var shell = (IShellLinkW)link;
            var target = new StringBuilder(1024);
            shell.GetPath(target, target.Capacity, IntPtr.Zero, 4); // SLGP_RAWPATH – environment variables expanded below
            var args = new StringBuilder(4096);
            shell.GetArguments(args, args.Capacity);
            return (Environment.ExpandEnvironmentVariables(target.ToString()).Trim().Trim('"'), args.ToString().Trim());
        }
        catch (Exception) { return ("", ""); }
        finally
        {
            if (link is not null && Marshal.IsComObject(link)) Marshal.ReleaseComObject(link);
        }
    }

    /// <summary>The URL= line of a .url file (e.g. steam://rungameid/1234). Empty when there is none.</summary>
    public static string ReadUrl(string path)
    {
        try
        {
            if (new FileInfo(path).Length > 64_000) return "";
            foreach (var line in File.ReadLines(path))
                if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase)) return line[4..].Trim();
        }
        catch (Exception) { }
        return "";
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class CShellLink { }

    // Only the first methods of IShellLinkW are declared – the order must match the real interface.
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
    }
}
