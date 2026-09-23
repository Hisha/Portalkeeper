using System;
using System.IO;

namespace Portalkeeper.Services;

// Minimal filesystem helpers for managed runtime construction. Low-level path
// safety reuses ManagedPath; these helpers add canonicalization and containment
// checks without introducing overlays, junctions, or runtime swapping.
public static class RuntimePaths
{
    private static StringComparison Comparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    public static string Canonical(string path) => Path.GetFullPath(path);

    public static string NormalizeRelative(string value, bool fileName = false) =>
        ManagedPath.Relative(value, fileName);

    public static string Resolve(string root, string relative) =>
        ManagedPath.Resolve(root, relative);

    public static bool IsWithin(string root, string candidate)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(candidate))
            return false;

        try
        {
            var fullRoot =
                Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) +
                Path.DirectorySeparatorChar;

            return Path.GetFullPath(candidate).StartsWith(fullRoot, Comparison);
        }
        catch
        {
            return false;
        }
    }

    public static bool SamePath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) && string.IsNullOrWhiteSpace(right))
            return true;

        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;

        try
        {
            return string.Equals(Canonical(left!), Canonical(right!), Comparison);
        }
        catch
        {
            return false;
        }
    }
}