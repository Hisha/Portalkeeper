using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
namespace Portalkeeper.Services;

public static class ManagedPath
{
    public static string Relative(string value, bool fileName = false)
    {
        var parts = value.Replace('\\', '/').Split('/');
        if (string.IsNullOrWhiteSpace(value) || (fileName && parts.Length != 1) ||
            parts.Any(p => p.Length == 0 || p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ') ||
                p.Any(c => c < 32 || "<>:\"|?*".Contains(c)) ||
                Regex.IsMatch(p, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])($|\.)", RegexOptions.IgnoreCase)))
            throw new InvalidDataException($"Unsafe relative path: {value}");
        return string.Join(Path.DirectorySeparatorChar, parts);
    }
    public static string Resolve(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root);
        var path = Path.GetFullPath(Path.Combine(fullRoot, Relative(relative)));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.StartsWith(fullRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison))
            throw new InvalidDataException("Managed path escapes the WoW directory.");
        // Reject existing symlinks/junctions, including ancestors and dangling links.
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current) || new FileInfo(current).LinkTarget is not null) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Managed paths cannot contain symbolic links or junctions.");
        }
        return path;
    }
    public static string Url(string value, bool optional = false)
    {
        if (optional && value.Length == 0) return value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https") || uri.UserInfo.Length > 0 ||
            value.Any(char.IsControl))
            throw new InvalidDataException("Expected a public HTTP or HTTPS URL without credentials.");
        return value;
    }
    public static string Hash(string value)
    {
        if (value.Length != 0 && (value.Length != 64 || !value.All(Uri.IsHexDigit)))
            throw new InvalidDataException("SHA-256 must be empty or exactly 64 hexadecimal characters.");
        return value;
    }
}
