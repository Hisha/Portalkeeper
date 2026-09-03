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

    public async Task InstallOrUpdateAsync(
        string clientDirectory,
        AddonDefinition addon)
    {
        ValidateDefinition(addon);

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
            await DownloadAsync(addon.DownloadUrl, archivePath);
            VerifySha256(archivePath, addon.Sha256);

            var extractDirectory = Path.Combine(workDirectory, "extracted");
            Directory.CreateDirectory(extractDirectory);
            ExtractZipSafely(archivePath, extractDirectory);

            var sourceDirectory = FindAddonDirectory(
                extractDirectory,
                addon.Folder);

            if (sourceDirectory is null)
            {
                throw new InvalidDataException(
                    $"The archive does not contain the expected addon folder '{addon.Folder}'.");
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

            var preparedDirectory = Path.Combine(
                workDirectory,
                "prepared",
                addon.Folder);

            CopyDirectory(sourceDirectory, preparedDirectory);

            var destinationDirectory = Path.Combine(
                addonsDirectory,
                addon.Folder);

            var backupDirectory = CreateBackupPath(
                clientDirectory,
                addon);

            var hadExistingInstall = Directory.Exists(destinationDirectory);
            var backupCreated = false;

            try
            {
                if (hadExistingInstall)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(backupDirectory)!);
                    Directory.Move(destinationDirectory, backupDirectory);
                    backupCreated = true;
                }

                Directory.Move(preparedDirectory, destinationDirectory);
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

    private static void ValidateDefinition(AddonDefinition addon)
    {
        if (string.IsNullOrWhiteSpace(addon.Id) ||
            string.IsNullOrWhiteSpace(addon.Folder))
        {
            throw new InvalidDataException(
                "Addon manifest entry is missing an id or folder name.");
        }

        if (string.IsNullOrWhiteSpace(addon.DownloadUrl))
        {
            throw new InvalidDataException(
                $"No download URL is configured for {addon.Name}.");
        }

        if (string.IsNullOrWhiteSpace(addon.Sha256) ||
            addon.Sha256.Length != 64 ||
            !addon.Sha256.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException(
                $"A valid SHA-256 hash is required for {addon.Name}.");
        }

        if (addon.Folder.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            addon.Folder.Contains(Path.DirectorySeparatorChar) ||
            addon.Folder.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new InvalidDataException(
                $"Invalid addon folder name: {addon.Folder}");
        }
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
            throw new FileNotFoundException("Addon archive was not found.", sourcePath);

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

        foreach (var entry in archive.Entries)
        {
            var destinationPath = Path.GetFullPath(
                Path.Combine(destinationDirectory, entry.FullName));

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

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            entry.ExtractToFile(destinationPath, true);
        }
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

    private static string CreateBackupPath(
        string clientDirectory,
        AddonDefinition addon)
    {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff");

        return Path.Combine(
            clientDirectory,
            ".portalkeeper",
            "backups",
            addon.Id,
            timestamp,
            addon.Folder);
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
                Path.Combine(destinationDirectory, Path.GetFileName(file)),
                true);
        }

        foreach (var directory in Directory.GetDirectories(sourceDirectory))
        {
            CopyDirectory(
                directory,
                Path.Combine(destinationDirectory, Path.GetFileName(directory)));
        }
    }
}
