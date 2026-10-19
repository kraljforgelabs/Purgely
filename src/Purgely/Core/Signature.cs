using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace Purgely.Core;

/// <summary>Checks Authenticode signatures – a driver or installer is only run when Windows trusts its signature.</summary>
public static class Signature
{
    public sealed record Result(bool Trusted, string Signer, string Problem);

    public static Result Check(string file)
    {
        var trusted = Verify(file, out var problem);
        var signer = "";
        try
        {
#pragma warning disable SYSLIB0057
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(file));
#pragma warning restore SYSLIB0057
            signer = cert.GetNameInfo(X509NameType.SimpleName, false);
        }
        catch (Exception) { /* not signed */ }
        return new Result(trusted, signer, trusted ? "" : problem);
    }

    /// <summary>True when the signer's name belongs to the expected company (e.g. "Intel Corporation" for "Intel").</summary>
    public static bool SignerMatches(string signer, string expectedMaker)
    {
        var s = Keywords.CleanPublisher(signer);
        var m = Keywords.CleanPublisher(expectedMaker);
        if (s.Length < 3 || m.Length < 3) return false;
        return s.Contains(m) || m.Contains(s) ||
               s.Contains("microsoft"); // Microsoft signs drivers from the Update Catalog / WHQL
    }

    // ---------- WinVerifyTrust ----------

    private static bool Verify(string file, out string problem)
    {
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = file,
        };
        var fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = 2,            // WTD_UI_NONE
                fdwRevocationChecks = 0,   // WTD_REVOKE_NONE
                dwUnionChoice = 1,         // WTD_CHOICE_FILE
                pFile = fileInfoPtr,
                dwStateAction = 0,
                dwProvFlags = 0x80,        // WTD_CACHE_ONLY_URL_RETRIEVAL
            };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
            var result = WinVerifyTrust(IntPtr.Zero, action, ref data);
            problem = result switch
            {
                0 => "",
                unchecked((int)0x800B0100) => "The file is not signed.",
                unchecked((int)0x80096010) => "The signature is broken – the file was changed after signing.",
                unchecked((int)0x800B0109) => "Signed with a certificate Windows doesn't trust.",
                unchecked((int)0x800B0101) => "The signing certificate has expired.",
                _ => $"Windows doesn't trust the signature (0x{result:X8}).",
            };
            return result == 0;
        }
        finally
        {
            Marshal.FreeHGlobal(fileInfoPtr);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, ref WINTRUST_DATA data);
}
