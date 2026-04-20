using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace DonClaw.Shared;

/// <summary>
/// Lightweight "new version available?" check against GitHub Releases.
///
/// <para>Deliberately <b>not</b> a full auto-updater: the Node ships as a
/// loose build folder today, not a signed installer, so silently replacing
/// the running exe would be asking for partial-write corruption. Instead we
/// just ask GitHub for the latest release tag, compare against the embedded
/// <see cref="Program.Version"/>, and surface a "🔔 Update available" chip
/// in the Settings window that opens the releases page in the user's
/// browser. Once we have an MSI published on Releases we can plug in a real
/// updater (Velopack / Squirrel) behind the same interface.</para>
///
/// <para>Fail-closed: any error (rate-limited, no releases yet, no network)
/// returns <c>null</c> so the UI falls back to "no update visible" rather
/// than nagging the user with an ambiguous error.</para>
/// </summary>
public class UpdateChecker
{
    // Primary: ask api.donclaw.com so we don't need the GitHub repo to be
    // public. The backend proxies release metadata via a server-side PAT.
    // Fallback: direct GitHub API in case the backend is down or the user
    // isn't paired yet. If both fail we silently hide the update badge.
    private const string PrimaryReleasesUrl = "https://api.donclaw.com/node/version";
    private const string FallbackReleasesUrl = "https://api.github.com/repos/lucast1574/donclaw-node/releases/latest";
    private static readonly HttpClient s_http = CreateHttpClient();

    private static UpdateInfo? s_cached;
    private static DateTime s_cachedAt = DateTime.MinValue;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(30);

    private readonly IOpenClawLogger _logger;
    private readonly string _currentVersion;

    public UpdateChecker(string currentVersion, IOpenClawLogger? logger = null)
    {
        _currentVersion = currentVersion;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Check whether a newer release than <see cref="_currentVersion"/> is
    /// published on GitHub. Returns <c>null</c> on any failure or if the
    /// current build is already up-to-date.
    /// </summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        if (s_cached != null && DateTime.UtcNow - s_cachedAt < CacheDuration)
            return IsNewer(s_cached.Tag, _currentVersion) ? s_cached : null;

        var info = await TryFetchFromBackendAsync(ct) ?? await TryFetchFromGitHubAsync(ct);
        if (info == null) return null;

        s_cached = info;
        s_cachedAt = DateTime.UtcNow;

        return IsNewer(info.Tag, _currentVersion) ? info : null;
    }

    private async Task<UpdateInfo?> TryFetchFromBackendAsync(CancellationToken ct)
    {
        try
        {
            var dto = await s_http.GetFromJsonAsync<BackendVersionDto>(PrimaryReleasesUrl, ct);
            if (dto?.Tag == null) return null;
            return new UpdateInfo
            {
                Tag = StripVPrefix(dto.Tag),
                Name = dto.Name ?? dto.Tag,
                // Keep users inside donclaw.com rather than bouncing out to
                // the GitHub releases page: the in-app /download experience
                // shows the button, size + version on the same screen.
                HtmlUrl = "https://donclaw.com/download",
                PublishedAtIso = dto.PublishedAt ?? "",
            };
        }
        catch (Exception ex)
        {
            _logger.Debug($"[UpdateChecker] backend metadata unavailable: {ex.Message}");
            return null;
        }
    }

    private async Task<UpdateInfo?> TryFetchFromGitHubAsync(CancellationToken ct)
    {
        try
        {
            var release = await s_http.GetFromJsonAsync<GitHubReleaseDto>(FallbackReleasesUrl, ct);
            if (release?.TagName == null) return null;
            return new UpdateInfo
            {
                Tag = StripVPrefix(release.TagName),
                Name = release.Name ?? release.TagName,
                HtmlUrl = release.HtmlUrl ?? "https://donclaw.com/download",
                PublishedAtIso = release.PublishedAt ?? "",
            };
        }
        catch (Exception ex)
        {
            _logger.Debug($"[UpdateChecker] github fallback unavailable: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Semver-ish "a &gt; b" comparison that tolerates extra dots or
    /// pre-release suffixes. Falls back to a string compare for anything we
    /// can't parse.
    /// </summary>
    internal static bool IsNewer(string candidate, string current)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(current))
            return false;
        if (TryParse(candidate, out var c) && TryParse(current, out var n))
        {
            for (int i = 0; i < Math.Max(c.Length, n.Length); i++)
            {
                var cv = i < c.Length ? c[i] : 0;
                var nv = i < n.Length ? n[i] : 0;
                if (cv > nv) return true;
                if (cv < nv) return false;
            }
            return false;
        }
        return string.CompareOrdinal(StripVPrefix(candidate), StripVPrefix(current)) > 0;
    }

    private static bool TryParse(string raw, out int[] parts)
    {
        var clean = StripVPrefix(raw);
        // Drop pre-release suffix after '-' (e.g. "8.3.1-rc1")
        var dashIdx = clean.IndexOf('-');
        if (dashIdx > 0) clean = clean.Substring(0, dashIdx);
        var split = clean.Split('.');
        parts = new int[split.Length];
        for (int i = 0; i < split.Length; i++)
        {
            if (!int.TryParse(split[i], out parts[i])) { parts = Array.Empty<int>(); return false; }
        }
        return true;
    }

    private static string StripVPrefix(string s)
        => s.StartsWith('v') || s.StartsWith('V') ? s[1..] : s;

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        // GitHub requires a UA header on API requests.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("donclaw-node-update-check");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    public class UpdateInfo
    {
        public string Tag { get; set; } = "";
        public string Name { get; set; } = "";
        public string HtmlUrl { get; set; } = "";
        public string PublishedAtIso { get; set; } = "";
    }

    private class GitHubReleaseDto
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("published_at")] public string? PublishedAt { get; set; }
    }

    private class BackendVersionDto
    {
        [JsonPropertyName("tag")] public string? Tag { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("publishedAt")] public string? PublishedAt { get; set; }
    }
}
