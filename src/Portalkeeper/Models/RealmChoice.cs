using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
namespace Portalkeeper.Models;

public sealed record RealmChoice(string Path, RealmInfo Realm)
{
    public string Name => Realm.Name;
    public string Description => Realm.Description;
    public string Address => Realm.Address;
    public bool IsLegacy => Realm.IsLegacyCompatibility;
    public static bool SamePath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try { return string.Equals(System.IO.Path.GetFullPath(left), System.IO.Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal); }
        catch (Exception) { return false; }
    }
    public static RealmChoice? Select(IReadOnlyList<RealmChoice> choices, string? savedPath)
    {
        var saved = choices.FirstOrDefault(c => SamePath(c.Path, savedPath));
        if (saved is not null) return saved;
        if (choices.Count == 1) return choices[0];
        // Preserve the previous first-start preference for a sole valid Schema v1
        // over retained legacy files. A missing saved choice still requires a prompt.
        if (string.IsNullOrWhiteSpace(savedPath))
        {
            var schema = choices.Where(c => !c.IsLegacy).Take(2).ToArray();
            if (schema.Length == 1) return schema[0];
        }
        return null;
    }
}
