using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Purgely.Core;

/// <summary>
/// "Check before you install": who signed the installer, where it was downloaded from, what kind of installer it is,
/// and whether it carries a known advertising/bundling SDK. Read-only – nothing is run.
/// </summary>
public static class InstallerCheck
{
    public sealed record Finding(bool Warning, string Text);

    public sealed record Report(string File, string Sha256, string Kind, List<Finding> Findings)
    {
        public bool HasWarnings => Findings.Any(f => f.Warning);
        public string VirusTotalUrl => $"https://www.virustotal.com/gui/file/{Sha256}";
    }

    /// <summary>Text found inside installers that ship advertising "offers" (bundleware SDKs).</summary>
    private static readonly (string Marker, string Name)[] Bundlers =
    {
        ("OpenCandy", "OpenCandy"), ("InstallCore", "InstallCore"), ("installcore", "InstallCore"),
        ("Amonetize", "Amonetize"), ("Somoto", "Somoto"), ("DownloadAdmin", "DownloadAdmin"),
        ("SweetLabs", "SweetLabs"), ("OutBrowse", "OutBrowse"), ("Solimba", "Solimba"), ("Firseria", "Firseria"),
        ("RelevantKnowledge", "RelevantKnowledge"), ("ironSource", "ironSource installer"), ("InstallMonetizer", "InstallMonetizer"),
        ("OfferScreen", "an offer screen"), ("Opera_GX_offer", "an Opera offer"), ("AvastOffer", "an Avast offer"),
        ("McAfee WebAdvisor", "a McAfee offer"), ("Bing Bar", "a Bing toolbar offer"), ("Ask Toolbar", "an Ask toolbar offer"),
    };

    public static Report Check(string file)
    {
        var findings = new List<Finding>();
        var info = new FileInfo(file);

        // 1. Signature
        var sig = Signature.Check(file);
        findings.Add(sig.Trusted
            ? new Finding(false, $"Signed by: {sig.Signer} ✓")
            : new Finding(true, sig.Signer.Length > 0
                ? $"Signature problem ({sig.Problem}) – signer: {sig.Signer}"
                : "Not signed – nobody vouches for this file. Only install it if you trust where it came from."));

        // 2. Where it came from (the "Mark of the Web" Windows adds to downloads)
        var zone = ReadZone(file);
        if (zone.Host.Length > 0) findings.Add(new Finding(false, $"Downloaded from: {zone.Host}"));
        else if (zone.Zone >= 3) findings.Add(new Finding(false, "Downloaded from the internet (the site isn't recorded)"));

        // 3. Kind of installer and bundled offers – read the first 16 MB as text
        var kind = "Program";
        var text = ReadText(file, 16 * 1024 * 1024);
        if (file.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)) kind = "Windows Installer (MSI)";
        else if (text.Contains("Inno Setup", StringComparison.Ordinal)) kind = "Inno Setup installer";
        else if (text.Contains("Nullsoft", StringComparison.Ordinal)) kind = "NSIS installer";
        else if (text.Contains("InstallShield", StringComparison.Ordinal)) kind = "InstallShield installer";
        else if (text.Contains("WiX Toolset", StringComparison.Ordinal) || text.Contains("WixBundle", StringComparison.Ordinal)) kind = "WiX bundle";
        else if (text.Contains("Squirrel", StringComparison.Ordinal)) kind = "Squirrel installer (installs into your user folder)";

        var bundlers = Bundlers.Where(b => text.Contains(b.Marker, StringComparison.Ordinal)).Select(b => b.Name).Distinct().ToList();
        if (bundlers.Count > 0)
            findings.Add(new Finding(true, $"Contains {string.Join(", ", bundlers)} – this installer may offer extra software. " +
                                            "Read every screen and untick offers (Trial Mode also catches them afterwards)."));
        if (info.Length < 3 * 1024 * 1024 && (text.Contains("download", StringComparison.OrdinalIgnoreCase) || text.Contains("http", StringComparison.Ordinal)))
            findings.Add(new Finding(false, "Small web installer – it downloads the real program while installing."));

        return new Report(file, Sha256(file), kind, findings);
    }

    private static (int Zone, string Host) ReadZone(string file)
    {
        try
        {
            var ads = file + ":Zone.Identifier";
            if (!File.Exists(ads)) return (0, "");
            var lines = File.ReadAllLines(ads);
            int.TryParse(lines.FirstOrDefault(l => l.StartsWith("ZoneId=", StringComparison.OrdinalIgnoreCase))?.Substring(7), out var zoneId);
            var url = lines.FirstOrDefault(l => l.StartsWith("HostUrl=", StringComparison.OrdinalIgnoreCase))?.Substring(8) ??
                      lines.FirstOrDefault(l => l.StartsWith("ReferrerUrl=", StringComparison.OrdinalIgnoreCase))?.Substring(12) ?? "";
            var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "";
            return (zoneId, host);
        }
        catch (Exception) { return (0, ""); }
    }

    private static string ReadText(string file, int max)
    {
        try
        {
            using var s = File.OpenRead(file);
            var buffer = new byte[Math.Min(max, s.Length)];
            var read = s.Read(buffer, 0, buffer.Length);
            // ASCII view plus UTF-16 view (many installers keep their strings in UTF-16).
            return Encoding.ASCII.GetString(buffer, 0, read) + "\n" + Encoding.Unicode.GetString(buffer, 0, read & ~1);
        }
        catch (Exception) { return ""; }
    }

    private static string Sha256(string file)
    {
        try
        {
            using var s = File.OpenRead(file);
            return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
        }
        catch (Exception) { return ""; }
    }
}
