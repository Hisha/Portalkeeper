using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Portalkeeper.Models;

namespace Portalkeeper.Services;

public sealed class RealmConfigurationUpdateService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };

    public async Task<RealmConfigurationUpdateResult> CheckForUpdateAsync(
        string localConfigPath,
        string updateUrl)
    {
        // If UpdateUrl is empty or whitespace, treat remote-update checking as unavailable 
        if (string.IsNullOrWhiteSpace(updateUrl))
        {
            return new RealmConfigurationUpdateResult(UpdateCheckStatus.NoUpdateUrl, null, null, null);
        }

        try
        {
            // Retrieve the remote realm configuration as raw bytes
            var remoteBytes = await Http.GetByteArrayAsync(updateUrl);
            
            // Calculate SHA-256 for the local realm configuration file bytes
            var localBytes = await File.ReadAllBytesAsync(localConfigPath);

            var localHash = Convert.ToHexString(SHA256.HashData(localBytes));
            var remoteHash = Convert.ToHexString(SHA256.HashData(remoteBytes));

            // Determine whether the hashes differ
            bool isUpdatedAvailable = !localHash.Equals(remoteHash, StringComparison.Ordinal);

            return new RealmConfigurationUpdateResult(
                isUpdatedAvailable ? UpdateCheckStatus.UpdateAvailable : UpdateCheckStatus.NoUpdateAvailable,
                localHash,
                remoteHash,
                remoteBytes);
        }
        catch (Exception)
        {
            // Network/download failures must not damage or modify the existing local realm configuration
            return new RealmConfigurationUpdateResult(UpdateCheckStatus.DownloadFailure, null, null, null);
        }
    }

    public async Task<RealmConfigurationUpdateResult> ApplyUpdateAsync(
        string localConfigPath,
        byte[] remoteBytes)
    {
        // Validate the downloaded configuration using the same parsing/validation rules 
        // that Portalkeeper uses for normal *.realm.conf files
        var tempConfigPath = localConfigPath + ".tmp";
        
        try
        {
            // Write the remote bytes to a temporary file to validate it first
            await File.WriteAllBytesAsync(tempConfigPath, remoteBytes);
            
            // Validate the configuration using existing realm config service
            var realmService = new RealmConfigurationService();
            _ = realmService.Load(tempConfigPath);  // Use discard operator for unused variable

            // If we get here without exception, the configuration is valid
            // Now proceed with replacement
            
            // Create backup directory if needed
            var backupDirectory = Path.Combine(
                Path.GetDirectoryName(localConfigPath)!,
                ".portalkeeper",
                "backups");
            
            Directory.CreateDirectory(backupDirectory);
            
            // Use deterministic backup path (one backup per realm config)
            var backupPath = Path.Combine(
                backupDirectory,
                $"{Path.GetFileName(localConfigPath)}.bak");
            
            // Create backup of existing configuration, replacing any previous backup
            if (File.Exists(localConfigPath))
            {
                File.Copy(localConfigPath, backupPath, true);
            }
            
            // Write new config to temporary file first
            var tempPath = localConfigPath + ".tmp.new";
            await File.WriteAllBytesAsync(tempPath, remoteBytes);
            
            // Atomic replacement - we use File.Replace which is cross-platform safe for most cases
            // but we also make it safer by not deleting the original file first
            if (File.Exists(localConfigPath))
            {
                File.Replace(tempPath, localConfigPath, null); // This replaces target with source without backup
            }
            else
            {
                File.Move(tempPath, localConfigPath);
            }
            
            return new RealmConfigurationUpdateResult(
                UpdateCheckStatus.UpdateApplied,
                null,
                null,
                null);
        }
        catch (InvalidDataException)
        {
            // Validation failed - realm config has invalid format/content 
            return new RealmConfigurationUpdateResult(
                UpdateCheckStatus.ValidationFailure, 
                null, 
                null, 
                null);
        }
        catch (Exception)
        {
            // Filesystem/backup/replacement operation failed
            return new RealmConfigurationUpdateResult(
                UpdateCheckStatus.ReplacementFailure, 
                null, 
                null, 
                null);
        }
        finally
        {
            // Clean up temporary files if they exist
            if (File.Exists(tempConfigPath))
            {
                File.Delete(tempConfigPath);
            }
            
            // Cleanup any temp file that might have been created during processing but not moved
            var tempNewPath = localConfigPath + ".tmp.new";
            if (File.Exists(tempNewPath))
            {
                File.Delete(tempNewPath);
            }
        }
    }
}

public sealed record RealmConfigurationUpdateResult(
    UpdateCheckStatus Status,
    string? LocalHash,
    string? RemoteHash,
    byte[]? RemoteBytes);

public enum UpdateCheckStatus
{
    NoUpdateUrl,
    NoUpdateAvailable,
    UpdateAvailable,
    DownloadFailure,
    ValidationFailure,
    ReplacementFailure,
    UpdateApplied
}