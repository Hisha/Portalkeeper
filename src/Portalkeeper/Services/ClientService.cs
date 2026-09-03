using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Portalkeeper.Models;

namespace Portalkeeper.Services;

public sealed class ClientService
{
    public const string SupportedVersion = "3.3.5";
    public const string SupportedBuild = "12340";

    private static readonly string[] BuildMarkers =
    {
        "World of WarCraft (build 12340)",
        "WoW [Release] Build 12340",
        "WOWCOMSATCLIENT12340",
        "<version>12340</version>"
    };

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

        if (!IsWindowsExecutable(executablePath))
        {
            return new ClientInfo
            {
                DirectoryPath = fullDirectoryPath,
                ExecutablePath = executablePath,
                ExecutableFound = true,
                StatusMessage = "Wow.exe was found, but it is not a valid Windows executable."
            };
        }

        //
        // First try normal executable version metadata.
        // This generally works well on Windows.
        //
        var versionInfo = TryReadVersionInfo(executablePath);

        if (IsSupportedVersionString(versionInfo))
        {
            return CreateSupportedClient(
                fullDirectoryPath,
                executablePath);
        }

        //
        // PE version-resource parsing is not always available/reliable
        // cross-platform, so inspect the executable itself for Blizzard's
        // embedded build markers.
        //
        if (ContainsSupportedBuildMarkers(executablePath))
        {
            return CreateSupportedClient(
                fullDirectoryPath,
                executablePath);
        }

        var detectedVersion =
            string.IsNullOrWhiteSpace(versionInfo)
                ? "unknown"
                : versionInfo;

        return new ClientInfo
        {
            DirectoryPath = fullDirectoryPath,
            ExecutablePath = executablePath,
            Version = detectedVersion,
            ExecutableFound = true,
            IsSupportedClient = false,
            StatusMessage =
                $"Wow.exe found, but build {SupportedBuild} could not be verified."
        };
    }

    private static ClientInfo CreateSupportedClient(
        string directoryPath,
        string executablePath)
    {
        return new ClientInfo
        {
            DirectoryPath = directoryPath,
            ExecutablePath = executablePath,
            Version = $"3.3.5a ({SupportedBuild})",
            ExecutableFound = true,
            IsSupportedClient = true,
            StatusMessage =
                $"World of Warcraft 3.3.5a build {SupportedBuild} verified."
        };
    }

    private static string? FindWowExecutable(string directoryPath)
    {
        //
        // Windows file systems are normally case-insensitive.
        // Linux file systems are normally case-sensitive, so support
        // the common capitalization variants explicitly.
        //
        var candidates = new[]
        {
            Path.Combine(directoryPath, "Wow.exe"),
            Path.Combine(directoryPath, "wow.exe"),
            Path.Combine(directoryPath, "WoW.exe")
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

    private static bool IsWindowsExecutable(string executablePath)
    {
        try
        {
            using var stream = File.OpenRead(executablePath);

            if (stream.Length < 2)
            {
                return false;
            }

            //
            // Windows PE executables begin with the DOS "MZ" signature.
            //
            return stream.ReadByte() == 'M' &&
                   stream.ReadByte() == 'Z';
        }
        catch
        {
            return false;
        }
    }

    private static string TryReadVersionInfo(string executablePath)
    {
        try
        {
            var info =
                FileVersionInfo.GetVersionInfo(executablePath);

            if (!string.IsNullOrWhiteSpace(info.FileVersion))
            {
                return info.FileVersion;
            }

            if (!string.IsNullOrWhiteSpace(info.ProductVersion))
            {
                return info.ProductVersion;
            }
        }
        catch
        {
            //
            // Expected on some non-Windows platforms.
            // The embedded marker scan below is our fallback.
            //
        }

        return string.Empty;
    }

    private static bool IsSupportedVersionString(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return false;
        }

        return
            version.Contains(
                SupportedVersion,
                StringComparison.OrdinalIgnoreCase)
            &&
            version.Contains(
                SupportedBuild,
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsSupportedBuildMarkers(
        string executablePath)
    {
        try
        {
            var bytes = File.ReadAllBytes(executablePath);

            //
            // WoW 3.3.5a stores several useful build strings directly
            // inside Wow.exe. Reading them ourselves keeps detection
            // identical on Windows and Linux.
            //
            var contents = Encoding.ASCII.GetString(bytes);

            var buildFound = false;

            foreach (var marker in BuildMarkers)
            {
                if (contents.Contains(
                        marker,
                        StringComparison.Ordinal))
                {
                    buildFound = true;
                    break;
                }
            }

            if (!buildFound)
            {
                return false;
            }

            //
            // Require both the build marker and the 3.3.5 version marker.
            // This avoids accepting an executable based solely on a loose
            // occurrence of "12340".
            //
            return contents.Contains(
                SupportedVersion,
                StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }
}