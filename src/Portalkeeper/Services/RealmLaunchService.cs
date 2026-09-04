using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Portalkeeper.Models;

namespace Portalkeeper.Services;

public sealed class RealmLaunchService
{
    private static readonly string[] WowExecutableNames =
    {
        "Wow.exe",
        "wow.exe",
        "WoW.exe"
    };

    public RealmLaunchResult PrepareAndLaunch(
        string clientDirectory,
        RealmInfo realm)
    {
        if (string.IsNullOrWhiteSpace(clientDirectory))
            throw new InvalidOperationException(
                "No World of Warcraft client directory is configured.");

        if (!realm.IsConfigured)
            throw new InvalidOperationException(
                "Realm configuration is incomplete.");

        if (realm.Address.Contains('\r') || realm.Address.Contains('\n'))
            throw new InvalidOperationException(
                "Realm address contains invalid characters.");

        var fullClientDirectory = Path.GetFullPath(clientDirectory);

        if (!Directory.Exists(fullClientDirectory))
            throw new DirectoryNotFoundException(
                "The configured World of Warcraft client directory no longer exists.");

        var wowExecutable = FindWowExecutable(fullClientDirectory)
            ?? throw new FileNotFoundException(
                "Wow.exe was not found in the configured client directory.");

        var localeDirectory = FindLocaleDirectory(fullClientDirectory);
        var locale = Path.GetFileName(localeDirectory);
        var realmlistPath = Path.Combine(localeDirectory, "realmlist.wtf");

        WriteRealmlist(realmlistPath, realm.Address, fullClientDirectory);
        LaunchWow(wowExecutable, fullClientDirectory);

        return new RealmLaunchResult
        {
            Locale = locale,
            RealmlistPath = realmlistPath,
            ExecutablePath = wowExecutable
        };
    }

    private static string? FindWowExecutable(string clientDirectory)
    {
        foreach (var fileName in WowExecutableNames)
        {
            var path = Path.Combine(clientDirectory, fileName);
            if (File.Exists(path))
                return path;
        }

        return null;
    }

    private static string FindLocaleDirectory(string clientDirectory)
    {
        var dataDirectory = Path.Combine(clientDirectory, "Data");

        if (!Directory.Exists(dataDirectory))
            throw new DirectoryNotFoundException(
                "The WoW Data directory was not found.");

        var directories = Directory
            .EnumerateDirectories(dataDirectory, "*", SearchOption.TopDirectoryOnly)
            .ToArray();

        var existingRealmlists = directories
            .Where(directory =>
                File.Exists(Path.Combine(directory, "realmlist.wtf")))
            .ToArray();

        if (existingRealmlists.Length == 1)
            return existingRealmlists[0];

        if (existingRealmlists.Length > 1)
        {
            throw new InvalidOperationException(
                "Multiple WoW locale directories contain realmlist.wtf. " +
                "Portalkeeper cannot safely choose one automatically.");
        }

        var localeCandidates = new List<string>();

        foreach (var directory in directories)
        {
            var locale = Path.GetFileName(directory);

            if (string.IsNullOrWhiteSpace(locale))
                continue;

            var hasLocaleMpq = Directory
                .EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .Any(file =>
                    Path.GetFileName(file).Equals(
                        $"locale-{locale}.MPQ",
                        StringComparison.OrdinalIgnoreCase));

            if (hasLocaleMpq)
                localeCandidates.Add(directory);
        }

        if (localeCandidates.Count == 1)
            return localeCandidates[0];

        if (localeCandidates.Count == 0)
        {
            throw new InvalidOperationException(
                "Portalkeeper could not determine the WoW locale directory under Data.");
        }

        throw new InvalidOperationException(
            "Multiple WoW locale directories were detected. " +
            "Portalkeeper cannot safely choose one automatically.");
    }

    private static void WriteRealmlist(
        string realmlistPath,
        string realmAddress,
        string clientDirectory)
    {
        var desiredContents = $"set realmlist {realmAddress.Trim()}{Environment.NewLine}";

        if (File.Exists(realmlistPath))
        {
            var currentContents = File.ReadAllText(realmlistPath);

            if (string.Equals(
                    NormalizeLineEndings(currentContents),
                    NormalizeLineEndings(desiredContents),
                    StringComparison.Ordinal))
            {
                return;
            }

            BackupRealmlist(realmlistPath, clientDirectory);
        }

        var directory = Path.GetDirectoryName(realmlistPath)
            ?? throw new InvalidOperationException(
                "Unable to determine the realmlist directory.");

        Directory.CreateDirectory(directory);

        var temporaryPath = realmlistPath + ".portalkeeper.tmp";

        try
        {
            File.WriteAllText(temporaryPath, desiredContents);
            File.Move(temporaryPath, realmlistPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static void BackupRealmlist(
        string realmlistPath,
        string clientDirectory)
    {
        var backupDirectory = Path.Combine(
            clientDirectory,
            ".portalkeeper",
            "backups",
            "realmlist");

        Directory.CreateDirectory(backupDirectory);

        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff");
        var backupPath = Path.Combine(
            backupDirectory,
            $"realmlist-{timestamp}.wtf");

        File.Copy(realmlistPath, backupPath, false);
    }

    private static string NormalizeLineEndings(string value)
    {
        return value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal)
            .Trim();
    }

    private static void LaunchWow(
        string wowExecutable,
        string clientDirectory)
    {
        ProcessStartInfo startInfo;

        if (OperatingSystem.IsWindows())
        {
            startInfo = new ProcessStartInfo
            {
                FileName = wowExecutable,
                WorkingDirectory = clientDirectory,
                UseShellExecute = false
            };
        }
        else if (OperatingSystem.IsLinux())
        {
            var wineExecutable = FindOnPath("wine") ?? FindOnPath("wine64");

            if (wineExecutable is null)
            {
                throw new InvalidOperationException(
                    "Wine was not found in PATH. Install Wine or make the wine executable available in PATH.");
            }

            startInfo = new ProcessStartInfo
            {
                FileName = wineExecutable,
                WorkingDirectory = clientDirectory,
                UseShellExecute = false
            };

            var winePrefix = FindWinePrefix(wowExecutable);

            if (!string.IsNullOrWhiteSpace(winePrefix))
                startInfo.Environment["WINEPREFIX"] = winePrefix;

            startInfo.ArgumentList.Add(wowExecutable);
        }
        else
        {
            throw new PlatformNotSupportedException(
                "Portalkeeper launching is currently supported on Windows and Linux.");
        }

        var process = Process.Start(startInfo);

        if (process is null)
            throw new InvalidOperationException(
                "The World of Warcraft process could not be started.");
    }


    private static string? FindWinePrefix(string wowExecutable)
    {
        var configuredPrefix =
            Environment.GetEnvironmentVariable("WINEPREFIX");

        if (!string.IsNullOrWhiteSpace(configuredPrefix))
        {
            var expandedPrefix = ExpandHome(configuredPrefix);

            if (Directory.Exists(expandedPrefix))
                return expandedPrefix;
        }

        var homeDirectory =
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (string.IsNullOrWhiteSpace(homeDirectory))
            return null;

        var applicationsDirectory = Path.Combine(
            homeDirectory,
            ".local",
            "share",
            "applications");

        if (!Directory.Exists(applicationsDirectory))
            return null;

        var normalizedWowPath =
            Path.GetFullPath(wowExecutable);

        foreach (var desktopFile in Directory.EnumerateFiles(
                     applicationsDirectory,
                     "*.desktop",
                     SearchOption.TopDirectoryOnly))
        {
            string[] lines;

            try
            {
                lines = File.ReadAllLines(desktopFile);
            }
            catch
            {
                continue;
            }

            var execLine = lines.FirstOrDefault(line =>
                line.StartsWith("Exec=", StringComparison.OrdinalIgnoreCase));

            if (string.IsNullOrWhiteSpace(execLine))
                continue;

            var command = execLine["Exec=".Length..];

            if (!command.Contains(
                    normalizedWowPath,
                    StringComparison.Ordinal))
            {
                continue;
            }

            const string prefixMarker = "WINEPREFIX=";
            var prefixIndex = command.IndexOf(
                prefixMarker,
                StringComparison.Ordinal);

            if (prefixIndex < 0)
                continue;

            var prefixStart = prefixIndex + prefixMarker.Length;
            var prefixEnd = command.IndexOf(' ', prefixStart);

            var prefix = prefixEnd < 0
                ? command[prefixStart..]
                : command[prefixStart..prefixEnd];

            prefix = prefix.Trim().Trim('"', '\'');
            prefix = ExpandHome(prefix);

            if (Directory.Exists(prefix))
                return prefix;
        }

        return null;
    }

    private static string ExpandHome(string path)
    {
        if (!path.StartsWith("~/", StringComparison.Ordinal))
            return path;

        var homeDirectory =
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        return string.IsNullOrWhiteSpace(homeDirectory)
            ? path
            : Path.Combine(homeDirectory, path[2..]);
    }

    private static string? FindOnPath(string executableName)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");

        if (string.IsNullOrWhiteSpace(pathValue))
            return null;

        foreach (var directory in pathValue.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory))
                continue;

            var candidate = Path.Combine(directory, executableName);

            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }
}

public sealed class RealmLaunchResult
{
    public string Locale { get; init; } = string.Empty;
    public string RealmlistPath { get; init; } = string.Empty;
    public string ExecutablePath { get; init; } = string.Empty;
}
