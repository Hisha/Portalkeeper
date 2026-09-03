using System;
using System.Diagnostics;
using System.IO;
using Portalkeeper.Models;

namespace Portalkeeper.Services;

public sealed class ClientService
{
    public const string SupportedVersion = "3.3.5";
    public const string SupportedBuild = "12340";

    public ClientInfo ValidateClient(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return new ClientInfo
            {
                StatusMessage = "No World of Warcraft installation configured."
            };
        }

        string fullDirectoryPath;

        try
        {
            fullDirectoryPath = Path.GetFullPath(directoryPath);
        }
        catch
        {
            return new ClientInfo
            {
                DirectoryPath = directoryPath,
                StatusMessage = "The selected path is invalid."
            };
        }

        var executablePath = FindWowExecutable(fullDirectoryPath);

        if (executablePath is null)
        {
            return new ClientInfo
            {
                DirectoryPath = fullDirectoryPath,
                StatusMessage = "Wow.exe was not found in the selected folder."
            };
        }

        string version = string.Empty;

        try
        {
            var versionInfo = FileVersionInfo.GetVersionInfo(executablePath);

            version =
                versionInfo.FileVersion
                ?? versionInfo.ProductVersion
                ?? string.Empty;
        }
        catch
        {
            // We still report that Wow.exe exists even if the platform
            // cannot read the Windows version resource.
        }

        var isSupportedClient =
            version.Contains(SupportedBuild, StringComparison.OrdinalIgnoreCase) &&
            version.Contains(SupportedVersion, StringComparison.OrdinalIgnoreCase);

        string statusMessage;

        if (isSupportedClient)
        {
            statusMessage =
                $"World of Warcraft 3.3.5a build {SupportedBuild} detected.";
        }
        else if (string.IsNullOrWhiteSpace(version))
        {
            statusMessage =
                "Wow.exe found, but Portalkeeper could not determine its version.";
        }
        else
        {
            statusMessage =
                $"Unsupported WoW client version detected: {version}";
        }

        return new ClientInfo
        {
            DirectoryPath = fullDirectoryPath,
            ExecutablePath = executablePath,
            Version = version,
            ExecutableFound = true,
            IsSupportedClient = isSupportedClient,
            StatusMessage = statusMessage
        };
    }

    private static string? FindWowExecutable(string directoryPath)
    {
        var candidates = new[]
        {
            Path.Combine(directoryPath, "Wow.exe"),
            Path.Combine(directoryPath, "wow.exe")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}