using System;
using System.Collections.Generic;
namespace Portalkeeper.Models;

// Canonical Schema v1 model; existing consumers retain their typed property names.
public sealed class RealmInfo
{
    public int SchemaVersion { get; init; }
    public bool IsLegacyCompatibility { get; init; }
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string WebsiteUrl { get; init; } = "";
    public string Address { get; init; } = "";
    public int AuthPort { get; init; } = 3724;
    public int WorldPort { get; init; } = 8085;
    public ClientRequirements Client { get; init; } = new();
    public string MinimumVersion { get; init; } = "";
    public string ManifestUrl { get; init; } = "";
    public string NewsUrl { get; init; } = "";
    public string StatusUrl { get; init; } = "";
    public string CalendarUrl { get; init; } = "";
    public string ArmoryUrl { get; init; } = "";
    public string ConfigUrl { get; init; } = "";
    public IReadOnlyList<AddonDefinition> Addons { get; init; } = Array.Empty<AddonDefinition>();
    public IReadOnlyList<PatchDefinition> Patches { get; init; } = Array.Empty<PatchDefinition>();
    public bool IsConfigured => (SchemaVersion == 1 || (SchemaVersion == 0 && IsLegacyCompatibility)) && Name.Length > 0 && Address.Length > 0;
}
public sealed class ClientRequirements
{
    public string Version { get; init; } = "3.3.5a";
    public string Build { get; init; } = "12340";
    public string Executable { get; init; } = "Wow.exe";
    public string ExecutableSha256 { get; init; } = "";
}
public enum ComponentRequirement { Required, Recommended, Optional }
public sealed class PatchDefinition
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public ComponentRequirement Requirement { get; init; }
    public string SourceType { get; init; } = "HTTP";
    public string SourceUrl { get; init; } = "";
    public string FileName { get; init; } = "";
    public string InstallDirectory { get; init; } = "";
    public string Sha256 { get; init; } = "";
}
public sealed record PatchInfo(PatchDefinition Definition, string Destination, bool IsInstalled, bool IsValid, string Status);
