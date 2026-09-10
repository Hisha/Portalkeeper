using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
namespace Portalkeeper.Services;

public enum ReleaseCheckState { NotChecked, UpToDate, Available, Failed }
public sealed record ReleaseCheckResult(ReleaseCheckState State, string Message, string? Tag = null, string? Version = null);

// Advisory release information only; this service never downloads application assets.
public sealed class PortalkeeperReleaseService
{
    public const string LatestReleaseEndpoint = "https://api.github.com/repos/Hisha/Portalkeeper/releases/latest";
    private static readonly HttpClient SharedHttp = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(8), MaxResponseContentBufferSize = 1024 * 1024 };
    private readonly HttpClient _http;
    private readonly Func<DateTimeOffset> _now;
    private readonly SemaphoreSlim _checkLock = new(1, 1);
    public PortalkeeperReleaseService(HttpClient? http = null, Func<DateTimeOffset>? now = null)
    { _http = http ?? SharedHttp; _now = now ?? (() => DateTimeOffset.UtcNow); }

    public static bool IsCheckDue(DateTimeOffset? last, DateTimeOffset now) =>
        last is null || now < last.Value || now - last.Value >= TimeSpan.FromHours(24);

    public static bool TryStableTag(string? tag, out string version)
    {
        version = "";
        if (tag is null || tag.Length > 128) return false;
        var match = Regex.Match(tag, @"\A[vV]?(?<version>(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?)\z", RegexOptions.CultureInvariant);
        if (!match.Success) return false;
        try
        {
            version = match.Groups["version"].Value;
            RealmConfigurationService.CompareVersions(version, version);
            return true;
        }
        catch (Exception) { version = ""; return false; }
    }
    public static Uri? ReleaseUri(string? tag) => TryStableTag(tag, out _)
        ? new Uri("https://github.com/Hisha/Portalkeeper/releases/tag/" + Uri.EscapeDataString(tag!)) : null;

    private static ReleaseCheckResult Describe(string? tag, string installed)
    {
        if (!TryStableTag(tag, out var version))
            return new(ReleaseCheckState.NotChecked, "Use CHECK FOR UPDATES to check for a newer stable release.");
        return RealmConfigurationService.CompareVersions(version, installed) > 0
            ? new(ReleaseCheckState.Available, $"Portalkeeper {version} is available.", tag, version)
            : new(ReleaseCheckState.UpToDate, "Portalkeeper is up to date.", tag, version);
    }
    private static void Persist(PortalkeeperSettings settings, Action<PortalkeeperSettings> save)
    {
        try { save(settings); }
        catch (Exception) { /* Read-only settings must not block startup or realm use. */ }
    }
    public async Task<ReleaseCheckResult> CheckAsync(PortalkeeperSettings settings, string installed,
        bool manual, Action<PortalkeeperSettings> save)
    {
        await _checkLock.WaitAsync();
        try
        {
            var now = _now();
            if (!manual && !IsCheckDue(settings.LastPortalkeeperUpdateCheckUtc, now))
                return Describe(settings.LatestPortalkeeperReleaseTag, installed);
            // Record attempts, including failures, so an offline user is not queried every launch.
            settings.LastPortalkeeperUpdateCheckUtc = now;
            Persist(settings, save);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseEndpoint);
            request.Headers.UserAgent.ParseAdd("Portalkeeper/" + installed);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await _http.SendAsync(request, timeout.Token);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            var release = json.RootElement;
            if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean())
                throw new InvalidOperationException("Not a stable release.");
            var tag = release.GetProperty("tag_name").GetString();
            if (!TryStableTag(tag, out _)) throw new InvalidOperationException("Invalid stable release version.");
            var result = Describe(tag, installed);
            // Never trust html_url or asset URLs supplied in the response for navigation.
            settings.LatestPortalkeeperReleaseTag = tag;
            Persist(settings, save);
            return result;
        }
        catch (Exception) { return new(ReleaseCheckState.Failed, "Unable to check for updates."); }
        finally { _checkLock.Release(); }
    }
}
