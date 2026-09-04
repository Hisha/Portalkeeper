using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Portalkeeper.Models;

namespace Portalkeeper.Services;

public sealed class RealmHealthService
{
    private static readonly TimeSpan ConnectTimeout =
        TimeSpan.FromSeconds(2);

    public async Task<RealmHealthInfo> CheckAsync(
        RealmInfo realm,
        CancellationToken cancellationToken = default)
    {
        if (!realm.IsConfigured)
        {
            return new RealmHealthInfo
            {
                State = RealmHealthState.Unknown
            };
        }

        var authTask = CanConnectAsync(
            realm.Address,
            realm.AuthPort,
            cancellationToken);

        var worldTask = CanConnectAsync(
            realm.Address,
            realm.WorldPort,
            cancellationToken);

        await Task.WhenAll(authTask, worldTask);

        var authReachable = await authTask;
        var worldReachable = await worldTask;

        var state = (authReachable, worldReachable) switch
        {
            (true, true) => RealmHealthState.Online,
            (true, false) => RealmHealthState.AuthOnly,
            (false, true) => RealmHealthState.WorldOnly,
            _ => RealmHealthState.Offline
        };

        return new RealmHealthInfo
        {
            State = state,
            AuthReachable = authReachable,
            WorldReachable = worldReachable
        };
    }

    private static async Task<bool> CanConnectAsync(
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeout =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);

            timeout.CancelAfter(ConnectTimeout);

            using var client = new TcpClient();

            await client.ConnectAsync(
                host,
                port,
                timeout.Token);

            return client.Connected;
        }
        catch
        {
            return false;
        }
    }
}
