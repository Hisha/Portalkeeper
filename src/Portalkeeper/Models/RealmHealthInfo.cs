namespace Portalkeeper.Models;

public enum RealmHealthState
{
    Unknown = 0,
    Checking = 1,
    Online = 2,
    AuthOnly = 3,
    WorldOnly = 4,
    Offline = 5
}

public sealed class RealmHealthInfo
{
    public RealmHealthState State { get; init; }
    public bool AuthReachable { get; init; }
    public bool WorldReachable { get; init; }
}
