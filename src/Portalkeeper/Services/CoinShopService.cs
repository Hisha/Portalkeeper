using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Portalkeeper.Services;

public sealed record ShopCharacter(uint Guid, string Name, int Race, int Class, int Gender, int Level, bool Online);

/// <param name="Owned">Characters that have it already (or have the service waiting for their next login).</param>
/// <param name="Cannot">Characters that can't use it (race, class, level, a skill it needs).</param>
/// <param name="Preview">A picture of it that can be turned, when the realm has one.</param>
/// <param name="Locked">Not for sale to the account yet: none of its characters has come far enough in the realm's progression.</param>
/// <param name="Unlock">What opens a locked offer ("Defeat Ragnaros in Molten Core"), or empty.</param>
/// <param name="Account">Bought for the whole account (a mount or companion on a realm that shares them): no mail, every character gets it.</param>
/// <param name="Section">The heading it's listed under within its category ("BlizzCon 2005"), or empty for none.</param>
public sealed record ShopOffer(uint Id, string Category, string Name, string Description, string Icon, int Price,
    IReadOnlyList<uint> Owned, IReadOnlyList<uint> Cannot, ShopPreview? Preview = null, bool Locked = false, string Unlock = "",
    bool Account = false, string Section = "");

/// <summary>A turntable picture: <see cref="Frames"/> pictures side by side, one per angle as the model turns.</summary>
public sealed record ShopPreview(Uri Url, int Frames);

/// <summary>What the account can buy, its wallet, and its characters, as the portal answered.</summary>
public sealed record ShopListing(int Balance, IReadOnlyList<ShopCharacter> Characters, IReadOnlyList<ShopOffer> Offers, DateTimeOffset CachedAt);

public enum ShopPurchaseOutcome
{
    /// <summary>Paid for; <see cref="ShopPurchaseResult.Balance"/> is what's left.</summary>
    Bought,
    /// <summary>The game server said no (409): not enough coins, owned already, ... Nothing was taken.</summary>
    Refused,
    /// <summary>The portal turned it away (429, 400, 503, ...) before asking the game server. Nothing was taken.</summary>
    NotTried,
    /// <summary>The login is no longer good (401). A 403 (a ban, or a proxy's page) is only reported.</summary>
    LoggedOut,
    /// <summary>No answer from the game server or the portal: it may or may not have gone through.</summary>
    Unknown,
}

/// <param name="Code">The game server's reason for a refusal: closed, no_offer, no_character, busy, locked, owned, cannot, balance.</param>
public sealed record ShopPurchaseResult(ShopPurchaseOutcome Outcome, string Message, int? Balance = null, string? Code = null);

/// <summary>
/// The realm's coin shop (the portal's api/launcher/shop.php, in front of mod-boss-coins): what an
/// account can buy with the Tarnished Undermine Reals in its wallet, and buying it for one of its
/// characters. The portal keeps a listing for 15 seconds, so it's only asked when the Shop opens,
/// after a purchase and when the player refreshes.
/// </summary>
public sealed class CoinShopService
{
    public const string Endpoint = "shop.php";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly HttpClient _http;
    private readonly Uri _api;

    public CoinShopService(Uri? api = null, HttpClient? http = null)
    {
        _api = api ?? RealmBranding.LauncherApi;
        _http = http ?? Http;
    }

    /// <summary>
    /// The account's listing. Null when the realm has no shop (404). Throws
    /// <see cref="LauncherApiException"/> with the portal's message for anything else it refused
    /// (503: the game server can't be reached or the shop is closed; 401: the login expired), and
    /// HttpRequestException / TaskCanceledException / JsonException when it couldn't be asked.
    /// </summary>
    public async Task<ShopListing?> GetListingAsync(LauncherSession session, CancellationToken cancellationToken = default)
    {
        using var request = Authorized(HttpMethod.Get, session.Token);
        using var response = await _http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) throw await ErrorAsync(response, cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<ListingBody>(cancellationToken)
            ?? throw new InvalidDataException("The shop sent an empty answer.");
        return new ShopListing(
            Math.Max(0, body.Balance),
            (body.Characters ?? []).OfType<CharacterBody>().Where(c => c.Guid > 0)
                .Select(c => new ShopCharacter(c.Guid, c.Name ?? "", c.Race, c.Class, c.Gender, c.Level, c.Online)).ToArray(),
            (body.Offers ?? []).OfType<OfferBody>().Where(o => o.Id > 0)
                .Select(o => new ShopOffer(o.Id, (o.Category ?? "").Trim().ToLowerInvariant(), o.Name ?? "", o.Description ?? "",
                    o.Icon ?? "", Math.Max(0, o.Price), o.Owned ?? [], o.Cannot ?? [], Preview(o.Preview),
                    o.Locked, (o.Unlock ?? "").Trim(), o.Account, (o.Section ?? "").Trim())).ToArray(),
            body.CachedAt is > 0 and < 253402300799 ? DateTimeOffset.FromUnixTimeSeconds(body.CachedAt) : DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Buys <paramref name="offerId"/> for the character <paramref name="characterGuid"/>. Never
    /// throws for an answer or a lost connection: the result says what happened, and whether the
    /// purchase may have gone through without an answer (<see cref="ShopPurchaseOutcome.Unknown"/>).
    /// </summary>
    public async Task<ShopPurchaseResult> BuyAsync(LauncherSession session, uint offerId, uint characterGuid, CancellationToken cancellationToken = default)
    {
        HttpResponseMessage response;
        try
        {
            using var request = Authorized(HttpMethod.Post, session.Token);
            request.Content = JsonContent.Create(new BuyRequest(offerId, characterGuid));
            response = await _http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex) when (ex.HttpRequestError is HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError)
        {
            // No connection was made, so nothing was sent.
            return new ShopPurchaseResult(ShopPurchaseOutcome.NotTried, "Couldn't reach the shop. Nothing was bought; try again in a moment.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            if (cancellationToken.IsCancellationRequested) throw;
            return new ShopPurchaseResult(ShopPurchaseOutcome.Unknown,
                "The shop didn't answer. Look at your balance before you try again: the purchase may have gone through.");
        }

        using (response)
        {
            BuyBody? body = null;
            try { body = await response.Content.ReadFromJsonAsync<BuyBody>(cancellationToken); }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or HttpRequestException) { }
            var message = !string.IsNullOrWhiteSpace(body?.Message) ? body!.Message!
                : !string.IsNullOrWhiteSpace(body?.Error) && response.StatusCode != HttpStatusCode.Conflict ? body!.Error!
                : "";

            switch (response.StatusCode)
            {
                case HttpStatusCode.OK when body is { Ok: true }:
                    return new ShopPurchaseResult(ShopPurchaseOutcome.Bought,
                        message.Length > 0 ? message : "Bought.", body.Balance is int left ? Math.Max(0, left) : null);
                case HttpStatusCode.OK:
                    // A 200 without "ok": not what the portal sends. Don't guess either way.
                    return new ShopPurchaseResult(ShopPurchaseOutcome.Unknown,
                        "The shop sent an answer Portalkeeper doesn't understand. Look at your balance before you try again.");
                case HttpStatusCode.Conflict:
                    return new ShopPurchaseResult(ShopPurchaseOutcome.Refused,
                        message.Length > 0 ? message : "The purchase was refused.", null, body?.Error);
                case HttpStatusCode.Unauthorized:
                    return new ShopPurchaseResult(ShopPurchaseOutcome.LoggedOut,
                        message.Length > 0 ? message : "Your login has expired. Log in again.");
                case HttpStatusCode.TooManyRequests:
                    return new ShopPurchaseResult(ShopPurchaseOutcome.NotTried,
                        message.Length > 0 ? message : "Your last purchase is still going through. Try again in a moment.");
                case HttpStatusCode.BadGateway:
                    return new ShopPurchaseResult(ShopPurchaseOutcome.Unknown,
                        message.Length > 0 ? message : "The game server didn't answer. Look at your balance before you try again: the purchase may have gone through.");
                case >= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError:
                    return new ShopPurchaseResult(ShopPurchaseOutcome.NotTried,
                        message.Length > 0 ? message : $"The shop answered {(int)response.StatusCode} {response.ReasonPhrase}.");
                default:
                    // 503 is the portal refusing before the game server is asked; other 5xx (a proxy's
                    // 504) could have come after it, so they're treated as maybe-bought.
                    return new ShopPurchaseResult(
                        response.StatusCode == HttpStatusCode.ServiceUnavailable ? ShopPurchaseOutcome.NotTried : ShopPurchaseOutcome.Unknown,
                        message.Length > 0 ? message : $"The shop answered {(int)response.StatusCode} {response.ReasonPhrase}.");
            }
        }
    }

    /// <summary>The offer's picture, when the portal named one this can fetch (http or https, 1 to 360 frames).</summary>
    private static ShopPreview? Preview(PreviewBody? body) =>
        body is { Frames: >= 1 and <= 360 } && Uri.TryCreate(body.Url, UriKind.Absolute, out var url)
        && (url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeHttp)
            ? new ShopPreview(url, body.Frames) : null;

    private HttpRequestMessage Authorized(HttpMethod method, string token)
    {
        var request = new HttpRequestMessage(method, new Uri(_api, Endpoint));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static async Task<LauncherApiException> ErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string? message = null;
        try { message = (await response.Content.ReadFromJsonAsync<ErrorBody>(cancellationToken))?.Error; }
        catch (Exception ex) when (ex is JsonException or NotSupportedException) { }
        return new LauncherApiException(response.StatusCode, string.IsNullOrWhiteSpace(message)
            ? $"The shop answered {(int)response.StatusCode} {response.ReasonPhrase}."
            : message);
    }

    private sealed record CharacterBody(
        [property: JsonPropertyName("guid")] uint Guid,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("race")] int Race,
        [property: JsonPropertyName("class")] int Class,
        [property: JsonPropertyName("gender")] int Gender,
        [property: JsonPropertyName("level")] int Level,
        [property: JsonPropertyName("online")] bool Online);
    private sealed record OfferBody(
        [property: JsonPropertyName("id")] uint Id,
        [property: JsonPropertyName("category")] string? Category,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("icon")] string? Icon,
        [property: JsonPropertyName("price")] int Price,
        [property: JsonPropertyName("owned")] uint[]? Owned,
        [property: JsonPropertyName("cannot")] uint[]? Cannot,
        [property: JsonPropertyName("preview")] PreviewBody? Preview,
        [property: JsonPropertyName("locked")] bool Locked,
        [property: JsonPropertyName("unlock")] string? Unlock,
        [property: JsonPropertyName("account")] bool Account,
        [property: JsonPropertyName("section")] string? Section);
    private sealed record PreviewBody(
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("frames")] int Frames);
    private sealed record ListingBody(
        [property: JsonPropertyName("balance")] int Balance,
        [property: JsonPropertyName("characters")] CharacterBody?[]? Characters,
        [property: JsonPropertyName("offers")] OfferBody?[]? Offers,
        [property: JsonPropertyName("cached_at")] long CachedAt);
    private sealed record BuyRequest(
        [property: JsonPropertyName("offer")] uint Offer,
        [property: JsonPropertyName("character")] uint Character);
    private sealed record BuyBody(
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("balance")] int? Balance,
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("message")] string? Message);
    private sealed record ErrorBody([property: JsonPropertyName("error")] string? Error);
}
