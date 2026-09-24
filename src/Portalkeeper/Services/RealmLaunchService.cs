using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
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

    public string GetLaunchEnvironmentSummary(string clientDirectory)
    {
        if (OperatingSystem.IsWindows())
            return "Native Windows launch";

        if (!OperatingSystem.IsLinux())
            return "Unsupported launch platform";

        var wineExecutable = FindOnPath("wine") ?? FindOnPath("wine64");

        if (wineExecutable is null)
            return "Wine not found in PATH";

        if (string.IsNullOrWhiteSpace(clientDirectory) ||
            !Directory.Exists(clientDirectory))
        {
            return $"Wine: {wineExecutable}";
        }

        var wowExecutable = FindWowExecutable(Path.GetFullPath(clientDirectory));

        if (wowExecutable is null)
            return $"Wine: {wineExecutable}";

        var winePrefix = FindWinePrefix(wowExecutable);

        return string.IsNullOrWhiteSpace(winePrefix)
            ? $"Wine: {wineExecutable} • default prefix"
            : $"Wine: {wineExecutable} • Prefix: {winePrefix}";
    }

    public RealmLaunchResult PrepareAndLaunch(
        string clientDirectory,
        RealmInfo realm,
        string? sourceClientDirectory = null)
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

        string wowExecutable;
        if (realm.Client.RuntimeMode == ClientRuntimeMode.Isolated)
        {
            var source = sourceClientDirectory ??
                throw new InvalidOperationException("An isolated launch requires its source client.");
            RealmRuntimeResolver.RequireReady(fullClientDirectory, source, realm);
            var manifest = new ManagedRuntimeManifestService().Load(
                RuntimePaths.Resolve(fullClientDirectory, ManagedRuntimeBuilder.ManifestRelativePath));
            wowExecutable = RealmExecutableService.RequireValid(fullClientDirectory, source, realm, manifest);
        }
        else
        {
            wowExecutable = ClientService.FindWowExecutable(fullClientDirectory, realm.Client.Executable)
                ?? throw new FileNotFoundException("Wow.exe was not found in the configured client directory.");
        }

        var localeDirectory = FindLocaleDirectory(fullClientDirectory);
        var locale = Path.GetFileName(localeDirectory);
        var realmlistPath = Path.Combine(localeDirectory, "realmlist.wtf");

        // A managed launch must not follow user-created links outside its root
        // or replace a baseline entry. Legacy behavior is unchanged.
        if (File.Exists(Path.Combine(fullClientDirectory, ManagedRuntimeBuilder.ManifestRelativePath)))
        {
            ManagedRuntimeWriteGuard.Check(fullClientDirectory,
                ManagedPath.Resolve(fullClientDirectory, Path.GetRelativePath(fullClientDirectory, realmlistPath)));
            ManagedRuntimeWriteGuard.Check(fullClientDirectory,
                ManagedPath.Resolve(fullClientDirectory, "WTF/Config.wtf"));
            ManagedPath.Resolve(fullClientDirectory, ".portalkeeper/backups/realmlist");
            ManagedPath.Resolve(fullClientDirectory, ".portalkeeper/backups/config");
        }
        WriteRealmlist(realmlistPath, realm.Address, fullClientDirectory);
        if (!string.IsNullOrWhiteSpace(realm.GameRealmName))
            WriteGameRealmName(Path.Combine(fullClientDirectory, "WTF", "Config.wtf"), realm.GameRealmName, fullClientDirectory);
        var process = LaunchWow(wowExecutable, fullClientDirectory, sourceClientDirectory, realm.Client.Executable);

        return new RealmLaunchResult
        {
            Locale = locale,
            RealmlistPath = realmlistPath,
            ExecutablePath = wowExecutable,
            Process = process
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

    private static void WriteGameRealmName(string configPath, string gameRealmName, string clientDirectory)
    {
        if (gameRealmName.Any(char.IsControl) || gameRealmName.Contains('"') || gameRealmName.Contains('\\'))
            throw new InvalidOperationException("GameRealmName contains characters that cannot be written to Config.wtf.");

        var desiredLine = $"SET realmName \"{gameRealmName}\"";
        var exists = File.Exists(configPath);
        var current = string.Empty;
        Encoding encoding = new UTF8Encoding(false);
        if (exists)
        {
            var bytes = File.ReadAllBytes(configPath);
            using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, true);
            current = reader.ReadToEnd();
            encoding = reader.CurrentEncoding;
            if (encoding.CodePage == Encoding.UTF8.CodePage &&
                !(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF))
                encoding = new UTF8Encoding(false);
        }

        var pattern = new Regex(@"^([ \t]*)SET[ \t]+realmName(?:[ \t]+[^\r\n]*)?[ \t]*(?=\r?$)",
            RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);
        var found = false;
        var updated = pattern.Replace(current, match =>
        {
            found = true;
            return match.Groups[1].Value + desiredLine;
        });
        if (!found)
        {
            var newline = current.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" :
                current.Contains('\n') ? "\n" : current.Contains('\r') ? "\r" : Environment.NewLine;
            if (updated.Length > 0 && !updated.EndsWith('\n') && !updated.EndsWith('\r'))
                updated += newline;
            updated += desiredLine + newline;
        }
        if (exists && string.Equals(current, updated, StringComparison.Ordinal))
            return;

        var directory = Path.GetDirectoryName(configPath)
            ?? throw new InvalidOperationException("Unable to determine the WoW configuration directory.");
        Directory.CreateDirectory(directory);
        if (exists)
        {
            var backupDirectory = Path.Combine(clientDirectory, ".portalkeeper", "backups", "config");
            Directory.CreateDirectory(backupDirectory);
            var backupPath = Path.Combine(backupDirectory, $"Config-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}.wtf");
            File.Copy(configPath, backupPath, false);
        }

        var temporaryPath = configPath + ".portalkeeper.tmp";
        try
        {
            File.WriteAllText(temporaryPath, updated, encoding);
            File.Move(temporaryPath, configPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static string NormalizeLineEndings(string value)
    {
        return value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal)
            .Trim();
    }

    private static Process LaunchWow(string wowExecutable, string clientDirectory,
        string? sourceClientDirectory, string executableName)
    {
        var process = Process.Start(CreateLaunchStartInfo(wowExecutable, clientDirectory, sourceClientDirectory, executableName));
        return process ?? throw new InvalidOperationException("The World of Warcraft process could not be started.");
    }

    // Resolve Wine from the source executable, but execute the effective runtime.
    // The explicit developer launch and normal ENTER REALM share this path.
    public static ProcessStartInfo CreateLaunchStartInfo(string wowExecutable, string clientDirectory,
        string? sourceClientDirectory = null, string executableName = "Wow.exe")
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

            var environmentExecutable = sourceClientDirectory is null ? wowExecutable :
                ClientService.FindWowExecutable(sourceClientDirectory, executableName)
                ?? throw new FileNotFoundException("The source launch executable was not found.");
            var winePrefix = FindWinePrefix(environmentExecutable);

            if (!string.IsNullOrWhiteSpace(winePrefix))
                startInfo.Environment["WINEPREFIX"] = winePrefix;

            startInfo.ArgumentList.Add(wowExecutable);
        }
        else
        {
            throw new PlatformNotSupportedException(
                "Portalkeeper launching is currently supported on Windows and Linux.");
        }

        return startInfo;
    }

    public async Task WaitForGameExitAsync(RealmLaunchResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var process = result.Process
            ?? throw new InvalidOperationException(
                "No World of Warcraft process is associated with this launch.");

        // In the normal Windows and Wine paths the process returned by
        // Process.Start remains alive for the lifetime of the game.
        await Task.Delay(500);

        if (!process.HasExited)
        {
            await process.WaitForExitAsync();
            return;
        }

        // Some Wine configurations hand the executable off to another
        // process and let the original loader exit. In that case, follow
        // the process by its command line under /proc.
        if (!OperatingSystem.IsLinux())
            return;

        var foundGame = false;

        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (IsLinuxGameProcessRunning(result.ExecutablePath))
            {
                foundGame = true;
                break;
            }

            await Task.Delay(250);
        }

        if (!foundGame)
            return;

        while (IsLinuxGameProcessRunning(result.ExecutablePath))
            await Task.Delay(1000);
    }

    private static bool IsLinuxGameProcessRunning(string wowExecutable)
    {
        var normalizedPath = Path.GetFullPath(wowExecutable);
        const string procDirectory = "/proc";

        if (!Directory.Exists(procDirectory))
            return false;

        foreach (var processDirectory in Directory.EnumerateDirectories(procDirectory))
        {
            var name = Path.GetFileName(processDirectory);

            if (string.IsNullOrWhiteSpace(name) ||
                !name.All(char.IsDigit))
            {
                continue;
            }

            var commandLinePath = Path.Combine(processDirectory, "cmdline");

            try
            {
                if (!File.Exists(commandLinePath))
                    continue;

                var commandLine = File.ReadAllText(commandLinePath)
                    .Replace('\0', ' ');

                if (commandLine.Contains(
                        normalizedPath,
                        StringComparison.Ordinal))
                {
                    return true;
                }
            }
            catch
            {
                // Processes may exit while /proc is being inspected.
            }
        }

        return false;
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

        var normalizedWowPath = Path.GetFullPath(wowExecutable);

        foreach (var applicationsDirectory in GetDesktopApplicationDirectories())
        {
            if (!Directory.Exists(applicationsDirectory))
                continue;

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
                prefix = ExpandHomeAgainstCandidates(prefix);

                if (Directory.Exists(prefix))
                    return prefix;
            }
        }

        return null;
    }

    private static IEnumerable<string> GetDesktopApplicationDirectories()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrWhiteSpace(xdgDataHome))
        {
            var directory = Path.Combine(xdgDataHome, "applications");
            if (seen.Add(directory))
                yield return directory;
        }

        foreach (var home in GetCandidateHomeDirectories())
        {
            var directory = Path.Combine(
                home,
                ".local",
                "share",
                "applications");

            if (seen.Add(directory))
                yield return directory;
        }
    }

    private static IEnumerable<string> GetCandidateHomeDirectories()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var profile = Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile);

        if (!string.IsNullOrWhiteSpace(profile) && seen.Add(profile))
            yield return profile;

        var home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrWhiteSpace(home) && seen.Add(home))
            yield return home;

        var accountHome = GetLinuxAccountHomeDirectory();
        if (!string.IsNullOrWhiteSpace(accountHome) && seen.Add(accountHome))
            yield return accountHome;
    }

    private static string? GetLinuxAccountHomeDirectory()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/etc/passwd"))
            return null;

        try
        {
            var userName = Environment.UserName;

            foreach (var line in File.ReadLines("/etc/passwd"))
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
                    continue;

                var fields = line.Split(':');
                if (fields.Length < 6)
                    continue;

                if (!fields[0].Equals(userName, StringComparison.Ordinal))
                    continue;

                return string.IsNullOrWhiteSpace(fields[5])
                    ? null
                    : fields[5];
            }
        }
        catch
        {
            // If account-home discovery fails, normal HOME/XDG discovery
            // remains available.
        }

        return null;
    }

    private static string ExpandHomeAgainstCandidates(string path)
    {
        if (!path.StartsWith("~/", StringComparison.Ordinal))
            return path;

        foreach (var home in GetCandidateHomeDirectories())
        {
            var candidate = Path.Combine(home, path[2..]);
            if (Directory.Exists(candidate))
                return candidate;
        }

        return ExpandHome(path);
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
    public Process? Process { get; init; }
}
