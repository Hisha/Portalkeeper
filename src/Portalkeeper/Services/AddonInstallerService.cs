using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Portalkeeper.Models;

namespace Portalkeeper.Services;

public sealed class AddonInstallerService
{
    private static readonly HttpClient HttpClient = new();
    private readonly AddonInstallStateService _installStateService = new();

    public async Task InstallOrUpdateAsync(
        string clientDirectory,
        AddonDefinition addon)
    {
        ValidateDefinition(addon);
        _ = ManagedPath.Resolve(clientDirectory, Path.Combine("Interface", "AddOns", addon.Folder));
        ManagedPath.Resolve(clientDirectory, ".portalkeeper/backups");

        var addonsDirectory = Path.Combine(
            clientDirectory,
            "Interface",
            "AddOns");

        Directory.CreateDirectory(addonsDirectory);

        var workDirectory = Path.Combine(
            Path.GetTempPath(),
            "Portalkeeper",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(workDirectory);

        try
        {
            var archivePath = Path.Combine(workDirectory, "addon.zip");

            if (addon.IsGitHubSource)
            {
                var archiveUrl = GitHubAddonSourceService.BuildCommitArchiveUrl(
                    addon.GitUrl,
                    addon.SourceCommit);

                await DownloadAsync(archiveUrl, archivePath);
            }
            else
            {
                await DownloadAsync(addon.DownloadUrl, archivePath);
                if (addon.Sha256.Length > 0) VerifySha256(archivePath, addon.Sha256);
            }

            var extractDirectory = Path.Combine(workDirectory, "extracted");
            Directory.CreateDirectory(extractDirectory);
            ExtractZipSafely(archivePath, extractDirectory);

            var sourceDirectory = addon.IsGitHubSource
                ? FindGitHubAddonDirectory(extractDirectory, addon)
                : FindAddonDirectory(extractDirectory, addon.Folder);

            if (sourceDirectory is null)
            {
                throw new InvalidDataException(
                    $"The archive does not contain the expected addon '{addon.Folder}'.");
            }

            var tocFiles = Directory.GetFiles(
                sourceDirectory,
                "*.toc",
                SearchOption.TopDirectoryOnly);

            if (tocFiles.Length == 0)
            {
                throw new InvalidDataException(
                    $"The expected addon folder '{addon.Folder}' does not contain a .toc file.");
            }

            var archiveVersion = TryReadTocVersion(tocFiles);
            if (!string.IsNullOrWhiteSpace(addon.Version) &&
                !string.IsNullOrWhiteSpace(archiveVersion) &&
                !archiveVersion.Equals(
                    addon.Version,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Downloaded addon version '{archiveVersion}' does not match the discovered version '{addon.Version}'.");
            }

            var preparedDirectory = Path.Combine(
                workDirectory,
                "prepared",
                addon.Folder);

            CopyDirectory(sourceDirectory, preparedDirectory);

            var destinationDirectory = ManagedPath.Resolve(clientDirectory, Path.Combine("Interface", "AddOns", addon.Folder));

            var backupDirectory = CreateBackupPath(
                clientDirectory,
                addon);

            var hadExistingInstall = Directory.Exists(destinationDirectory);
            var backupCreated = false;

            try
            {
                if (hadExistingInstall)
                {
                    Directory.CreateDirectory(
                        Path.GetDirectoryName(backupDirectory)!);

                    Directory.Move(
                        destinationDirectory,
                        backupDirectory);

                    backupCreated = true;
                }

                // preparedDirectory lives under the system temp directory, which may
                // be on a different filesystem from the WoW client. Directory.Move()
                // cannot cross filesystem boundaries on Unix, so copy the prepared
                // addon into place instead. The existing install has already been
                // moved to a backup on the client filesystem, so rollback remains safe.
                CopyDirectory(
                    preparedDirectory,
                    destinationDirectory);

                _installStateService.Save(
                    clientDirectory,
                    addon.Id,
                    string.IsNullOrWhiteSpace(archiveVersion)
                        ? addon.Version
                        : archiveVersion,
                    addon.SourceCommit);
            }
            catch
            {
                if (Directory.Exists(destinationDirectory))
                    Directory.Delete(destinationDirectory, true);

                if (backupCreated && Directory.Exists(backupDirectory))
                    Directory.Move(backupDirectory, destinationDirectory);

                throw;
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(workDirectory))
                    Directory.Delete(workDirectory, true);
            }
            catch
            {
                // Temporary cleanup failure should not invalidate a successful install.
            }
        }
    }

    public void Remove(string clientDirectory, AddonDefinition addon)
    {
        ManagedPath.Relative(addon.Id, true);
        ManagedPath.Relative(addon.Folder, true);
        var destination = ManagedPath.Resolve(clientDirectory, Path.Combine("Interface", "AddOns", addon.Folder));
        if (!Directory.Exists(destination)) return;
        var backup = ManagedPath.Resolve(clientDirectory, Path.Combine(".portalkeeper", "backups", addon.Id, Guid.NewGuid().ToString("N"), addon.Folder));
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        Directory.Move(destination, backup);
    }
    private static void ValidateDefinition(AddonDefinition addon)
    {
        ManagedPath.Relative(addon.Id, true);
        ManagedPath.Relative(addon.Folder, true);
        if (string.IsNullOrWhiteSpace(addon.Id) ||
            string.IsNullOrWhiteSpace(addon.Folder))
        {
            throw new InvalidDataException(
                "Addon source is missing an id or discovered folder name.");
        }

        if (addon.Folder.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            addon.Folder.Contains(Path.DirectorySeparatorChar) ||
            addon.Folder.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new InvalidDataException(
                $"Invalid addon folder name: {addon.Folder}");
        }

        if (addon.IsGitHubSource)
        {
            if (string.IsNullOrWhiteSpace(addon.SourceCommit))
            {
                throw new InvalidDataException(
                    $"No GitHub source commit was resolved for {addon.Name}.");
            }

            if (!GitHubAddonSourceService.TryParseRepositoryUrl(
                    addon.GitUrl,
                    out _,
                    out _))
            {
                throw new InvalidDataException(
                    $"Unsupported GitHub repository URL for {addon.Name}.");
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(addon.DownloadUrl))
        {
            throw new InvalidDataException(
                $"No download URL is configured for {addon.Name}.");
        }

        ManagedPath.Url(addon.DownloadUrl);
        ManagedPath.Hash(addon.Sha256);

    }

    private static async Task DownloadAsync(
        string location,
        string destinationPath)
    {
        if (Uri.TryCreate(location, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp ||
             uri.Scheme == Uri.UriSchemeHttps))
        {
            using var response = await HttpClient.GetAsync(
                uri,
                HttpCompletionOption.ResponseHeadersRead);

            response.EnsureSuccessStatusCode();

            await using var source = await response.Content.ReadAsStreamAsync();
            await using var destination = File.Create(destinationPath);
            await source.CopyToAsync(destination);
            return;
        }

        var sourcePath = Path.GetFullPath(location);
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException(
                "Addon archive was not found.",
                sourcePath);
        }

        File.Copy(sourcePath, destinationPath, true);
    }

    private static void VerifySha256(
        string archivePath,
        string expectedHash)
    {
        using var stream = File.OpenRead(archivePath);
        var actualHash = Convert.ToHexString(
            SHA256.HashData(stream));

        if (!actualHash.Equals(
                expectedHash,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Downloaded addon failed SHA-256 verification.");
        }
    }

    private static void ExtractZipSafely(
        string archivePath,
        string destinationDirectory)
    {
        var destinationRoot = Path.GetFullPath(destinationDirectory) +
                              Path.DirectorySeparatorChar;

        using var archive = ZipFile.OpenRead(archivePath);

        if (archive.Entries.Count > 50000 || archive.Entries.Sum(e => e.Length) > 1024L * 1024 * 1024)
            throw new InvalidDataException("Addon archive exceeds safe extraction limits.");
        foreach (var entry in archive.Entries)
        {
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException("Addon archive contains a symbolic link.");
            var relative = ManagedPath.Relative(entry.FullName.Replace('\\', '/').TrimEnd('/'));
            var destinationPath = Path.GetFullPath(
                Path.Combine(destinationDirectory, relative));

            if (!destinationPath.StartsWith(
                    destinationRoot,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Addon archive contains an unsafe path.");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            Directory.CreateDirectory(
                Path.GetDirectoryName(destinationPath)!);

            entry.ExtractToFile(destinationPath, true);
        }
    }

    private static string? FindGitHubAddonDirectory(
        string extractDirectory,
        AddonDefinition addon)
    {
        var topLevelDirectories = Directory.GetDirectories(extractDirectory);
        if (topLevelDirectories.Length != 1)
            return null;

        var repositoryRoot = topLevelDirectories[0];

        var sourceDirectory = string.IsNullOrWhiteSpace(addon.AddonPath)
            ? repositoryRoot
            : Path.Combine(
                repositoryRoot,
                addon.AddonPath.Replace('/', Path.DirectorySeparatorChar));

        if (!Directory.Exists(sourceDirectory))
            return null;

        return Directory.GetFiles(
            sourceDirectory,
            "*.toc",
            SearchOption.TopDirectoryOnly).Length > 0
                ? sourceDirectory
                : null;
    }

    private static string? FindAddonDirectory(
        string extractDirectory,
        string expectedFolder)
    {
        var direct = Path.Combine(extractDirectory, expectedFolder);
        if (Directory.Exists(direct))
            return direct;

        var matches = Directory
            .EnumerateDirectories(
                extractDirectory,
                expectedFolder,
                SearchOption.AllDirectories)
            .Where(path => Directory.GetFiles(
                path,
                "*.toc",
                SearchOption.TopDirectoryOnly).Length > 0)
            .Take(2)
            .ToArray();

        return matches.Length == 1
            ? matches[0]
            : null;
    }

    private static string TryReadTocVersion(
        string[] tocFiles)
    {
        foreach (var tocPath in tocFiles)
        {
            foreach (var rawLine in File.ReadLines(tocPath))
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
        }

        return string.Empty;
    }

    private static string CreateBackupPath(
        string clientDirectory,
        AddonDefinition addon)
    {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff");

        return ManagedPath.Resolve(clientDirectory, Path.Combine(".portalkeeper", "backups", addon.Id, timestamp + "-" + Guid.NewGuid().ToString("N"), addon.Folder));
    }

    private static void CopyDirectory(
        string sourceDirectory,
        string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);

        foreach (var file in Directory.GetFiles(sourceDirectory))
        {
            File.Copy(
                file,
                Path.Combine(
                    destinationDirectory,
                    Path.GetFileName(file)),
                true);
        }

        foreach (var directory in Directory.GetDirectories(sourceDirectory))
        {
            CopyDirectory(
                directory,
                Path.Combine(
                    destinationDirectory,
                    Path.GetFileName(directory)));
        }
    }
}
