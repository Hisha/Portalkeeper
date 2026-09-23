using System;
using System.IO;
using System.Linq;

namespace Portalkeeper.Services;

// Determines the source client's usable locale for runtime construction.
// Mirrors the repository's existing locale logic (a Data/<locale> directory
// containing a locale-<locale>.MPQ archive) so construction selects exactly
// the locale the client would use, without hard-coding enUS as the only
// possible architecture. Does not traverse into or modify source content.
public sealed class LocaleDiscovery
{
    public string Discover(string clientPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientPath);

        var dataDirectory = Path.Combine(Path.GetFullPath(clientPath), "Data");

        if (!Directory.Exists(dataDirectory))
            throw new DirectoryNotFoundException(
                "The source client does not contain a Data directory.");

        var candidates = Directory
            .EnumerateDirectories(dataDirectory, "*", SearchOption.TopDirectoryOnly)
            .Where(directory =>
            {
                var locale = Path.GetFileName(directory);

                if (string.IsNullOrWhiteSpace(locale))
                    return false;

                return File.Exists(Path.Combine(
                    directory,
                    "locale-" + locale + ".MPQ"));
            })
            .Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        if (candidates.Length == 1)
            return candidates[0]!;

        if (candidates.Length == 0)
            throw new InvalidDataException(
                "Portalkeeper could not determine the client locale under Data.");

        throw new InvalidDataException(
            "Multiple WoW locale directories were detected; " +
            "Portalkeeper cannot safely choose one automatically.");
    }
}