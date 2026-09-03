namespace Portalkeeper.Models;

public sealed class ClientInfo
{
    public string DirectoryPath { get; init; } = string.Empty;

    public string ExecutablePath { get; init; } = string.Empty;

    public string Version { get; init; } = string.Empty;

    public bool ExecutableFound { get; init; }

    public bool IsSupportedClient { get; init; }

    public string StatusMessage { get; init; } = string.Empty;
}