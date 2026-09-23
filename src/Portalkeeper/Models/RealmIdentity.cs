using System;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Portalkeeper.Models;

// Logical realm identity used by the client-local ownership ledger and the
// future managed-runtime manifest. The algorithm deliberately excludes source
// URLs, hashes and configuration location so the same realm always resolves to
// the same identity regardless of where its configuration lives.
public static class RealmIdentity
{
    public static string FromRealm(RealmInfo realm)
    {
        ArgumentNullException.ThrowIfNull(realm);

        var parts = new[]
        {
            realm.Name.ToUpperInvariant(),
            realm.Address.ToUpperInvariant(),
            realm.AuthPort.ToString(CultureInfo.InvariantCulture),
            realm.WorldPort.ToString(CultureInfo.InvariantCulture)
        };

        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(parts))));
    }

    // Accepts the current 64-hex logical identity and a future
    // administrator-provided stable RealmID/GUID so the manifest model does not
    // need to be rewritten when the identity evolves.
    public static bool IsValid(string? id) =>
        id is not null && (IsSha256Hex(id) || Guid.TryParse(id, out _));

    private static bool IsSha256Hex(string id) =>
        id.Length == 64 && id.All(Uri.IsHexDigit);
}