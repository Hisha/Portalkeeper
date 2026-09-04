using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Portalkeeper.Models;

namespace Portalkeeper.Services;

public sealed class GitHubAddonSourceService
{
    private static readonly HttpClient HttpClient = CreateHttpClient();

    private static readonly JsonSerializerOptions JsonOptions =
        new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };

    private readonly string _cachePath;
    private GitHubSourceCache _cache;

    public GitHubAddonSourceService()
    {
        var applicationData =
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        var portalkeeperDirectory =
            Path.Combine(applicationData, "Portalkeeper");

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

        var cacheKey = $"{owner}/{repository}";

        _cache.Repositories.TryGetValue(
            cacheKey,
            out var cached);

        var defaultBranch = cached?.DefaultBranch ?? string.Empty;

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
        catch (HttpRequestException) when (cached is not null)
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
            tree.Tree,
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

        var addonPath = NormalizeAddonPath(
            string.IsNullOrWhiteSpace(addon.AddonPath)
                ? GetDirectoryPart(tocPath)
                : addon.AddonPath);

        var folder = !string.IsNullOrWhiteSpace(addon.Folder)
            ? addon.Folder
            : InferFolder(repository, tocPath, addonPath);

        _cache.Repositories[cacheKey] = new GitHubSourceCacheEntry
        {
            DefaultBranch = defaultBranch,
            Commit = commit.Sha,
            AddonPath = addonPath,
            Folder = folder,
            Version = version
        };

        SaveCache();

        return BuildResolvedDefinition(
            addon,
            repository,
            defaultBranch,
            commit.Sha,
            addonPath,
            folder,
            version);
    }

    public static bool TryParseRepositoryUrl(
        string gitUrl,
        out string owner,
        out string repository)
    {
        owner = string.Empty;
        repository = string.Empty;

        if (!Uri.TryCreate(gitUrl, UriKind.Absolute, out var uri) ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var parts = uri.AbsolutePath
            .Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length < 2)
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
        string version)
    {
        return new AddonDefinition
        {
            Id = string.IsNullOrWhiteSpace(source.Id)
                ? repository.ToLowerInvariant()
                : source.Id,
            Name = string.IsNullOrWhiteSpace(source.Name)
                ? repository
                : source.Name,
            Folder = folder,
            Version = version,
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
        IReadOnlyList<TreeItemResponse> tree,
        string repository,
        string addonPathOverride)
    {
        var tocFiles = tree
            .Where(item =>
                item.Type.Equals("blob", StringComparison.OrdinalIgnoreCase) &&
                item.Path.EndsWith(".toc", StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Path.Replace('\\', '/'))
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

        if (tocFiles.Length == 1)
            return tocFiles[0];

        // Embedded libraries frequently ship their own .toc files. If the repository
        // also contains normal addon candidates, do not let those library metadata
        // files make discovery look ambiguous.
        var nonLibraryTocs = tocFiles
            .Where(path => !IsEmbeddedLibraryToc(path))
            .ToArray();

        if (nonLibraryTocs.Length > 0)
            tocFiles = nonLibraryTocs;

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

        var choices = string.Join(", ", tocFiles.Take(6));
        if (tocFiles.Length > 6)
            choices += ", ...";

        throw new InvalidDataException(
            $"Multiple addon .toc files were found ({choices}). " +
            "Set addonPath in the manifest to identify the addon directory.");
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
            throw new InvalidOperationException(
                "GitHub API rate limit reached. Try CHECK AGAIN later.");
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
            new ProductInfoHeaderValue("Portalkeeper", "0.1"));
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
