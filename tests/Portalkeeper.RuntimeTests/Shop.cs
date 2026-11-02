using System.Net;
using System.Text;
using System.Text.Json;
using Portalkeeper.Services;
using Portalkeeper.ViewModels;

namespace Portalkeeper.RuntimeTests;

// Vaultrona fork: the coin shop (CoinShopService, ShopViewModel) against a stand-in for the
// portal's shop.php that answers what it's told to.
internal static partial class Program
{
    private const string ShopListingJson = """
        {"balance":45,
         "characters":[{"guid":7,"name":"Alys","race":10,"class":2,"gender":1,"level":62,"online":false},
                       {"guid":9,"name":"Bronn","race":3,"class":1,"gender":0,"level":12,"online":true}],
         "offers":[{"id":1,"category":"service","name":"Character Rename","description":"A new name.","icon":"","price":20,"owned":[7],"cannot":[]},
                   {"id":101,"category":"pet","name":"Red Panda","description":"A companion.","icon":"","price":10,"owned":[],"cannot":[],"preview":{"url":"file:///etc/passwd","frames":36}},
                   {"id":201,"category":"mount","name":"Great Red Panda","description":"A mount.","icon":"","price":40,"owned":[],"cannot":[9],"preview":{"url":"https://portal.test/realm/shop/201.webp?v=5","frames":36}},
                   {"id":202,"category":"mount","name":"Ashen Steed","description":"A horse.","icon":"","price":30,"account":true,"owned":[],"cannot":[]},
                   {"id":301,"category":"Toy","name":"Odd Thing","description":"","icon":"","price":50,"owned":[],"cannot":[]},
                   {"id":401,"category":"blizzcon","section":"BlizzCon 2005","name":"Murky","description":"A murloc.","icon":"","price":100,"owned":[],"cannot":[]},
                   {"id":402,"category":"blizzcon","section":"BlizzCon 2008","name":"Big Blizzard Bear","description":"A bear.","icon":"","price":250,"owned":[],"cannot":[]},
                   {"id":403,"category":"blizzcon","section":"BlizzCon 2008","name":"Another Bear","description":"A bear.","icon":"","price":5,"owned":[],"cannot":[]},
                   {"id":404,"category":"blizzcon","section":"BlizzCon 2026","name":"Rabbit'ath","description":"A rabbit.","icon":"","price":1,"owned":[],"cannot":[]},
                   {"id":501,"category":"recruit","section":"Recruit-A-Friend 2008","name":"Swift Zhevra","description":"A zhevra.","icon":"","price":0,"owned":[],"cannot":[]}],
         "cached_at":1760000000}
        """;

    private static async Task ShopChecks(Action<string, bool> check)
    {
        var api = new Uri("https://portal.test/api/launcher/");
        var session = new LauncherSession(FakeShopPortal.Token, "ALICE", DateTimeOffset.UtcNow.AddDays(30));
        CoinShopService Service(FakeShopPortal portal) => new(api, new HttpClient(portal));
        // Pictures come from the stand-in too (it has none: 404), into a folder of their own, never the real one.
        var previewDir = Path.Combine(Path.GetTempPath(), "portalkeeper-shop-previews-" + Guid.NewGuid().ToString("N")[..8]);
        ShopViewModel Shop(FakeShopPortal portal) => new(Service(portal), session, new ShopPreviewService(previewDir, new HttpClient(portal)));

        // The listing.
        var portal = new FakeShopPortal();
        var listing = await Service(portal).GetListingAsync(session);
        check("the shop listing is read: balance, characters, offers",
            listing is { Balance: 45, Characters.Count: 2, Offers.Count: 10 }
            && listing.Characters[1] is { Guid: 9, Name: "Bronn", Race: 3, Class: 1, Level: 12, Online: true }
            && listing.Offers[2] is { Id: 201, Category: "mount", Price: 40, Account: false }
            && listing.Offers[3] is { Id: 202, Account: true }
            && listing.Offers[0].Owned.SequenceEqual([7u]) && listing.Offers[2].Cannot.SequenceEqual([9u])
            && listing.CachedAt == DateTimeOffset.FromUnixTimeSeconds(1760000000));
        check("the listing is asked for with the launcher login", portal.Authorized == 1 && portal.Unauthorized == 0);
        check("an unknown category is kept, lower-cased", listing?.Offers[4].Category == "toy");
        check("an offer's section is read, and an offer without one has none",
            listing?.Offers[5].Section == "BlizzCon 2005" && listing?.Offers[0].Section == "");

        // BLIZZCON: a heading per section, in the realm's order whatever the sort does inside them.
        {
            using var sections = Shop(new FakeShopPortal());
            await sections.LoadAsync();
            sections.SelectedCategory = sections.Categories.First(c => c.Key == "blizzcon");
            string Shape() => string.Join("; ", sections.OfferSections.Select(s => s.Title + ": " + string.Join(",", s.Offers.Select(o => o.Offer.Id))));
            check("a tab's offers are listed under their sections, in the realm's order",
                Shape() == "BlizzCon 2005: 401; BlizzCon 2008: 402,403; BlizzCon 2026: 404");
            check("the BLIZZCON and RECRUIT-A-FRIEND tabs come before SERVICES",
                string.Join(",", sections.Categories.Select(c => c.Title).Take(5)) == "MOUNTS,COMPANIONS,BLIZZCON,RECRUIT-A-FRIEND,SERVICES");
            sections.SelectedSort = sections.Sorts.First(s => s.Key == "price-asc");
            check("a sort orders the tiles inside a section and leaves the sections in order",
                Shape() == "BlizzCon 2005: 401; BlizzCon 2008: 403,402; BlizzCon 2026: 404");
            sections.SearchText = "2008";
            check("searching a year keeps that year's section", Shape() == "BlizzCon 2008: 403,402");
            sections.ClearFilters();
            sections.SelectedCategory = sections.Categories.First(c => c.Key == "mount");
            check("a tab without sections has one group and no heading",
                sections.OfferSections.Count == 1 && !sections.OfferSections[0].HasTitle && sections.OfferSections[0].Offers.Count == 2);
        }
        check("an offer's turntable picture is read",
            listing?.Offers[2].Preview is { Frames: 36 } preview && preview.Url.AbsoluteUri == "https://portal.test/realm/shop/201.webp?v=5");
        check("a picture that isn't http(s) is left out, and offers without one have none",
            listing?.Offers[1].Preview is null && listing?.Offers[0].Preview is null);

        portal.ListingJson = """{"balance":3,"characters":null,"offers":[{"id":5,"price":2}]}""";
        listing = await Service(portal).GetListingAsync(session);
        check("missing fields in the listing don't break it",
            listing is { Balance: 3, Characters.Count: 0, Offers.Count: 1 } && listing.Offers[0] is { Category: "", Name: "", Owned.Count: 0 });

        portal.Status = 404;
        check("404 means the realm has no shop", await Service(portal).GetListingAsync(session) is null);
        portal.Status = 503;
        portal.ListingJson = """{"error":"The shop can't reach the game server right now. Try again in a minute."}""";
        LauncherApiException? closed = null;
        try { await Service(portal).GetListingAsync(session); } catch (LauncherApiException ex) { closed = ex; }
        check("503 is reported with the portal's message",
            closed is { Status: HttpStatusCode.ServiceUnavailable } && closed.Message.StartsWith("The shop can't reach", StringComparison.Ordinal));
        portal.Status = 200;
        LauncherApiException? expired = null;
        try { await Service(portal).GetListingAsync(session with { Token = "stale" }); } catch (LauncherApiException ex) { expired = ex; }
        check("an expired login is reported as 401", expired?.Status == HttpStatusCode.Unauthorized);
        portal.Offline = true;
        check("an unreachable shop is reported, not taken for 'no shop'", await ThrowsAsync(() => Service(portal).GetListingAsync(session)));

        // Buying: every answer the portal can give.
        async Task<(ShopPurchaseResult Result, FakeShopPortal Portal)> Buy(int status, string json)
        {
            var shop = new FakeShopPortal { BuyStatus = status, BuyJson = json };
            return (await Service(shop).BuyAsync(session, 201, 7), shop);
        }
        var (bought, buyPortal) = await Buy(200, """{"ok":true,"balance":5,"message":"Great Red Panda is in Alys's mailbox."}""");
        check("a purchase posts the offer and character as JSON",
            buyPortal.LastBuy is { } body && body.GetProperty("offer").GetUInt32() == 201 && body.GetProperty("character").GetUInt32() == 7
            && buyPortal.LastBuyContentType == "application/json");
        check("200: bought, with the new balance and the server's message",
            bought is { Outcome: ShopPurchaseOutcome.Bought, Balance: 5, Message: "Great Red Panda is in Alys's mailbox." });
        var (refused, _) = await Buy(409, """{"ok":false,"error":"balance","message":"You need 40 coins and have 12."}""");
        check("409: refused, with the code and the server's message",
            refused is { Outcome: ShopPurchaseOutcome.Refused, Code: "balance", Message: "You need 40 coins and have 12.", Balance: null });
        var (owned, _) = await Buy(409, """{"ok":false,"error":"owned","message":"Alys has one already."}""");
        check("409 owned keeps its message", owned is { Outcome: ShopPurchaseOutcome.Refused, Code: "owned", Message: "Alys has one already." });
        var (busy, _) = await Buy(429, """{"error":"Your last purchase is still going through. Try again in a moment."}""");
        check("429: not tried, with the portal's message",
            busy is { Outcome: ShopPurchaseOutcome.NotTried } && busy.Message.StartsWith("Your last purchase", StringComparison.Ordinal));
        var (lost, _) = await Buy(502, """{"error":"The game server didn't answer. Look at your balance before you try again: the purchase may have gone through."}""");
        check("502: may have gone through, with the portal's message",
            lost is { Outcome: ShopPurchaseOutcome.Unknown } && lost.Message.StartsWith("The game server didn't answer", StringComparison.Ordinal));
        var (closedBuy, _) = await Buy(503, """{"error":"The shop is closed right now."}""");
        check("503: not tried", closedBuy is { Outcome: ShopPurchaseOutcome.NotTried, Message: "The shop is closed right now." });
        var (logout, _) = await Buy(401, """{"error":"Your login has expired. Log in again."}""");
        check("401: logged out", logout.Outcome == ShopPurchaseOutcome.LoggedOut);
        var (forbidden, _) = await Buy(403, "<html>Forbidden</html>");
        check("403 (a ban or a proxy's page) is reported, not taken for a logout", forbidden.Outcome == ShopPurchaseOutcome.NotTried);
        var (garbled, _) = await Buy(502, "<html>Bad Gateway</html>");
        check("a proxy's 502 page still counts as 'may have gone through'",
            garbled is { Outcome: ShopPurchaseOutcome.Unknown } && garbled.Message.Length > 0);
        var (noBalance, _) = await Buy(200, """{"ok":true,"message":"Bought."}""");
        check("a purchase answer without a balance doesn't claim 0", noBalance is { Outcome: ShopPurchaseOutcome.Bought, Balance: null });
        var (odd, _) = await Buy(200, """{"balance":5}""");
        check("a 200 without ok isn't taken for a purchase", odd.Outcome == ShopPurchaseOutcome.Unknown);
        var offline = new FakeShopPortal { Offline = true };
        check("no answer at all: may have gone through",
            (await Service(offline).BuyAsync(session, 201, 7)).Outcome == ShopPurchaseOutcome.Unknown);
        offline.OfflineError = HttpRequestError.ConnectionError;
        check("no connection at all: nothing was sent",
            (await Service(offline).BuyAsync(session, 201, 7)).Outcome == ShopPurchaseOutcome.NotTried);

        // Offers locked by the realm's progression: read, shown as locked, never bought.
        portal = new FakeShopPortal
        {
            ListingJson = """
                {"balance":200,
                 "characters":[{"guid":7,"name":"Alys","race":10,"class":2,"gender":1,"level":62,"online":true}],
                 "offers":[{"id":207,"category":"mount","name":"Gargantuan Grrloc","price":45,"phase":1,"locked":true,"unlock":" Defeat Ragnaros in Molten Core with any character to unlock it. ","owned":[],"cannot":[]},
                           {"id":208,"category":"mount","name":"Tuskarr Hermit Crab","price":45,"phase":1,"locked":true,"unlock":"Comes to the realm with The Burning Crusade.","owned":[7],"cannot":[]},
                           {"id":206,"category":"mount","name":"Whoofle Bramblewing","price":0,"phase":0,"locked":false,"unlock":"","owned":[],"cannot":[]}]}
                """,
        };
        listing = await Service(portal).GetListingAsync(session);
        check("a locked offer is read with what unlocks it, trimmed",
            listing?.Offers[0] is { Locked: true, Unlock: "Defeat Ragnaros in Molten Core with any character to unlock it." } && listing.Offers[2] is { Locked: false, Unlock: "" });
        using (var shop = Shop(portal))
        {
            await shop.LoadAsync();
            ShopOfferView Card(uint id) => shop.Offers.First(o => o.Offer.Id == id);
            check("a locked offer can't be bought and says what unlocks it", Card(207) is
                { IsLocked: true, CanBuy: false, StateText: "Locked", UnlockText: "Defeat Ragnaros in Molten Core with any character to unlock it." });
            check("a locked offer without a reason still says why", new ShopOfferView(new ShopOffer(1, "mount", "X", "", "", 5, [], [], Locked: true))
                .UnlockText == "None of your characters has come far enough to buy this yet.");
            check("a locked offer the character owns already shows as owned", Card(208) is { IsLocked: false, IsOwned: true, StateText: "Owned" });
            check("an open offer can be bought", Card(206) is { IsLocked: false, CanBuy: true });
            shop.AskToBuy(Card(207));
            check("BUY does nothing on a locked offer", !shop.IsConfirming && portal.Buys == 0);
        }

        // The Shop window's view model: owned / can't use per character, the balance, the confirm step.
        portal = new FakeShopPortal();
        using (var shop = Shop(portal))
        {
            await shop.LoadAsync();
            check("the shop opens on the online character", shop.SelectedCharacter?.Name == "Bronn" && shop.Balance == 45);
            check("categories: mounts, companions, the events, services, then the rest, and no ALL tab",
                string.Join(",", shop.Categories.Select(c => c.Title)) == "MOUNTS,COMPANIONS,BLIZZCON,RECRUIT-A-FRIEND,SERVICES,TOY");
            check("the shop opens on the mounts tab", shop.SelectedCategory?.Key == "mount" && shop.Offers.Count == 2 && shop.Offers[0].Offer.Id == 201);
            // An account-wide offer (a mount the realm shares): bought for the account, not the picked character.
            check("an account-wide offer says so, and still needs the coins", Card(202) is { IsAccount: true, CanBuy: true, HasState: false } && !Card(201).IsAccount);
            shop.AskToBuy(Card(202));
            check("an account-wide offer is bought for the account", shop.IsConfirming
                && shop.ConfirmText == "Buy Ashen Steed for 30 coins for your account?"
                && shop.ConfirmDetail == "You have 45 coins; 15 will be left. A horse. Every character on your account gets it.");
            shop.CancelBuy();
            ShopOfferView Any(uint id) => shop.Categories.Select(c => { shop.SelectedCategory = c; return shop.Offers.FirstOrDefault(o => o.Offer.Id == id); })
                .First(o => o is not null)!;
            ShopOfferView Card(uint id) => Any(id);
            check("an offer the character can't use can't be bought", Card(201) is { CanBuy: false, StateText: "Can't use" });
            check("an offer the character can use and afford can be bought", Card(101) is { CanBuy: true, HasState: false });
            check("an offer the wallet can't pay for can't be bought", Card(301) is { CanBuy: false, StateText: "Need 5 more" });
            shop.SelectedCharacter = shop.Characters.First(c => c.Guid == 7);
            check("a service waiting for this character is marked", Card(1) is { CanBuy: false, IsOwned: true, StateText: "Already waiting" });
            check("what another character couldn't use, this one can", Card(201).CanBuy);
            shop.SelectedCategory = shop.Categories.First(c => c.Key == "mount");
            check("a category tab shows only its offers", shop.Offers.Count == 2 && shop.Offers[0].Offer.Id == 201);
            check("the stage shows the tab's first offer", shop.SelectedOffer?.Offer.Id == 201 && shop.Offers[0].IsSelected);

            shop.AskToBuy(shop.Offers[0]);
            check("the confirm step says what's left", shop.ConfirmDetail.StartsWith("You have 45 coins; 5 will be left.", StringComparison.Ordinal));
            check("BUY asks first", shop.IsConfirming && shop.ConfirmText == "Buy Great Red Panda for 40 coins for Alys?"
                && portal.Buys == 0);
            shop.CancelBuy();
            check("cancel buys nothing", !shop.IsConfirming && portal.Buys == 0);

            // The character asked about is the one bought for, even if the picker moves meanwhile.
            portal.BuyJson = """{"ok":true,"balance":45,"message":"Bought."}""";
            shop.AskToBuy(shop.Offers[0]);
            shop.SelectedCharacter = shop.Characters.First(c => c.Guid == 9);
            await shop.ConfirmBuyAsync();
            check("the purchase goes to the character the confirm step named",
                portal.Buys == 1 && portal.LastBuy?.GetProperty("character").GetUInt32() == 7);
            shop.SelectedCharacter = shop.Characters.First(c => c.Guid == 7);

            // BUY while a listing is on its way still asks.
            portal.HoldListing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var reloading = shop.LoadAsync();
            var wasLoading = shop.IsLoading;
            shop.AskToBuy(shop.Offers[0]);
            check("BUY works while the listing reloads", wasLoading && shop.IsConfirming);
            shop.CancelBuy();
            portal.HoldListing.SetResult();
            await reloading;
            portal.HoldListing = null;

            portal.BuyJson = """{"ok":true,"balance":5,"message":"Great Red Panda is in Alys's mailbox."}""";
            portal.ListingJson = ShopListingJson.Replace("\"balance\":45", "\"balance\":5")
                .Replace("\"owned\":[],\"cannot\":[9]", "\"owned\":[7],\"cannot\":[9]");
            var listingsBefore = portal.Listings;
            shop.AskToBuy(shop.Offers[0]);
            await shop.ConfirmBuyAsync();
            check("a purchase shows the server's message and the new balance",
                shop.StatusMessage == "Great Red Panda is in Alys's mailbox." && !shop.StatusIsError && shop.Balance == 5);
            check("a purchase fetches the listing again, keeping character and tab",
                portal.Listings == listingsBefore + 1 && shop.SelectedCharacter?.Guid == 7
                && shop.SelectedCategory?.Key == "mount" && shop.Offers[0] is { IsOwned: true, CanBuy: false });

            portal.ListingJson = ShopListingJson;
            await shop.LoadAsync();
            shop.SelectedCategory = shop.Categories.First(c => c.Key == "pet");
            portal.BuyStatus = 409;
            portal.BuyJson = """{"ok":false,"error":"balance","message":"You need 10 coins and have 5."}""";
            portal.ListingJson = ShopListingJson.Replace("\"balance\":45", "\"balance\":2");
            listingsBefore = portal.Listings;
            shop.AskToBuy(shop.Offers[0]);
            await shop.ConfirmBuyAsync();
            check("a refusal shows the server's message and fetches the listing again",
                shop.StatusMessage == "You need 10 coins and have 5." && shop.StatusIsError
                && portal.Listings == listingsBefore + 1 && shop.Balance == 2);

            portal.ListingJson = ShopListingJson;
            await shop.LoadAsync();
            check("a refresh clears the last purchase's message", !shop.HasStatus && shop.Balance == 45);
            portal.BuyStatus = 429;
            portal.BuyJson = """{"error":"Your last purchase is still going through. Try again in a moment."}""";
            listingsBefore = portal.Listings;
            shop.AskToBuy(shop.Offers[0]);
            await shop.ConfirmBuyAsync();
            check("429 shows the portal's message and doesn't ask again",
                shop.StatusIsError && shop.StatusMessage.StartsWith("Your last purchase", StringComparison.Ordinal) && portal.Listings == listingsBefore);

            portal.BuyStatus = 502;
            portal.BuyJson = """{"error":"The game server didn't answer. Look at your balance before you try again: the purchase may have gone through."}""";
            listingsBefore = portal.Listings;
            shop.AskToBuy(shop.Offers[0]);
            await shop.ConfirmBuyAsync();
            check("502 shows the portal's message and fetches the listing again",
                shop.StatusIsError && shop.StatusMessage.StartsWith("The game server didn't answer", StringComparison.Ordinal)
                && portal.Listings == listingsBefore + 1);

            portal.BuyStatus = 401;
            portal.BuyJson = """{"error":"Your login has expired. Log in again."}""";
            string? expiredMessage = null;
            shop.LoginExpired += message => expiredMessage = message;
            shop.AskToBuy(shop.Offers[0]);
            await shop.ConfirmBuyAsync();
            check("401 on a purchase sends the player back to the login", expiredMessage == "Your login has expired. Log in again.");

            portal.Status = 503;
            portal.ListingJson = """{"error":"The shop is closed."}""";
            await shop.LoadAsync();
            check("a failed refresh keeps the listing up and says why", shop.ShowShop && shop.StatusIsError && shop.StatusMessage == "The shop is closed.");
        }

        // Asking again for the same card with another character names the new one.
        using (var shop = Shop(new FakeShopPortal()))
        {
            await shop.LoadAsync();
            shop.SelectedCategory = shop.Categories.First(c => c.Key == "pet");
            var pet = shop.Offers.First(o => o.Offer.Id == 101);
            shop.SelectedCharacter = shop.Characters.First(c => c.Guid == 7);
            shop.AskToBuy(pet);
            shop.SelectedCharacter = shop.Characters.First(c => c.Guid == 9);
            shop.AskToBuy(pet);
            check("asking again with another character names that character", shop.ConfirmText == "Buy Red Panda for 10 coins for Bronn?");
        }

        // A purchase that may have gone through stays on screen when the refresh after it fails.
        var flaky = new FakeShopPortal();
        using (var shop = Shop(flaky))
        {
            await shop.LoadAsync();
            shop.SelectedCategory = shop.Categories.First(c => c.Key == "pet");
            flaky.BuyStatus = 502;
            flaky.BuyJson = """{"error":"The game server didn't answer. Look at your balance before you try again: the purchase may have gone through."}""";
            flaky.Status = 503;
            flaky.ListingJson = """{"error":"The shop can't reach the game server right now. Try again in a minute."}""";
            shop.AskToBuy(shop.Offers.First(o => o.Offer.Id == 101));
            await shop.ConfirmBuyAsync();
            check("a failed refresh after a 502 keeps the 'may have gone through' warning",
                shop.StatusIsError && shop.StatusMessage.StartsWith("The game server didn't answer", StringComparison.Ordinal)
                && shop.StatusMessage.Contains("couldn't be refreshed: The shop can't reach", StringComparison.Ordinal));

            flaky.Status = 200;
            flaky.ListingJson = ShopListingJson;
            await shop.LoadAsync();
            flaky.BuyStatus = 200;
            flaky.BuyJson = """{"ok":true,"balance":35,"message":"Red Panda is in Bronn's mailbox."}""";
            flaky.Status = 503;
            flaky.ListingJson = """{"error":"The shop is closed."}""";
            shop.AskToBuy(shop.Offers.First(o => o.Offer.Id == 101));
            await shop.ConfirmBuyAsync();
            check("a failed refresh after a purchase doesn't turn it into an error",
                !shop.StatusIsError && shop.StatusMessage.StartsWith("Red Panda is in Bronn's mailbox.", StringComparison.Ordinal) && shop.Balance == 35);

            // The stage across refreshes.
            flaky.Status = 200;
            flaky.ListingJson = ShopListingJson;
            await shop.LoadAsync();
            shop.SelectedCategory = shop.Categories.First(c => c.Key == "toy");
            shop.SelectedOffer = shop.Offers.First(o => o.Offer.Id == 301);
            var before = shop.SelectedOffer;
            await shop.LoadAsync();
            check("a refresh keeps the same offer on the stage", !ReferenceEquals(shop.SelectedOffer, before)
                && shop.SelectedOffer?.Offer.Id == 301 && shop.Offers.Single(o => o.IsSelected).Offer.Id == 301);
            flaky.ListingJson = """{"balance":35,"characters":[],"offers":[]}""";
            await shop.LoadAsync();
            check("a refresh with nothing for sale empties the stage", shop is { SelectedOffer: null, HasSelectedOffer: false, StagePicture: null });
        }
        try { Directory.Delete(previewDir, true); } catch (DirectoryNotFoundException) { }

        // Sorting and filtering within a tab, on a listing with several mounts.
        portal = new FakeShopPortal { ListingJson = """
            {"balance":45,
             "characters":[{"guid":7,"name":"Alys","race":10,"class":2,"gender":1,"level":62,"online":false},
                           {"guid":9,"name":"Bronn","race":3,"class":1,"gender":0,"level":12,"online":true}],
             "offers":[{"id":101,"category":"pet","name":"Red Panda","description":"A companion.","price":10,"owned":[],"cannot":[]},
                       {"id":201,"category":"mount","name":"Great Red Panda","description":"A big panda.","price":40,"owned":[],"cannot":[9]},
                       {"id":202,"category":"mount","name":"Ashen Steed","description":"A horse.","price":60,"owned":[],"cannot":[]},
                       {"id":203,"category":"mount","name":"Bramble Owl","description":"An owl.","price":0,"owned":[7],"cannot":[]},
                       {"id":204,"category":"mount","name":"Dusty Ram","description":"A panda-coloured ram.","price":25,"owned":[],"cannot":[]}]}
            """ };
        using (var shop = Shop(portal))
        {
            await shop.LoadAsync();
            string Ids() => string.Join(",", shop.Offers.Select(o => o.Offer.Id));
            check("the mounts tab in the shop's order", shop.SelectedCategory?.Key == "mount" && Ids() == "201,202,203,204" && !shop.IsFiltered);
            shop.SelectedSort = shop.Sorts.First(s => s.Key == "price-desc");
            check("sort by price, high to low", Ids() == "202,201,204,203");
            shop.SelectedSort = shop.Sorts.First(s => s.Key == "price-asc");
            check("sort by price, low to high", Ids() == "203,204,201,202");
            shop.SelectedSort = shop.Sorts.First(s => s.Key == "name");
            check("sort by name", Ids() == "202,203,204,201");
            shop.SelectedSort = shop.Sorts[0];
            check("shop order again", Ids() == "201,202,203,204");

            shop.SearchText = "  PANDA ";
            check("the search matches names and descriptions, ignoring case and spaces around it",
                shop.IsFiltered && Ids() == "201,204" && shop.SelectedOffer?.Offer.Id == 201);
            shop.SearchText = "zzz";
            check("a search with no match says so instead of 'nothing for sale'",
                shop.HasNoOffers && shop.SelectedOffer is null && shop.NoOffersText.StartsWith("Nothing here matches", StringComparison.Ordinal));
            shop.ClearFilters();
            check("CLEAR empties the search and shows the tab again", !shop.IsFiltered && shop.SearchText == "" && Ids() == "201,202,203,204");

            check("Bronn is the picked character", shop.SelectedCharacter?.Guid == 9);
            shop.SelectedShow = shop.Shows.First(s => s.Key == "owned");
            check("show owned follows the picked character (Bronn owns nothing)", shop.IsFiltered && shop.Offers.Count == 0);
            shop.SelectedCharacter = shop.Characters.First(c => c.Guid == 7);
            check("picking another character refilters: Alys owns the owl", Ids() == "203");
            shop.SelectedShow = shop.Shows.First(s => s.Key == "unowned");
            check("show not owned", Ids() == "201,202,204");
            shop.SelectedShow = shop.Shows.First(s => s.Key == "buyable");
            check("show what can be bought now: affordable, usable, not owned", Ids() == "201,204");
            shop.SelectedCharacter = shop.Characters.First(c => c.Guid == 9);
            check("can buy now follows the character: Bronn can't ride the panda but can have the free owl", Ids() == "203,204");

            shop.SelectedSort = shop.Sorts.First(s => s.Key == "price-desc");
            await shop.LoadAsync();
            check("a refresh keeps the tab, the sort and the Show filter",
                shop.SelectedCategory?.Key == "mount" && shop.SelectedSort.Key == "price-desc" && shop.SelectedShow.Key == "buyable" && Ids() == "204,203");
            shop.SelectedCategory = shop.Categories.First(c => c.Key == "pet");
            check("the sort and filter carry over to another tab", Ids() == "101" && shop.IsFiltered);
            shop.SelectedShow = shop.Shows[0];
            check("Everything puts the tab back", !shop.IsFiltered && Ids() == "101");
        }

        using (var shop = Shop(new FakeShopPortal { Status = 503, ListingJson = """{"error":"The shop is closed."}""" }))
        {
            await shop.LoadAsync();
            check("a shop that can't open says why instead of a listing", shop.HasLoadError && !shop.ShowShop && shop.LoadError == "The shop is closed.");
        }
        using (var shop = Shop(new FakeShopPortal { ListingJson = """{"balance":0,"characters":[],"offers":[{"id":1,"category":"pet","price":1}]}""" }))
        {
            await shop.LoadAsync();
            check("an account without characters is told so, and nothing can be bought",
                shop.HasNoCharacters && shop.SelectedCharacter is null && shop.Offers.All(o => o is { CanBuy: false, StateText: "No character" }));
            check("a single category is a single tab", shop.Categories.Count == 1);
        }
        using (var shop = Shop(new FakeShopPortal { ListingJson = """
            {"balance":0,"characters":[{"guid":7,"name":"Alys","race":4,"class":11,"gender":1,"level":20,"online":false}],
             "offers":[{"id":206,"category":"mount","name":"Whoofle Bramblewing","description":"A mount.","price":0,"owned":[],"cannot":[]}]}
            """ }))
        {
            await shop.LoadAsync();
            var offer = shop.Offers.Single();
            check("a 0-coin offer says Free, without a coin", offer is { IsFree: true, ShowCoin: false, PriceNumber: "Free" });
            check("a free offer can be had with an empty wallet", offer is { CanBuy: true, StateText: "" });
            shop.AskToBuy(offer);
            check("a free offer asks to get it, not to buy it, and leaves the wallet out",
                shop.ConfirmText == "Get Whoofle Bramblewing for free for Alys?" && shop.ConfirmDetail == "A mount.");
        }
    }

    private sealed class FakeShopPortal : HttpMessageHandler
    {
        public const string Token = "shop-test-token";
        public int Status = 200;
        public string ListingJson = ShopListingJson;
        public int BuyStatus = 200;
        public string BuyJson = """{"ok":true,"balance":0,"message":"Bought."}""";
        public bool Offline;
        public HttpRequestError OfflineError = HttpRequestError.Unknown;
        /// <summary>When set, a listing isn't answered until this completes.</summary>
        public TaskCompletionSource? HoldListing;
        public int Authorized, Unauthorized, Listings, Buys;
        public JsonElement? LastBuy;
        public string? LastBuyContentType;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Offline) throw new HttpRequestException(OfflineError, "No route to host.");
            if (request.RequestUri!.AbsolutePath != "/api/launcher/shop.php") return Json(404, """{"error":"Not found."}""");
            if (request.Headers.Authorization is not { Scheme: "Bearer", Parameter: Token })
            {
                Unauthorized++;
                return Json(401, """{"error":"Your login has expired. Log in again."}""");
            }
            Authorized++;
            if (request.Method == HttpMethod.Get)
            {
                if (HoldListing is { } hold) await hold.Task.WaitAsync(cancellationToken);
                Listings++;
                return Json(Status, Status == 404 ? """{"error":"This realm has no shop."}""" : ListingJson);
            }
            Buys++;
            LastBuyContentType = request.Content?.Headers.ContentType?.MediaType;
            LastBuy = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement.Clone();
            return Json(BuyStatus, BuyJson);
        }

        private static HttpResponseMessage Json(int status, string json) =>
            new((HttpStatusCode)status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
