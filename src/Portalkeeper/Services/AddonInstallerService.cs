using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Collections.Generic;
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

            var sourceDirectories = FindAllAddonDirectories(extractDirectory);
            
            if (sourceDirectories is null || sourceDirectories.Count == 0)
            {
                throw new InvalidDataException(
                    $"The archive does not contain any valid addon directories.");
            }

            // Validate that at least one directory has a .toc file directly in it
            var tocFiles = sourceDirectories.SelectMany(dir => Directory.GetFiles(dir, "*.toc", SearchOption.TopDirectoryOnly)).ToArray();

            if (tocFiles.Length == 0)
            {
                throw new InvalidDataException(
                    $"The archive does not contain any valid addon directories.");
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

            var preparedDirectories = new List<string>();
            foreach (var sourceDir in sourceDirectories)
            {
                var preparedDirectory = Path.Combine(
                    workDirectory,
                    "prepared",
                    Path.GetFileName(sourceDir));

                CopyDirectory(sourceDir, preparedDirectory);
                preparedDirectories.Add(preparedDirectory);
            }

            var destinationDirectories = new List<string>();
            foreach (var sourceDir in sourceDirectories)
            {
                var destinationDirectory = ManagedPath.Resolve(clientDirectory, Path.Combine("Interface", "AddOns", Path.GetFileName(sourceDir)));
                destinationDirectories.Add(destinationDirectory);
            }

            var backupDirectories = new List<string>();
            var hadExistingInstall = false;
            foreach (var destDir in destinationDirectories)
            {
                if (Directory.Exists(destDir))
                {
                    hadExistingInstall = true;
                    break;
                }
            }

            var backupCreated = false;

            try
            {
                if (hadExistingInstall)
                {
                    for (int i = 0; i < destinationDirectories.Count; i++)
                    {
                        var destDir = destinationDirectories[i];
                        if (Directory.Exists(destDir))
                        {
                            var backupDirectory = CreateBackupPath(
                                clientDirectory,
                                addon,
                                Path.GetFileName(destDir));

                            Directory.CreateDirectory(
                                Path.GetDirectoryName(backupDirectory)!);

                            Directory.Move(
                                destDir,
                                backupDirectory);

                            backupDirectories.Add(backupDirectory);
                        }
                    }

                    backupCreated = true;
                }

                // preparedDirectory lives under the system temp directory, which may
                // be on a different filesystem from the WoW client. Directory.Move()
                // cannot cross filesystem boundaries on Unix, so copy the prepared
                // addon into place instead. The existing install has already been
                // moved to a backup on the client filesystem, so rollback remains safe.
                
                for (int i = 0; i < preparedDirectories.Count; i++)
                {
                    var preparedDir = preparedDirectories[i];
                    var destDir = destinationDirectories[i];
                    
                    CopyDirectory(
                        preparedDir,
                        destDir);
                }

                // Save installation state with all installed folder names (all addon components found)
                var allInstalledFolders = sourceDirectories.Select(d => Path.GetFileName(d)).ToList();
                _installStateService.Save(
                    clientDirectory,
                    addon.Id,
                    string.IsNullOrWhiteSpace(archiveVersion)
                        ? addon.Version
                        : archiveVersion,
                    addon.SourceCommit,
                    allInstalledFolders);
            }
            catch
            {
                // Clean up any installed directories if there's a partial install
                for (int i = 0; i < destinationDirectories.Count; i++)
                {
                    var destDir = destinationDirectories[i];
                    if (Directory.Exists(destDir))
                        Directory.Delete(destDir, true);
                }

                if (backupCreated)
                {
                    // Restore all backed up directories
                    foreach (var backupDir in backupDirectories)
                    {
                        if (Directory.Exists(backupDir))
                        {
                            var restoreDest = backupDir.Substring(0, backupDir.LastIndexOf(Path.DirectorySeparatorChar));
                            Directory.CreateDirectory(Path.GetDirectoryName(restoreDest)!);
                            Directory.Move(backupDir, restoreDest);
                        }
                    }
                }

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
        
        // Load install state to see all installed folders for this addon
        var state = _installStateService.Load(clientDirectory, addon.Id);
        
        // If we have InstalledFolders saved (new style), remove all of them
        if (state.InstalledFolders != null && state.InstalledFolders.Count > 0)
        {
            foreach (var folderName in state.InstalledFolders)
            {
                var destination = ManagedPath.Resolve(clientDirectory, Path.Combine("Interface", "AddOns", folderName));
                if (!Directory.Exists(destination)) continue;
                
                var backup = ManagedPath.Resolve(clientDirectory, Path.Combine(".portalkeeper", "backups", addon.Id, Guid.NewGuid().ToString("N"), folderName));
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                Directory.Move(destination, backup);
            }
        }
        else
        {
            // Fallback to original single-folder behavior for backward compatibility
            var destination = ManagedPath.Resolve(clientDirectory, Path.Combine("Interface", "AddOns", addon.Folder));
            if (!Directory.Exists(destination)) return;
            var backup = ManagedPath.Resolve(clientDirectory, Path.Combine(".portalkeeper", "backups", addon.Id, Guid.NewGuid().ToString("N"), addon.Folder));
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            Directory.Move(destination, backup);
        }
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

    private static List<string> FindAllAddonDirectories(
        string extractDirectory)
    {
        var addonDirectories = new List<string>();
        
        // Look at the root of the extracted directory and find all directories containing .toc files
        var topLevelDirs = Directory.GetDirectories(extractDirectory);
        
        foreach (var dir in topLevelDirs)
        {
            // For GitHub archives, check if this directory is a wrapper directory that contains 
            // another level with addon directories. The GitHub codeload ZIP extracts with:
            //    extracted/
            //        RepoName-<commit>/
            //            Addon1/
            //                Addon1.toc
            //            Addon2/  
            //                Addon2.toc
            // We want to check if the immediate children contain addon directories.
            var isGitHubArchive = topLevelDirs.Length == 1 && 
                                  topLevelDirs[0].Contains("-") && // Simple heuristic for GitHub archive format
                                  !Path.GetFileName(topLevelDirs[0]).Contains("."); // Not a directory starting with dot (e.g., ".git")
            
            List<string> candidateDirectories;
            
            if (isGitHubArchive)
            {
                // Check the subdirectories of the single wrapper directory  
                var subDir = topLevelDirs[0];
                var subDirectories = Directory.GetDirectories(subDir);
                candidateDirectories = subDirectories.ToList();
            }
            else
            {
                candidateDirectories = new List<string> { dir };
            }
            
            foreach (var candidateDir in candidateDirectories)
            {
                // Check if this directory contains at least one .toc file directly in it (not recursively)
                var tocFiles = Directory.GetFiles(candidateDir, "*.toc", SearchOption.TopDirectoryOnly);
                
                if (tocFiles.Length > 0)
                {
                    addonDirectories.Add(candidateDir);
                }
            }
        }
        
        return addonDirectories;
    }

    private static List<string> FindAddonDirectories(
        string extractDirectory,
        string expectedFolder)
    {
        var direct = Path.Combine(extractDirectory, expectedFolder);
        if (Directory.Exists(direct))
            return new List<string> { direct };

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

        if (matches.Length == 1)
            return new List<string> { matches[0] };
            
        return new List<string>();
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
        AddonDefinition addon,
        string directoryName)
    {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff");

        return ManagedPath.Resolve(clientDirectory, Path.Combine(".portalkeeper", "backups", addon.Id, timestamp + "-" + Guid.NewGuid().ToString("N"), directoryName));
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
