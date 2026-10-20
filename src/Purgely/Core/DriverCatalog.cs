using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Purgely.Core;

/// <summary>A driver package in the Microsoft Update Catalog.</summary>
public sealed record CatalogDriver(string UpdateId, string Title, string Products, string Classification,
                                   DateTime? LastUpdated, string Version, string Size, string MatchedId, bool ExactModel)
{
    /// <summary>
    /// The provider at the start of the title. Two title styles exist: "Intel - Net - 23.60.0.10" (older) and
    /// "Intel Corporation Bluetooth Driver Update (24.70.0.4)" (newer) – the second one used to give "" and every
    /// update of that style was missed.
    /// </summary>
    public string Vendor
    {
        get
        {
            if (Title.Contains(" - ")) return Title[..Title.IndexOf(" - ", StringComparison.Ordinal)].Trim();
            var m = Regex.Match(Title, @"^(.+?)\s+(?:\S+\s+)?Driver Update\b", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value.Trim();
            var paren = Title.IndexOf('(');
            return paren > 0 ? Title[..paren].Trim() : "";
        }
    }
}

/// <summary>Result of looking up one device: the best package, or why there is none.</summary>
public sealed record CatalogLookup(CatalogDriver? Driver, bool SameFamily, bool Failed, int Hits = 0, string NewestSeen = "");

/// <summary>
/// Microsoft Update Catalog (catalog.update.microsoft.com) – Microsoft's official, free store of signed drivers,
/// searched by hardware ID the way professional tools do.
/// Rules (council 2026-10-16): never firmware/BIOS/boot-disk controllers/add-ons; only x64 Windows 10/11 packages;
/// "newer" only within the same driver family; download only from Microsoft servers with a matching hash; the package
/// must be WHQL-signed and its .inf must list this device; the current driver is exported and the Undo record written
/// before anything is installed; Undo removes only packages this install added.
/// </summary>
public static partial class DriverCatalog
{
    private const string Site = "https://www.catalog.update.microsoft.com/";
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Purgely");
        return client;
    }

    /// <summary>The hardware IDs to search, most specific first. "Exact" = identifies this PC's model, not just the chip.</summary>
    public static IEnumerable<(string Id, bool Exact)> SearchIds(HardwareDevice device)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // The catalog's search is a text search: "USB\VID_8087&PID_0026&REV_0002" fills the first page with older packages
        // that list that revision, and newer ones (listing only VID/PID) never show up. Search the ID without &REV_ too.
        var ids = new List<string>();
        foreach (var raw in device.HardwareIds)
        {
            var id = raw.Trim();
            ids.Add(id);
            var noRev = Regex.Replace(id, @"&REV_[0-9A-Fa-f]+", "", RegexOptions.IgnoreCase);
            if (!noRev.Equals(id, StringComparison.OrdinalIgnoreCase)) ids.Add(noRev);
        }
        // The chip itself ("PCI\VEN_8086&DEV_9A60", without the PC maker's board ID): Intel, NVIDIA and AMD packages
        // usually list only that – the board-specific IDs above then find nothing ("Not in the catalog").
        ids.AddRange(device.HardwareIds.Select(BaseId).Where(b => b.Length > 0));
        // Class-code IDs ("&CC_0300") match whole device families – last.
        ids = ids.OrderBy(i => i.Contains("&CC_", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var id in ids)
        {
            if (id.Length < 8 || !seen.Add(id)) continue;
            var upper = id.ToUpperInvariant();
            var exact =
                upper.Contains("SUBSYS_") ||                                            // PCI and HD Audio with board ID
                (upper.StartsWith("USB\\") || upper.StartsWith("BTH")) && upper.Contains("&PID_") ||
                (upper.StartsWith("ACPI\\") && !upper.StartsWith("ACPI\\PNP") && !upper.StartsWith("ACPI\\ACPI") &&
                 !upper.StartsWith("ACPI\\VEN_PNP") && !upper.StartsWith("ACPI\\VEN_ACPI"));
            yield return (id, exact);
        }
    }

    /// <summary>"PCI\VEN_8086&DEV_9A60&SUBSYS_14771025&REV_01" → "PCI\VEN_8086&DEV_9A60" (the chip, as Windows' compatible ID).</summary>
    public static string BaseId(string id)
    {
        if (id.Contains("&CC_", StringComparison.OrdinalIgnoreCase)) return "";
        var b = Regex.Replace(id.Trim(), @"&(SUBSYS|REV)_[0-9A-Fa-f]+", "", RegexOptions.IgnoreCase);
        // Only chip-level IDs with both a vendor and a device part are useful (never just "PCI\VEN_8086").
        return Regex.IsMatch(b, @"^(PCI|HDAUDIO\\FUNC_\d+)\\?.*VEN_[0-9A-F]{4}&DEV_[0-9A-F]{4}$", RegexOptions.IgnoreCase) &&
               !b.Equals(id.Trim(), StringComparison.OrdinalIgnoreCase) ? b : "";
    }

    public static bool IsFirmwareTitle(string title, string classification = "") =>
        classification.Contains("Firmware", StringComparison.OrdinalIgnoreCase) ||
        Regex.IsMatch(title, @"\b(firmware|bios|uefi|capsule|ec\s*update|me\s*firmware|thunderbolt\s*nvm)\b", RegexOptions.IgnoreCase);

    /// <summary>Finds the best newer package for a device. Failed = the Catalog could not be reached.</summary>
    public static async Task<CatalogLookup> FindNewerAsync(HardwareDevice device, CancellationToken cancel = default)
    {
        var current = ParseVersion(device.DriverVersion);   // null = no driver installed
        var failures = 0;
        var tried = 0;
        var hits = 0;
        Version? newestSeen = null;
        var sortedOnce = false; // the extra "newest first" requests: for one ID per device, to stay gentle with the site
        foreach (var (id, exact) in SearchIds(device).Take(8))
        {
            tried++;
            List<CatalogDriver> found;
            try
            {
                found = await SearchAsync(id, exact, cancel, newestFirst: !sortedOnce).ConfigureAwait(false);
                if (found.Count > 25) sortedOnce = true;
            }
            catch (Exception) { failures++; continue; }

            var usable = found
                .Where(d => !IsFirmwareTitle(d.Title, d.Classification))
                .Where(d => d.Products.Contains("Windows 11", StringComparison.OrdinalIgnoreCase) ||
                            d.Products.Contains("Windows 10", StringComparison.OrdinalIgnoreCase))
                .Where(d => ParseVersion(d.Version) is not null)
                .ToList();
            hits += usable.Count;
            // Newest version seen (same maker preferred) – shown as "Up to date (catalog: …)".
            var sameMaker = usable.Where(d => SameVendor(d.Vendor, device.DriverProvider)).ToList();
            foreach (var d in sameMaker.Count > 0 ? sameMaker : usable)
                if (ParseVersion(d.Version) is { } v && (newestSeen is null || v > newestSeen)) newestSeen = v;

            // Same family (same provider as the installed driver) and really newer – a true update.
            var family = usable
                .Where(d => current is not null && SameVendor(d.Vendor, device.DriverProvider) && ParseVersion(d.Version) > current)
                .OrderByDescending(d => ParseVersion(d.Version)).ThenByDescending(d => d.LastUpdated).FirstOrDefault();
            if (family is not null) return new CatalogLookup(family, true, false, hits);

            // No driver, or Windows' generic one: the maker's driver is an alternative (versions aren't comparable).
            if (current is null || device.IsGeneric)
            {
                var alternative = usable.OrderByDescending(d => d.LastUpdated).ThenByDescending(d => ParseVersion(d.Version)).FirstOrDefault();
                if (alternative is not null) return new CatalogLookup(alternative, false, false, hits);
            }
        }
        return new CatalogLookup(null, false, tried > 0 && failures == tried, hits, newestSeen?.ToString() ?? "");
    }

    private static bool SameVendor(string vendor, string provider)
    {
        var v = Keywords.CleanPublisher(vendor);
        var p = Keywords.CleanPublisher(provider);
        return v.Length >= 3 && p.Length >= 3 && (v.Contains(p) || p.Contains(v));
    }

    /// <summary>Results per hardware ID for this Purgely session – a second check doesn't ask the catalog again.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<CatalogDriver>> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    public static async Task<List<CatalogDriver>> SearchAsync(string hardwareId, bool exact, CancellationToken cancel = default,
                                                              bool newestFirst = true)
    {
        var cacheKey = $"{hardwareId}|{exact}|{newestFirst}";
        if (Cache.TryGetValue(cacheKey, out var cached)) return cached;
        var found = await SearchUncachedAsync(hardwareId, exact, newestFirst, cancel).ConfigureAwait(false);
        Cache[cacheKey] = found;
        return found;
    }

    private static async Task<List<CatalogDriver>> SearchUncachedAsync(string hardwareId, bool exact, bool newestFirst, CancellationToken cancel)
    {
        var url = $"{Site}Search.aspx?q={Uri.EscapeDataString(hardwareId)}";
        var html = await Http.GetStringAsync(url, cancel).ConfigureAwait(false);
        var result = Parse(html, hardwareId, exact);

        // The catalog shows only 25 results per page, oldest-first for busy chips (Intel lists 1000+ packages for one
        // chipset ID) – the newest driver was simply never seen. When there is more than one page, ask the catalog to
        // sort by "Last Updated" (the same click a person would make) and add those rows. Any problem → first page only.
        if (newestFirst && MorePagesRegex().IsMatch(html))
        {
            try
            {
                var sorted = await PostBackAsync(url, html, SortByDateTarget, cancel).ConfigureAwait(false);
                var rows = Parse(sorted, hardwareId, exact);
                // A header click may sort ascending first – then click it once more for newest first.
                if (rows.Count > 1 && rows[0].LastUpdated < rows[^1].LastUpdated)
                    rows = Parse(await PostBackAsync(url, sorted, SortByDateTarget, cancel).ConfigureAwait(false), hardwareId, exact);
                var known = new HashSet<string>(result.Select(r => r.UpdateId), StringComparer.OrdinalIgnoreCase);
                result.AddRange(rows.Where(r => known.Add(r.UpdateId)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* first page is still a valid answer */ }
        }
        return result;
    }

    private const string SortByDateTarget = "ctl00$catalogBody$updateMatches$ctl02$dateComputedHeaderLink";

    /// <summary>Repeats what a click on a catalog link does: posts the page's own form back with that link as the target.</summary>
    private static async Task<string> PostBackAsync(string url, string page, string target, CancellationToken cancel)
    {
        var fields = new List<KeyValuePair<string, string>>();
        foreach (Match input in HiddenInputRegex().Matches(page))
        {
            var tag = input.Value;
            var name = NameAttributeRegex().Match(tag);
            if (!name.Success) continue;
            var value = ValueAttributeRegex().Match(tag);
            var key = System.Net.WebUtility.HtmlDecode(name.Groups[1].Value);
            if (key is "__EVENTTARGET" or "__EVENTARGUMENT") continue;
            fields.Add(new(key, value.Success ? System.Net.WebUtility.HtmlDecode(value.Groups[1].Value) : ""));
        }
        if (!fields.Any(f => f.Key == "__VIEWSTATE")) throw new InvalidOperationException("no page state");
        fields.Add(new("__EVENTTARGET", target));
        fields.Add(new("__EVENTARGUMENT", ""));
        using var content = new FormUrlEncodedContent(fields);
        using var response = await Http.PostAsync(url, content, cancel).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
    }


    private static List<CatalogDriver> Parse(string html, string hardwareId, bool exact)
    {
        var result = new List<CatalogDriver>();
        foreach (Match row in RowRegex().Matches(html))
        {
            var cells = CellRegex().Matches(row.Groups[2].Value).Select(c => Clean(c.Groups[1].Value)).ToList();
            var updateId = row.Groups[1].Value;
            // Columns: [0] blank, [1] Title, [2] Products, [3] Classification, [4] Last updated, [5] Version, [6] Size, [7] Download
            var start = cells.Count >= 8 ? 1 : 0;
            if (cells.Count < start + 6) continue;
            DateTime.TryParse(cells[start + 3], System.Globalization.CultureInfo.GetCultureInfo("en-US"),
                System.Globalization.DateTimeStyles.None, out var updated);
            result.Add(new CatalogDriver(updateId, cells[start], cells[start + 1], cells[start + 2],
                updated == default ? null : updated, cells[start + 4], cells[start + 5], hardwareId, exact));
        }
        return result;
    }

    /// <summary>Downloads, verifies and installs a catalog driver for one device (run it off the UI thread).</summary>
    public static async Task<string> InstallAsync(HardwareDevice device, IProgress<string> progress, CancellationToken cancel = default)
    {
        using var busy = Activity.Begin();
        var work = Path.Combine(Path.GetTempPath(), "Purgely-driver-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        try
        {
            // 1. Where to download from, and its fingerprint.
            progress.Report("Finding the download…");
            var (url, digest) = await DownloadInfoAsync(device.UpdateId, cancel).ConfigureAwait(false);
            if (url is null) throw new InvalidOperationException("The catalog didn't provide a download link.");
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                !(uri.Host.EndsWith(".windowsupdate.com", StringComparison.OrdinalIgnoreCase) ||
                  uri.Host.EndsWith(".microsoft.com", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Refused: the download is not on a Microsoft server ({uri?.Host}).");

            // 2. Download with progress (https first, Microsoft's plain-http mirror only together with the hash check).
            var cab = Path.Combine(work, Path.GetFileName(uri.AbsolutePath));
            try { await DownloadAsync(new UriBuilder(uri) { Scheme = "https", Port = -1 }.Uri, cab, progress, cancel).ConfigureAwait(false); }
            catch (HttpRequestException) when (uri.Scheme == "http" && digest is not null)
            { await DownloadAsync(uri, cab, progress, cancel).ConfigureAwait(false); }

            // 3. Same file Microsoft published? (the catalog lists its SHA-1)
            progress.Report("Checking the download…");
            if (digest is not null)
            {
                await using var stream = File.OpenRead(cab);
                var hash = Convert.ToBase64String(await SHA1.HashDataAsync(stream, cancel).ConfigureAwait(false));
                if (!string.Equals(hash, digest, StringComparison.Ordinal))
                    throw new InvalidOperationException("Refused: the download doesn't match Microsoft's fingerprint.");
            }

            // 4. Unpack (tar keeps sub-folders; expand as a fallback).
            progress.Report("Unpacking…");
            var unpacked = Path.Combine(work, "driver");
            Directory.CreateDirectory(unpacked);
            var (tarCode, _) = Shell.Run("tar.exe", $"-xf \"{cab}\" -C \"{unpacked}\"", 600_000);
            if (tarCode != 0 || !Directory.EnumerateFileSystemEntries(unpacked).Any())
            {
                var (expandCode, expandOut) = Shell.Run("expand.exe", $"-F:* \"{cab}\" \"{unpacked}\"", 600_000);
                if (expandCode != 0) throw new InvalidOperationException($"Could not unpack the driver: {expandOut}");
            }

            // 5. Signed by Microsoft's hardware program (WHQL)? Either the cab itself or the package's .cat.
            var cabSig = Signature.Check(cab);
            var catOk = Directory.EnumerateFiles(unpacked, "*.cat", SearchOption.AllDirectories)
                .Select(Signature.Check)
                .Any(s => s.Trusted && s.Signer.StartsWith("Microsoft Windows Hardware Compatibility", StringComparison.OrdinalIgnoreCase));
            if (!(cabSig.Trusted && cabSig.Signer.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)) && !catOk)
                throw new InvalidOperationException("Refused: the package is not signed by Microsoft's hardware program (WHQL).");

            // 6. Right device, right class, right architecture.
            var matching = Directory.EnumerateFiles(unpacked, "*.inf", SearchOption.AllDirectories)
                .Where(inf => InfFits(inf, device, out _)).ToList();
            if (matching.Count == 0)
            {
                var reason = Directory.EnumerateFiles(unpacked, "*.inf", SearchOption.AllDirectories)
                    .Select(inf => { InfFits(inf, device, out var why); return why; }).FirstOrDefault() ?? "no driver file found";
                throw new InvalidOperationException($"Refused: {reason}.");
            }

            // 7. Save the current driver and write the Undo record BEFORE installing.
            progress.Report("Saving the current driver (for Undo)…");
            var package = UndoStore.CreatePackage($"Driver {device.Name}");
            var backup = Path.Combine(package, "previous-driver");
            Directory.CreateDirectory(backup);
            if (device.InfName.StartsWith("oem", StringComparison.OrdinalIgnoreCase))
            {
                var (exportCode, exportOut) = Shell.Run("pnputil.exe", $"/export-driver {device.InfName} \"{backup}\"", 300_000);
                if (exportCode != 0 || !Directory.EnumerateFiles(backup, "*.inf", SearchOption.AllDirectories).Any())
                    throw new InvalidOperationException($"Refused: the current driver could not be saved for Undo ({LastLine(exportOut)}).");
            }
            var added = new List<string>();
            UndoStore.AddDriverAction(package, $"Driver {device.Name}", device.Name, backup, added);

            // 8. Install. The new oem*.inf files are found by comparing the driver store before/after – this works
            //    in every Windows language and never picks up a package that already existed (shared with others).
            progress.Report("Installing…");
            var result = "Installed";
            try
            {
                foreach (var inf in matching)
                {
                    var before = OemInfs();
                    var (code, output) = Shell.Run("pnputil.exe", $"/add-driver \"{inf}\" /install", 600_000);
                    added.AddRange(OemInfs().Except(before, StringComparer.OrdinalIgnoreCase));
                    result = code switch
                    {
                        0 => result,
                        3010 => "Installed – restart needed",
                        259 => "Added, but Windows kept the current driver",
                        _ => throw new InvalidOperationException($"Windows refused the driver (code {code}): {LastLine(output)}"),
                    };
                }
            }
            finally
            {
                UndoStore.AddDriverAction(package, $"Driver {device.Name}", device.Name, backup, added);
            }

            Timeline.Log(TimelineEventType.Updated, device.Name,
                $"Driver {device.DriverVersion} → {device.UpdateVersion} from the Microsoft Update Catalog – can be undone");
            return result;
        }
        finally
        {
            FileMover.DeleteBestEffort(work, true);
        }
    }

    private static HashSet<string> OemInfs()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF");
        try { return Directory.EnumerateFiles(dir, "oem*.inf").Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase)!; }
        catch (Exception) { return new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
    }

    private static async Task DownloadAsync(Uri uri, string target, IProgress<string> progress, CancellationToken cancel)
    {
        using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? 0;
        var free = new DriveInfo(Path.GetPathRoot(target)!).AvailableFreeSpace;
        if (total > 0 && total * 3 > free) throw new InvalidOperationException("Not enough free disk space to download and unpack the driver.");

        await using var source = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
        await using var file = File.Create(target);
        var buffer = new byte[1 << 16];
        long done = 0;
        var lastReport = DateTime.MinValue;
        int read;
        while ((read = await source.ReadAsync(buffer, cancel).ConfigureAwait(false)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), cancel).ConfigureAwait(false);
            done += read;
            if (DateTime.Now - lastReport > TimeSpan.FromMilliseconds(300))
            {
                lastReport = DateTime.Now;
                progress.Report(total > 0 ? $"Downloading… {done * 100 / total}% ({Sizes.Format(done)} of {Sizes.Format(total)})"
                                          : $"Downloading… {Sizes.Format(done)}");
            }
        }
    }

    private static async Task<(string? Url, string? Digest)> DownloadInfoAsync(string updateId, CancellationToken cancel)
    {
        var body = $"[{{\"size\":0,\"languages\":\"\",\"uidInfo\":\"{updateId}\",\"updateID\":\"{updateId}\"}}]";
        using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["updateIDs"] = body });
        using var response = await Http.PostAsync($"{Site}DownloadDialog.aspx", content, cancel).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
        var url = DownloadUrlRegex().Match(text);
        var digest = DigestRegex().Match(text);
        return (url.Success ? url.Groups[1].Value : null, digest.Success ? digest.Groups[1].Value : null);
    }

    /// <summary>
    /// The .inf must: list this device's hardware ID, be for this PC's architecture, and not be a firmware,
    /// add-on or boot-controller class.
    /// </summary>
    private static bool InfFits(string inf, HardwareDevice device, out string why)
    {
        why = "";
        string text;
        try { text = File.ReadAllText(inf); } catch (Exception) { why = "the driver file can't be read"; return false; }

        var cls = Regex.Match(text, @"^\s*Class\s*=\s*""?([A-Za-z0-9]+)", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        if (cls.Success && Hardware.NeverOfferClasses.Contains(cls.Groups[1].Value))
        { why = $"it is a {cls.Groups[1].Value} package (firmware, add-on or boot controller)"; return false; }
        if (text.Contains("f2e7dd72-6468-4e36-b6f1-6488f42c1b52", StringComparison.OrdinalIgnoreCase))
        { why = "it is a firmware package"; return false; }

        var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture ==
                   System.Runtime.InteropServices.Architecture.Arm64 ? "NTarm64" : "NTamd64";
        if (!text.Contains(arch, StringComparison.OrdinalIgnoreCase))
        { why = $"it is not made for this PC's architecture ({arch})"; return false; }

        var ids = device.HardwareIds.Where(id => id.Length >= 8)
            .SelectMany(id => new[] { id, Regex.Replace(id, @"&REV_[0-9A-Fa-f]+", "", RegexOptions.IgnoreCase), BaseId(id) })
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (!ids.Any(id => Regex.IsMatch(text, $@"^[^;\r\n]*,\s*{Regex.Escape(id)}\s*(,|$)", RegexOptions.Multiline | RegexOptions.IgnoreCase)))
        { why = "it does not list this device"; return false; }
        return true;
    }

    public static Version? ParseVersion(string text)
    {
        var m = Regex.Match(text ?? "", @"\d+(\.\d+){1,3}");
        return m.Success && Version.TryParse(m.Value, out var v) ? v : null;
    }

    private static string Clean(string html) =>
        System.Net.WebUtility.HtmlDecode(Regex.Replace(Regex.Replace(html, "<[^>]+>", " "), @"\s+", " ")).Trim();

    private static string LastLine(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? output;

    [GeneratedRegex(@"<tr id=""([0-9a-fA-F-]{36})_R\d+""[^>]*>(.*?)</tr>", RegexOptions.Singleline)]
    private static partial Regex RowRegex();

    [GeneratedRegex(@"(?<![\w-])name=""([^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex NameAttributeRegex();

    [GeneratedRegex(@"(?<![\w-])value=""([^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex ValueAttributeRegex();

    [GeneratedRegex(@"<input[^>]*type=""hidden""[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex HiddenInputRegex();

    /// <summary>"(page 1 of 40)" – more than one page of results.</summary>
    [GeneratedRegex(@"\(page\s+1\s+of\s+([2-9]|\d{2,})\)", RegexOptions.IgnoreCase)]
    private static partial Regex MorePagesRegex();

    [GeneratedRegex(@"<td[^>]*>(.*?)</td>", RegexOptions.Singleline)]
    private static partial Regex CellRegex();

    [GeneratedRegex(@"files\[\d+\]\.url\s*=\s*'([^']+)'", RegexOptions.IgnoreCase)]
    private static partial Regex DownloadUrlRegex();

    [GeneratedRegex(@"files\[\d+\]\.digest\s*=\s*'([^']+)'", RegexOptions.IgnoreCase)]
    private static partial Regex DigestRegex();
}
