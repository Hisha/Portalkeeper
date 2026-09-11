using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Portalkeeper.Models;

namespace Portalkeeper.Services;

public sealed class GitHubAddonSourceService
{
    private static readonly HttpClient SharedHttpClient = CreateHttpClient();
    private readonly HttpClient HttpClient;

    private static readonly JsonSerializerOptions JsonOptions =
        new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };

    private readonly string _cachePath;
    private GitHubSourceCache _cache;

    public GitHubAddonSourceService(HttpClient? httpClient = null, string? cacheDirectory = null)
    {
        HttpClient = httpClient ?? SharedHttpClient;
        var applicationData =
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        var portalkeeperDirectory =
            cacheDirectory ?? Path.Combine(applicationData, "Portalkeeper");

        Directory.CreateDirectory(portalkeeperDirectory);

        _cachePath = Path.Combine(
            portalkeeperDirectory,
            "github-addon-cache.json");

        _cache = LoadCache();
    }

    public async Task<AddonManifest> ResolveManifestAsync(
        AddonManifest manifest)
    {
        var resolved = new List<AddonDefinition>();

        foreach (var addon in manifest.Addons)
        {
            resolved.Add(
                addon.IsGitHubSource
                    ? await ResolveAsync(addon)
                    : addon);
        }

        return new AddonManifest
        {
            ManifestVersion = manifest.ManifestVersion,
            Addons = resolved
        };
    }

    public async Task<AddonDefinition> ResolveAsync(
        AddonDefinition addon)
    {
        if (!TryParseRepositoryUrl(
                addon.GitUrl,
                out var owner,
                out var repository))
        {
            throw new InvalidDataException(
                $"Unsupported GitHub repository URL for {addon.Name}: {addon.GitUrl}");
        }

        var cacheKey = $"{owner}/{repository}|{addon.Ref}|{addon.AddonPath}|{addon.Folder}";

        _cache.Repositories.TryGetValue(
            cacheKey,
            out var cached);

        try
        {
            try
            {
                return await ResolveUsingApiAsync(addon, owner, repository, cacheKey, cached);
            }
            catch (GitHubApiRateLimitException)
            {
                return await ResolveWithoutApiAsync(addon, owner, repository, cacheKey, cached);
            }
        }
        catch (Exception ex) when (cached is not null && (ex is HttpRequestException || ex is TaskCanceledException))
        {
            return BuildResolvedDefinition(addon, repository, cached.DefaultBranch, cached.Commit,
                cached.AddonPath, cached.Folder, cached.Version, "Source unavailable; using cached version/commit information.");
        }
    }

    private async Task<AddonDefinition> ResolveUsingApiAsync(
        AddonDefinition addon,
        string owner,
        string repository,
        string cacheKey,
        GitHubSourceCacheEntry? cached)
    {
        var defaultBranch = !string.IsNullOrWhiteSpace(addon.Ref) ? addon.Ref : cached?.DefaultBranch ?? string.Empty;

        if (string.IsNullOrWhiteSpace(defaultBranch))
        {
            var repositoryInfo = await GetJsonAsync<RepositoryResponse>(
                $"https://api.github.com/repos/{owner}/{repository}");

            defaultBranch = repositoryInfo.DefaultBranch;

            if (string.IsNullOrWhiteSpace(defaultBranch))
            {
                throw new InvalidDataException(
                    $"GitHub did not report a default branch for {addon.GitUrl}.");
            }
        }

        CommitResponse commit;
        try
        {
            commit = await GetJsonAsync<CommitResponse>(
                $"https://api.github.com/repos/{owner}/{repository}/commits/{Uri.EscapeDataString(defaultBranch)}");
        }
        catch (HttpRequestException) when (cached is not null && string.IsNullOrWhiteSpace(addon.Ref))
        {
            // The cached default branch may have changed. Refresh repository metadata once.
            var repositoryInfo = await GetJsonAsync<RepositoryResponse>(
                $"https://api.github.com/repos/{owner}/{repository}");

            defaultBranch = repositoryInfo.DefaultBranch;
            commit = await GetJsonAsync<CommitResponse>(
                $"https://api.github.com/repos/{owner}/{repository}/commits/{Uri.EscapeDataString(defaultBranch)}");
        }

        if (string.IsNullOrWhiteSpace(commit.Sha))
        {
            throw new InvalidDataException(
                $"GitHub did not report a commit for {addon.GitUrl}.");
        }

        if (cached is not null &&
            cached.Commit.Equals(commit.Sha, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(cached.Folder) &&
            !string.IsNullOrWhiteSpace(cached.Version))
        {
            return BuildResolvedDefinition(
                addon,
                repository,
                defaultBranch,
                commit.Sha,
                cached.AddonPath,
                cached.Folder,
                cached.Version);
        }

        if (string.IsNullOrWhiteSpace(commit.Commit.Tree.Sha))
        {
            throw new InvalidDataException(
                $"GitHub did not report a tree for {addon.GitUrl}.");
        }

        var tree = await GetJsonAsync<TreeResponse>(
            $"https://api.github.com/repos/{owner}/{repository}/git/trees/{commit.Commit.Tree.Sha}?recursive=1");

        if (tree.Truncated)
        {
            throw new InvalidDataException(
                $"The GitHub repository tree for {addon.Name} is too large for automatic addon discovery. " +
                "Set addonPath in the manifest for this repository.");
        }

        var tocPath = SelectTocPath(
            tree.Tree
                .Where(item =>
                    item.Type.Equals("blob", StringComparison.OrdinalIgnoreCase))
                .Select(item => item.Path),
            repository,
            addon.AddonPath);

        var rawTocUrl =
            $"https://raw.githubusercontent.com/{owner}/{repository}/{commit.Sha}/{EscapePath(tocPath)}";

        var tocText = await HttpClient.GetStringAsync(rawTocUrl);
        var version = ReadTocVersion(tocText);

        if (string.IsNullOrWhiteSpace(version))
        {
            throw new InvalidDataException(
                $"Portalkeeper found '{tocPath}' in {addon.Name}, but it does not contain '## Version:'.");
        }

        return CacheAndBuildResolvedDefinition(
            addon,
            repository,
            cacheKey,
            defaultBranch,
            commit.Sha,
            tocPath,
            version);
    }

    private async Task<AddonDefinition> ResolveWithoutApiAsync(
        AddonDefinition addon,
        string owner,
        string repository,
        string cacheKey,
        GitHubSourceCacheEntry? cached)
    {
        var head = await GetGitHeadAsync(owner, repository, addon.Ref);

        if (cached is not null &&
            cached.Commit.Equals(head.Commit, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(cached.Folder) &&
            !string.IsNullOrWhiteSpace(cached.Version))
        {
            return BuildResolvedDefinition(
                addon,
                repository,
                head.Branch,
                head.Commit,
                cached.AddonPath,
                cached.Folder,
                cached.Version);
        }

        var archiveUrl = BuildCommitArchiveUrl(addon.GitUrl, head.Commit);

        using var response = await HttpClient.GetAsync(archiveUrl);
        response.EnsureSuccessStatusCode();

        await using var archiveStream =
            await response.Content.ReadAsStreamAsync();
        using var archive = new ZipArchive(
            archiveStream,
            ZipArchiveMode.Read,
            leaveOpen: false);

        var entries = archive.Entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Name))
            .Select(entry => new
            {
                Entry = entry,
                RelativePath = StripArchiveRoot(entry.FullName)
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.RelativePath))
            .ToArray();

        var tocPath = SelectTocPath(
            entries.Select(item => item.RelativePath),
            repository,
            addon.AddonPath);

        var tocEntry = entries.FirstOrDefault(item =>
            item.RelativePath.Equals(
                tocPath,
                StringComparison.OrdinalIgnoreCase))?.Entry;

        if (tocEntry is null)
        {
            throw new InvalidDataException(
                $"Portalkeeper found '{tocPath}' in {addon.Name}, but could not read it from the repository archive.");
        }

        string tocText;
        await using (var tocStream = tocEntry.Open())
        using (var reader = new StreamReader(
                   tocStream,
                   Encoding.UTF8,
                   detectEncodingFromByteOrderMarks: true))
        {
            tocText = await reader.ReadToEndAsync();
        }

        var version = ReadTocVersion(tocText);

        if (string.IsNullOrWhiteSpace(version))
        {
            throw new InvalidDataException(
                $"Portalkeeper found '{tocPath}' in {addon.Name}, but it does not contain '## Version:'.");
        }

        return CacheAndBuildResolvedDefinition(
            addon,
            repository,
            cacheKey,
            head.Branch,
            head.Commit,
            tocPath,
            version);
    }

    private AddonDefinition CacheAndBuildResolvedDefinition(
        AddonDefinition addon,
        string repository,
        string cacheKey,
        string branch,
        string commit,
        string tocPath,
        string version)
    {
        var addonPath = NormalizeAddonPath(
            string.IsNullOrWhiteSpace(addon.AddonPath)
                ? GetDirectoryPart(tocPath)
                : addon.AddonPath);

        var folder = !string.IsNullOrWhiteSpace(addon.Folder)
            ? addon.Folder
            : InferFolder(repository, tocPath, addonPath);

        _cache.Repositories[cacheKey] = new GitHubSourceCacheEntry
        {
            DefaultBranch = branch,
            Commit = commit,
            AddonPath = addonPath,
            Folder = folder,
            Version = version
        };

        SaveCache();

        return BuildResolvedDefinition(
            addon,
            repository,
            branch,
            commit,
            addonPath,
            folder,
            version);
    }

    private static string StripArchiveRoot(string fullName)
    {
        var normalized = fullName.Replace('\\', '/').Trim('/');
        var slash = normalized.IndexOf('/');

        return slash < 0
            ? string.Empty
            : normalized[(slash + 1)..];
    }

    private async Task<GitHeadInfo> GetGitHeadAsync(
        string owner,
        string repository, string reference)
    {
        var url =
            $"https://github.com/{owner}/{repository}.git/info/refs?service=git-upload-pack";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue(
                "application/x-git-upload-pack-advertisement"));

        using var response = await HttpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var bytes = await response.Content.ReadAsByteArrayAsync();
        var advertisement = Encoding.UTF8.GetString(bytes);

        if (!string.IsNullOrWhiteSpace(reference))
        {
            var normalized = reference.StartsWith("refs/", StringComparison.Ordinal) ? reference : "refs/heads/" + reference;
            var tag = reference.StartsWith("refs/", StringComparison.Ordinal) ? reference : "refs/tags/" + reference;
            foreach (var candidate in new[] { tag + "^{}", normalized, tag })
            {
                var found = Regex.Match(advertisement, @"(?i)([0-9a-f]{40}) " + Regex.Escape(candidate) + @"(?=[\0\s])");
                if (found.Success) return new GitHeadInfo(reference, found.Groups[1].Value);
            }
            throw new InvalidDataException("Configured GitHub Ref was not advertised; refusing to use a different branch.");
        }
        var commitMatch = Regex.Match(
            advertisement,
            @"(?i)([0-9a-f]{40}) HEAD");
        var branchMatch = Regex.Match(
            advertisement,
            @"symref=HEAD:refs/heads/([^\0\s]+)");

        if (!commitMatch.Success)
        {
            throw new InvalidDataException(
                $"GitHub did not advertise a HEAD commit for https://github.com/{owner}/{repository}.");
        }

        var branch = branchMatch.Success
            ? branchMatch.Groups[1].Value
            : "HEAD";

        return new GitHeadInfo(
            branch,
            commitMatch.Groups[1].Value);
    }

    public static bool TryParseRepositoryUrl(
        string gitUrl,
        out string owner,
        out string repository)
    {
        owner = string.Empty;
        repository = string.Empty;

        if (!Uri.TryCreate(gitUrl, UriKind.Absolute, out var uri) ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            (uri.Scheme != "https" && uri.Scheme != "http") || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
        {
            return false;
        }

        var parts = uri.AbsolutePath
            .Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length != 2 || parts.Any(p => !Regex.IsMatch(p, @"^[A-Za-z0-9_.-]+$") || p is "." or ".."))
            return false;

        owner = parts[0];
        repository = parts[1];

        if (repository.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            repository = repository[..^4];

        return !string.IsNullOrWhiteSpace(owner) &&
               !string.IsNullOrWhiteSpace(repository);
    }

    public static string BuildCommitArchiveUrl(
        string gitUrl,
        string commit)
    {
        if (!TryParseRepositoryUrl(gitUrl, out var owner, out var repository))
        {
            throw new InvalidDataException(
                $"Unsupported GitHub repository URL: {gitUrl}");
        }

        return $"https://codeload.github.com/{owner}/{repository}/zip/{commit}";
    }

    private static AddonDefinition BuildResolvedDefinition(
        AddonDefinition source,
        string repository,
        string branch,
        string commit,
        string addonPath,
        string folder,
        string version, string sourceWarning = "")
    {
        return new AddonDefinition
        {
            Id = string.IsNullOrWhiteSpace(source.Id)
                ? repository.ToLowerInvariant()
                : source.Id,
            Name = string.IsNullOrWhiteSpace(source.Name)
                ? repository
                : source.Name,
            Folder = string.IsNullOrWhiteSpace(source.Folder) ? folder : source.Folder,
            Version = version,
            Ref = source.Ref,
            SourceWarning = sourceWarning,
            Required = source.Required,
            Recommended = source.Recommended,
            GitUrl = source.GitUrl,
            AddonPath = addonPath,
            DownloadUrl = source.DownloadUrl,
            Sha256 = source.Sha256,
            SourceCommit = commit,
            SourceBranch = branch,
            IsPersonal = source.IsPersonal
        };
    }

    private static string SelectTocPath(
        IEnumerable<string> paths,
        string repository,
        string addonPathOverride)
    {
        var tocFiles = paths
            .Where(path =>
                path.EndsWith(".toc", StringComparison.OrdinalIgnoreCase))
            .Select(path => path.Replace('\\', '/'))
            .ToArray();

        if (!string.IsNullOrWhiteSpace(addonPathOverride))
        {
            var prefix = NormalizeAddonPath(addonPathOverride);

            tocFiles = tocFiles
                .Where(path => IsUnderPath(path, prefix))
                .ToArray();
        }

        if (tocFiles.Length == 0)
        {
            throw new InvalidDataException(
                "No World of Warcraft .toc file was found in the GitHub repository.");
        }

        // Special handling for multi-addon repositories - find a valid metadata TOC when needed
        // but allow multiple addon directories to proceed
        if (tocFiles.Length > 1)
        {
            // Embedded libraries frequently ship their own .toc files. If the repository
            // also contains normal addon candidates, do not let those library metadata
            // files make discovery look ambiguous.
            var nonLibraryTocs = tocFiles
                .Where(path => !IsEmbeddedLibraryToc(path))
                .ToArray();

            if (nonLibraryTocs.Length > 0)
                tocFiles = nonLibraryTocs;

            // If we have multiple valid addon directories and the addonPathOverride is set, 
            // try to determine an appropriate metadata TOC
            if (!string.IsNullOrWhiteSpace(addonPathOverride))
            {
                var addonPathNormalized = NormalizeAddonPath(addonPathOverride);
                
                // Prefer a TOC that's inside the specified folder path if available
                foreach (var toc in tocFiles)
                {
                    if (IsUnderPath(toc, addonPathNormalized) &&
                        !string.IsNullOrWhiteSpace(GetDirectoryPart(toc)))
                    {
                        return toc;
                    }
                }
            }
            
            // If there are still multiple TOCs remaining, we must select one for version 
            // metadata while letting all be installed by installer.
            // Select based on: 1) repository root level TOC, 2) name match with repository
        }

        if (tocFiles.Length == 1)
            return tocFiles[0];

        // A single root-level .toc is the strongest signal for repositories whose
        // addon itself lives at repository root (for example MultiBot-Chatless).
        var rootTocs = tocFiles
            .Where(path => !path.Contains('/'))
            .ToArray();

        if (rootTocs.Length == 1)
            return rootTocs[0];

        var repositoryTocName = repository + ".toc";
        var exactNameMatches = tocFiles
            .Where(path =>
                Path.GetFileName(path).Equals(
                    repositoryTocName,
                    StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (exactNameMatches.Length == 1)
            return exactNameMatches[0];

        var folderMatches = tocFiles
            .Where(path =>
                GetLastDirectoryName(path).Equals(
                    repository,
                    StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (folderMatches.Length == 1)
            return folderMatches[0];

        // If we have multiple TOCs remaining after all attempts, try to pick one
        // For multi-addon repositories, we want to keep the behavior consistent with 
        // how installation works - but we still need a valid metadata TOC for version display.
        var candidateTocs = tocFiles;
        
        // Prefer root level .toc files first
        var rootLevelToc = candidateTocs.FirstOrDefault(path => !path.Contains('/'));
        if (rootLevelToc != null)
            return rootLevelToc;

        // Then prefer the repository name matched TOC
        var repoNameMatch = candidateTocs.FirstOrDefault(path => 
            Path.GetFileName(path).Equals(repositoryTocName, StringComparison.OrdinalIgnoreCase));
        if (repoNameMatch != null)
            return repoNameMatch;

        // If nothing else works, pick the first one to prevent a breaking change
        return candidateTocs[0];
    }

    private static bool IsEmbeddedLibraryToc(string path)
    {
        var segments = path
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        // Only treat directory segments as library markers. A root addon whose name
        // happens to contain one of these words should still be considered normally.
        for (var index = 0; index < segments.Length - 1; index++)
        {
            var segment = segments[index];

            if (segment.Equals("Lib", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("Libs", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("Library", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("Libraries", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("Vendor", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("Vendors", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("ThirdParty", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("Third-Party", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string InferFolder(
        string repository,
        string tocPath,
        string addonPath)
    {
        if (!string.IsNullOrWhiteSpace(addonPath))
        {
            var normalized = addonPath.Trim('/');
            var lastSlash = normalized.LastIndexOf('/');
            return lastSlash >= 0
                ? normalized[(lastSlash + 1)..]
                : normalized;
        }

        var tocName = Path.GetFileNameWithoutExtension(tocPath);

        return !string.IsNullOrWhiteSpace(tocName)
            ? tocName
            : repository;
    }

    private static string ReadTocVersion(string tocText)
    {
        using var reader = new StringReader(tocText);

        while (reader.ReadLine() is { } rawLine)
        {
            var line = rawLine.Trim();
            if (!line.StartsWith(
                    "## Version:",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return line["## Version:".Length..].Trim();
        }

        return string.Empty;
    }

    private async Task<T> GetJsonAsync<T>(string url)
    {
        using var response = await HttpClient.GetAsync(url);

        if (response.StatusCode == HttpStatusCode.Forbidden &&
            response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) &&
            remaining.FirstOrDefault() == "0")
        {
            throw new GitHubApiRateLimitException();
        }

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync();
        var value = await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions);

        return value ?? throw new InvalidDataException(
            $"GitHub returned an empty response for {url}.");
    }

    private GitHubSourceCache LoadCache()
    {
        try
        {
            if (!File.Exists(_cachePath))
                return new GitHubSourceCache();

            var json = File.ReadAllText(_cachePath);
            return JsonSerializer.Deserialize<GitHubSourceCache>(json, JsonOptions)
                   ?? new GitHubSourceCache();
        }
        catch
        {
            return new GitHubSourceCache();
        }
    }

    private void SaveCache()
    {
        try
        {
            File.WriteAllText(
                _cachePath,
                JsonSerializer.Serialize(_cache, JsonOptions));
        }
        catch
        {
            // Cache failure should not prevent addon discovery.
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("Portalkeeper", RealmConfigurationService.CurrentVersion));
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add(
            "X-GitHub-Api-Version",
            "2022-11-28");
        return client;
    }

    private static string NormalizeAddonPath(string path) =>
        path.Replace('\\', '/').Trim('/');

    private static bool IsUnderPath(string path, string prefix)
    {
        var normalizedPath = path.Replace('\\', '/').Trim('/');
        var normalizedPrefix = prefix.Replace('\\', '/').Trim('/');

        return normalizedPath.Equals(
                   normalizedPrefix,
                   StringComparison.OrdinalIgnoreCase) ||
               normalizedPath.StartsWith(
                   normalizedPrefix + "/",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string GetDirectoryPart(string path)
    {
        var normalized = path.Replace('\\', '/');
        var slash = normalized.LastIndexOf('/');
        return slash < 0 ? string.Empty : normalized[..slash];
    }

    private static string GetLastDirectoryName(string path)
    {
        var directory = GetDirectoryPart(path).Trim('/');
        if (string.IsNullOrWhiteSpace(directory))
            return string.Empty;

        var slash = directory.LastIndexOf('/');
        return slash < 0 ? directory : directory[(slash + 1)..];
    }

    private static string EscapePath(string path) =>
        string.Join(
            "/",
            path.Split('/')
                .Select(Uri.EscapeDataString));

    private sealed class RepositoryResponse
    {
        [JsonPropertyName("default_branch")]
        public string DefaultBranch { get; init; } = string.Empty;
    }

    private sealed class CommitResponse
    {
        [JsonPropertyName("sha")]
        public string Sha { get; init; } = string.Empty;

        [JsonPropertyName("commit")]
        public CommitDetailsResponse Commit { get; init; } = new();
    }

    private sealed class CommitDetailsResponse
    {
        [JsonPropertyName("tree")]
        public CommitTreeResponse Tree { get; init; } = new();
    }

    private sealed class CommitTreeResponse
    {
        [JsonPropertyName("sha")]
        public string Sha { get; init; } = string.Empty;
    }

    private sealed class TreeResponse
    {
        [JsonPropertyName("truncated")]
        public bool Truncated { get; init; }

        [JsonPropertyName("tree")]
        public List<TreeItemResponse> Tree { get; init; } = new();
    }

    private sealed class TreeItemResponse
    {
        [JsonPropertyName("path")]
        public string Path { get; init; } = string.Empty;

        [JsonPropertyName("type")]
        public string Type { get; init; } = string.Empty;
    }

    private sealed record GitHeadInfo(
        string Branch,
        string Commit);

    private sealed class GitHubApiRateLimitException : Exception
    {
    }

    private sealed class GitHubSourceCache
    {
        public Dictionary<string, GitHubSourceCacheEntry> Repositories { get; init; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class GitHubSourceCacheEntry
    {
        public string DefaultBranch { get; init; } = string.Empty;
        public string Commit { get; init; } = string.Empty;
        public string AddonPath { get; init; } = string.Empty;
        public string Folder { get; init; } = string.Empty;
        public string Version { get; init; } = string.Empty;
    }
}
