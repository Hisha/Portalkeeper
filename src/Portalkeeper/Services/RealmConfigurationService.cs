using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Portalkeeper.Models;
namespace Portalkeeper.Services;

public sealed class RealmConfigurationService
{
    public static string CurrentVersion => typeof(RealmConfigurationService).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.1.0";
    public RealmInfo Load(string path) => Parse(File.ReadAllText(path));
    internal static Dictionary<string, Dictionary<string, string>> ReadIni(string text)
    {
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? current = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimStart('\uFEFF');
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';')) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                var name = line[1..^1].Trim();
                current = new(StringComparer.OrdinalIgnoreCase);
                if (!sections.TryAdd(name, current)) throw new InvalidDataException($"Duplicate section: {name}");
                continue;
            }
            var equals = line.IndexOf('=');
            if (current is null || equals <= 0 || !current.TryAdd(line[..equals].Trim(), line[(equals+1)..].Trim()))
                throw new InvalidDataException("Malformed or duplicate realm configuration setting.");
        }
        return sections;
    }
    public RealmInfo Parse(string text, string? currentVersion = null)
    {
        var ini = ReadIni(text);
        string Get(string section, string key, bool required = false)
        {
            var value = ini.TryGetValue(section, out var fields) && fields.TryGetValue(key, out var found) ? found : "";
            if (required && value.Length == 0) throw new InvalidDataException($"Missing required [{section}] {key}.");
            return value;
        }
        var schema = Get("Config", "SchemaVersion");
        if (schema != "1") throw new InvalidDataException(schema.Length == 0
            ? "Unsupported realm configuration: SchemaVersion is missing. Legacy configuration requires migration to Schema v1."
            : $"Unsupported SchemaVersion={schema}. Upgrade Portalkeeper to use this realm.");
        foreach (var section in new[] { "Realm", "Connection", "Client", "Portalkeeper", "Services" })
            if (!ini.ContainsKey(section)) throw new InvalidDataException($"Missing required [{section}] section.");
        int Port(string key)
        {
            if (!int.TryParse(Get("Connection", key, true), out var n) || n < 1 || n > 65535)
                throw new InvalidDataException($"{key} must be a TCP port from 1 through 65535.");
            return n;
        }
        var address = Get("Connection", "Address", true);
        if (Uri.CheckHostName(address) == UriHostNameType.Unknown || address.Any(char.IsWhiteSpace))
            throw new InvalidDataException("Connection Address must be a host name or IP address.");
        var minimum = Get("Portalkeeper", "MinimumVersion", true);
        if (CompareVersions(currentVersion ?? CurrentVersion, minimum) < 0)
            throw new InvalidDataException($"This realm requires Portalkeeper {minimum} or newer; installed version is {currentVersion ?? CurrentVersion}. Upgrade Portalkeeper before using it.");
        var version = Get("Client", "Version", true);
        var build = Get("Client", "Build", true);
        if (!Regex.IsMatch(version, @"^\d+\.\d+\.\d+[a-z]?$", RegexOptions.CultureInvariant) ||
            !int.TryParse(build, out var buildNumber) || buildNumber <= 0)
            throw new InvalidDataException("Invalid Client Version or Build.");
        var addons = new List<AddonDefinition>();
        var patches = new List<PatchDefinition>();
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in ini.Keys)
        {
            bool addon = section.StartsWith("Addon.", StringComparison.OrdinalIgnoreCase);
            if (!addon && !section.StartsWith("Patch.", StringComparison.OrdinalIgnoreCase)) continue;
            var key = section[(section.IndexOf('.')+1)..];
            if (!Regex.IsMatch(key, @"^[A-Za-z0-9][A-Za-z0-9_-]*$")) throw new InvalidDataException($"Invalid component key: {key}");
            var requirementText = Get(section, "Requirement", true);
            if (!Enum.TryParse<ComponentRequirement>(requirementText, true, out var requirement) ||
                !Enum.GetNames<ComponentRequirement>().Any(n => n.Equals(requirementText, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"Invalid Requirement in [{section}].");
            var source = Get(section, "SourceType", true);
            var url = ManagedPath.Url(Get(section, "SourceURL", true));
            var name = Get(section, "Name", true);
            var directory = ManagedPath.Relative(Get(section, "InstallDirectory", true), addon);
            if (addon)
            {
                if (source != "GitHub" && source != "HTTP") throw new InvalidDataException($"Unsupported addon SourceType: {source}");
                if (source == "GitHub" && !GitHubAddonSourceService.TryParseRepositoryUrl(url, out _, out _))
                    throw new InvalidDataException("GitHub addons require a GitHub repository URL.");
                var reference = Get(section, "Ref");
                if (reference.Any(char.IsControl) || reference.Length > 256) throw new InvalidDataException("Invalid addon Ref.");
                if (!destinations.Add("Interface/AddOns/" + directory)) throw new InvalidDataException("Duplicate addon destination.");
                addons.Add(new AddonDefinition { Id = key.ToLowerInvariant(), Name = name, Folder = directory,
                    Required = requirement == ComponentRequirement.Required, Recommended = requirement == ComponentRequirement.Recommended,
                    GitUrl = source == "GitHub" ? url : "", DownloadUrl = source == "HTTP" ? url : "", Ref = reference });
            }
            else
            {
                if (source != "HTTP") throw new InvalidDataException($"Unsupported patch SourceType: {source}; use HTTP.");
                var file = ManagedPath.Relative(Get(section, "FileName", true), true);
                if (directory.Split(Path.DirectorySeparatorChar)[0].Equals(".portalkeeper", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Patches cannot replace Portalkeeper management metadata.");
                if (!destinations.Add(directory + "/" + file)) throw new InvalidDataException("Duplicate patch destination.");
                patches.Add(new PatchDefinition { Id = key, Name = name, Requirement = requirement, SourceUrl = url,
                    InstallDirectory = directory, FileName = file, Sha256 = ManagedPath.Hash(Get(section, "SHA256")) });
            }
        }
        return new RealmInfo { SchemaVersion = 1, Name = Get("Realm", "Name", true), Description = Get("Realm", "Description"),
            WebsiteUrl = ManagedPath.Url(Get("Realm", "WebsiteURL"), true), Address = address, AuthPort = Port("AuthPort"), WorldPort = Port("WorldPort"),
            Client = new ClientRequirements { Version = version, Build = build, Executable = ManagedPath.Relative(Get("Client", "Executable", true), true),
                ExecutableSha256 = ManagedPath.Hash(Get("Client", "ExecutableSHA256")) }, MinimumVersion = minimum,
            ManifestUrl = ManagedPath.Url(Get("Services", "ManifestURL"), true), NewsUrl = ManagedPath.Url(Get("Services", "NewsURL"), true),
            StatusUrl = ManagedPath.Url(Get("Services", "StatusURL"), true), CalendarUrl = ManagedPath.Url(Get("Services", "CalendarURL"), true),
            ArmoryUrl = ManagedPath.Url(Get("Services", "ArmoryURL"), true), ConfigUrl = ManagedPath.Url(Get("Services", "ConfigURL"), true), Addons = addons, Patches = patches };
    }
    public static int CompareVersions(string left, string right)
    {
        static (Version Core, string[] Pre) Read(string value)
        {
            var match = Regex.Match(value, @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$");
            if (!match.Success || !Version.TryParse(string.Join('.', match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value), out var core))
                throw new InvalidDataException($"Invalid semantic version: {value}");
            var pre = match.Groups[4].Success ? match.Groups[4].Value.Split('.') : Array.Empty<string>();
            if (pre.Any(p => p.All(char.IsDigit) && p.Length > 1 && p[0] == '0')) throw new InvalidDataException("Invalid semantic prerelease version.");
            return (core, pre);
        }
        var a = Read(left); var b = Read(right); var c = a.Core.CompareTo(b.Core);
        if (c != 0) return c;
        if (a.Pre.Length == 0 || b.Pre.Length == 0) return a.Pre.Length == b.Pre.Length ? 0 : a.Pre.Length == 0 ? 1 : -1;
        for (int i = 0; i < Math.Min(a.Pre.Length, b.Pre.Length); i++)
        {
            var x = a.Pre[i]; var y = b.Pre[i]; bool xn = x.All(char.IsDigit), yn = y.All(char.IsDigit);
            c = xn && yn ? (x.Length != y.Length ? x.Length.CompareTo(y.Length) : string.CompareOrdinal(x,y)) :
                xn != yn ? (xn ? -1 : 1) : string.CompareOrdinal(x,y);
            if (c != 0) return c;
        }
        return a.Pre.Length.CompareTo(b.Pre.Length);
    }
}
