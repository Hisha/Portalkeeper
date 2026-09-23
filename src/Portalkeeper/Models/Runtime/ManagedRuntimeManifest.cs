using System;
using System.Collections.Generic;

namespace Portalkeeper.Models.Runtime;

public enum ManagedRuntimeState { Incomplete, Complete }

// Persisted descriptor for a future per-realm managed runtime. Checkpoint 1
// introduces the model and serialization support only; no runtime manifests
// are written by normal application flow yet. The RealmId currently reuses the
// existing logical realm identity and is designed to accept an
// administrator-provided stable RealmID/GUID later without a rewrite.
public sealed class ManagedRuntimeManifest
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public string RealmId { get; init; } = string.Empty;

    public string RealmName { get; init; } = string.Empty;

    public string Generation { get; init; } = "1";

    public string SourceClientPath { get; init; } = string.Empty;

    public string SourceExecutableSha256 { get; init; } = string.Empty;

    public string RuntimePath { get; init; } = string.Empty;

    public string Locale { get; init; } = string.Empty;

    public string LaunchExecutableRelativePath { get; init; } = string.Empty;

    public ManagedRuntimeState State { get; init; } = ManagedRuntimeState.Incomplete;

    public DateTimeOffset? CreatedUtc { get; init; }

    public DateTimeOffset? CompletedUtc { get; init; }

    public List<ManagedRuntimeFileEntry> Files { get; init; } = new();
}