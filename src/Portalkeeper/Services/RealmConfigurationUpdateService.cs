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
            return new RealmConfigurationUpdateResult(UpdateCheckStatus.NoUpdateUrl, null, null);
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
                remoteHash);
        }
        catch (Exception)
        {
            // Network/download failures must not damage or modify the existing local realm configuration
            return new RealmConfigurationUpdateResult(UpdateCheckStatus.DownloadFailure, null, null);
        }
    }
}

public sealed record RealmConfigurationUpdateResult(
    UpdateCheckStatus Status,
    string? LocalHash,
    string? RemoteHash);

public enum UpdateCheckStatus
{
    NoUpdateUrl,
    NoUpdateAvailable,
    UpdateAvailable,
    DownloadFailure
}