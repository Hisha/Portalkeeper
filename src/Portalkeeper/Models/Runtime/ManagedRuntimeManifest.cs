using System;
using System.Collections.Generic;

namespace Portalkeeper.Models.Runtime;

public enum ManagedRuntimeState { Incomplete, Complete }

// Persisted descriptor for a per-realm managed runtime. Complete describes
// baseline construction; ProvisionedConfiguration additionally gates normal
// isolated operation after required realm content has been installed. The RealmId currently reuses the
// existing logical realm identity and is designed to accept an
// administrator-provided stable RealmID/GUID later without a rewrite.
public sealed class ManagedRuntimeManifest
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    // Empty for Checkpoint 2 baseline-only builds; populated only after provisioning.
    public string ProvisionedConfiguration { get; set; } = string.Empty;

    public string RealmId { get; init; } = string.Empty;

    public string RealmName { get; init; } = string.Empty;

    public string Generation { get; init; } = "1";

    public string SourceClientPath { get; init; } = string.Empty;

    public string SourceExecutableSha256 { get; init; } = string.Empty;

    public string RuntimePath { get; init; } = string.Empty;

    public string Locale { get; init; } = string.Empty;

    public string LaunchExecutableRelativePath { get; set; } = string.Empty;

    // Absent in CP2/CP3. Presence records ownership; readiness also checks the file.
    public ManagedRealmExecutable? RealmExecutable { get; set; }

    public ManagedRuntimeState State { get; init; } = ManagedRuntimeState.Incomplete;

    public DateTimeOffset? CreatedUtc { get; init; }

    public DateTimeOffset? CompletedUtc { get; init; }

    public List<ManagedRuntimeFileEntry> Files { get; init; } = new();
}