using System.Net.Http;
using System.Text.Json;

namespace Purgely.Core;

/// <summary>A newer Purgely release on GitHub.</summary>
public sealed record PurgelyRelease(Version Version, string Tag, string PageUrl, bool IsPreview);

/// <summary>
/// Asks GitHub whether a newer Purgely was released. Only reads the public "latest release" information –
/// nothing about the PC is sent, and nothing is downloaded or installed: the user opens the release page.
/// </summary>
public static class UpdateCheck
{
    public const string ReleasesPage = "https://github.com/kraljforgelabs/Purgely/releases";
    private const string ApiUrl = "https://api.github.com/repos/kraljforgelabs/Purgely/releases?per_page=10";

    public static Version CurrentVersion =>
        typeof(UpdateCheck).Assembly.GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(0, 0, 0);

    /// <summary>
    /// Returns the newest release that is newer than this copy, or null (also when offline or GitHub can't be reached).
    /// Previews (beta) are offered only to people who run a preview themselves.
    /// </summary>
    public static async Task<PurgelyRelease?> FindNewerAsync(CancellationToken cancel = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"Purgely/{CurrentVersion}");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            using var response = await http.GetAsync(ApiUrl, cancel);
            if (!response.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancel));
            var current = CurrentVersion;
            var acceptPreview = current.Major < 1; // everything before 1.0 is a preview
            PurgelyRelease? best = null;
            foreach (var r in doc.RootElement.EnumerateArray())
            {
                if (r.TryGetProperty("draft", out var draft) && draft.GetBoolean()) continue;
                var preview = r.TryGetProperty("prerelease", out var pre) && pre.GetBoolean();
                if (preview && !acceptPreview) continue;
                var tag = r.GetProperty("tag_name").GetString() ?? "";
                if (Parse(tag) is not { } version || version <= current) continue;
                var url = r.TryGetProperty("html_url", out var u) ? u.GetString() ?? ReleasesPage : ReleasesPage;
                // Only GitHub pages of this project are ever opened.
                if (!IsReleaseUrl(url)) url = ReleasesPage;
                if (best is null || version > best.Version) best = new PurgelyRelease(version, tag, url, preview);
            }
            return best;
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// True only for https://github.com/kraljforgelabs/Purgely/releases… – checked again right before opening, because the
    /// cached link lives in a per-user file that programs without administrator rights could change.
    /// </summary>
    public static bool IsReleaseUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        (uri.AbsolutePath.Equals("/kraljforgelabs/Purgely/releases", StringComparison.OrdinalIgnoreCase) ||
         uri.AbsolutePath.StartsWith("/kraljforgelabs/Purgely/releases/", StringComparison.OrdinalIgnoreCase));

    /// <summary>Opens a release page in the browser – only a checked GitHub link, otherwise the project's releases page.</summary>
    public static void OpenReleasePage(string? url)
    {
        var target = IsReleaseUrl(url) ? url! : ReleasesPage;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true })?.Dispose(); }
        catch (Exception) { }
    }

    /// <summary>"v0.9.1", "0.9.1-beta" → 0.9.1.</summary>
    public static Version? Parse(string tag)
    {
        var text = tag.Trim().TrimStart('v', 'V');
        var end = 0;
        while (end < text.Length && (char.IsDigit(text[end]) || text[end] == '.')) end++;
        if (!Version.TryParse(text[..end].TrimEnd('.'), out var v)) return null;
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
    }

    /// <summary>
    /// Checks at most once a day, only when the user allows it in Settings. Between checks, a release found earlier is
    /// returned again (until this copy is at least that new, or the user skipped that version).
    /// </summary>
    public static async Task<PurgelyRelease?> CheckIfDueAsync()
    {
        var settings = AppSettings.Current;
        if (!settings.CheckForUpdates) return null;
        PurgelyRelease? found;
        if (settings.LastUpdateCheck is { } last && last <= DateTime.Now && DateTime.Now - last < TimeSpan.FromHours(20))
        {
            found = Parse(settings.AvailableTag) is { } known && known > CurrentVersion
                ? new PurgelyRelease(known, settings.AvailableTag, IsReleaseUrl(settings.AvailableUrl) ? settings.AvailableUrl : ReleasesPage, false)
                : null;
        }
        else
        {
            found = await FindNewerAsync();
            settings.LastUpdateCheck = DateTime.Now;
            settings.AvailableTag = found?.Tag ?? "";
            settings.AvailableUrl = found?.PageUrl ?? "";
            AppSettings.Save();
        }
        if (found is not null && string.Equals(found.Tag, settings.SkippedVersion, StringComparison.OrdinalIgnoreCase)) return null;
        return found;
    }
}
