using System.Security.Cryptography;
using System.Text;
using Portalkeeper.Models;
using Portalkeeper.Services;
using Portalkeeper.ViewModels;

namespace Portalkeeper.RuntimeTests;

// Vaultrona fork: built-in realm, saved launcher login and the torrent sharing rules.
// Runs offline: torrents are made from fixture files and never announced anywhere reachable.
internal static partial class Program
{
    private static async Task<int> RunLauncher()
    {
        var root = Path.Combine(Path.GetTempPath(), "pk-launcher-" + Guid.NewGuid().ToString("N")[..8]);
        var failures = 0;
        void Check(string name, bool ok)
        {
            Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name);
            if (!ok) failures++;
        }

        try
        {
            Console.WriteLine("== Launcher (Vaultrona fork) ==");

            // Built-in realm.
            var store = Path.Combine(root, "realms");
            Check("seed writes the built-in realm into an empty store", RealmBranding.SeedRealmStore(store));
            var seeded = new RealmConfigurationService().Load(Path.Combine(store, RealmBranding.RealmFileName));
            Check("built-in realm parses", seeded.IsConfigured && seeded.Name == "Azeroth" && seeded.ConfigUrl.Length > 0);
            Check("seed leaves an existing store alone", !RealmBranding.SeedRealmStore(store));
            var other = Path.Combine(root, "realms-other");
            Directory.CreateDirectory(other);
            File.WriteAllText(Path.Combine(other, "mine.realm.conf"), "x");
            Check("seed never adds to a store that has another realm", !RealmBranding.SeedRealmStore(other)
                && !File.Exists(Path.Combine(other, RealmBranding.RealmFileName)));

            // realm.conf check the open launcher runs every other health tick: only a changed file refreshes.
            var storedPath = Path.Combine(store, RealmBranding.RealmFileName);
            var stored = File.ReadAllBytes(storedPath);
            var changedConf = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(stored).Replace("Name=Azeroth", "Name=Azeroth Changed"));
            var same = await new RealmConfigurationUpdateService(_ => Task.FromResult(stored)).CheckForUpdateAsync(storedPath, seeded.ConfigUrl);
            Check("unchanged realm.conf asks for no refresh", same.Status == UpdateCheckStatus.NoUpdateAvailable);
            var newer = await new RealmConfigurationUpdateService(_ => Task.FromResult(changedConf)).CheckForUpdateAsync(storedPath, seeded.ConfigUrl);
            Check("changed realm.conf asks for a refresh", newer.Status == UpdateCheckStatus.UpdateAvailable);
            var offline = await new RealmConfigurationUpdateService(_ => throw new HttpRequestException("offline")).CheckForUpdateAsync(storedPath, seeded.ConfigUrl);
            Check("unreachable realm.conf asks for no refresh and keeps the file",
                offline.Status == UpdateCheckStatus.DownloadFailure && File.ReadAllBytes(storedPath).SequenceEqual(stored));

            // Which patches have torrents.
            Check("patches-folder patch has a torrent name", RealmBranding.HostedPatchFileName(RealmBranding.PatchBaseUrl + "patch-P.MPQ") == "patch-P.MPQ");
            Check("escaped name is decoded", RealmBranding.HostedPatchFileName(RealmBranding.PatchBaseUrl + "Patch%2DH.MPQ") == "Patch-H.MPQ");
            Check("subfolders are not torrent patches", RealmBranding.HostedPatchFileName(RealmBranding.PatchBaseUrl + "armory/x.MPQ") is null);
            Check("patches outside the patches folder are not torrent patches", RealmBranding.HostedPatchFileName(RealmBranding.RealmBaseUrl + "patch-P.MPQ") is null);
            Check("other hosts are not torrent patches", RealmBranding.HostedPatchFileName("https://example.com/realm/patch-P.MPQ") is null);
            Check("traversal is not a torrent patch", RealmBranding.HostedPatchFileName(RealmBranding.PatchBaseUrl + "..") is null);

            // Retired realm patches: removed only while still ours, the client file they replaced restored.
            await RetiredPatchChecks(root, Check);
            LongPatchNameChecks(root, Check);
            // More than 10 characters per realm, and their order.
            CharacterListLimitChecks(root, Check);
            // The warband camp's character select edits.
            WarbandSelectChecks(root, Check);
            GuildBankSlotsChecks(root, Check);
            LiveBagSlotsChecks(root, Check);
            M2CacheFullPathsChecks(root, Check);
            MoonPhasesChecks(root, Check);
            await CharacterOrderChecks(root, Check);

            // Saved login.
            var sessionPath = Path.Combine(root, "session", "launcher-session.dat");
            var sessions = new LauncherSessionStore(sessionPath);
            Check("no saved login at first", sessions.Load() is null);
            var session = new LauncherSession("token-" + Guid.NewGuid().ToString("N"), "ALICE", DateTimeOffset.UtcNow.AddDays(30));
            sessions.Save(session);
            Check("saved login round trips", sessions.Load() == session);
            Check("saved login doesn't contain the token in clear on Windows",
                !OperatingSystem.IsWindows() || !File.ReadAllText(sessionPath).Contains(session.Token, StringComparison.Ordinal));
            if (!OperatingSystem.IsWindows())
                Check("saved login is owner-only", (File.GetUnixFileMode(sessionPath) & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)) == 0);
            var withPassword = session with { GamePassword = "hunter2" };
            sessions.Save(withPassword);
            Check("saved login keeps the game password", sessions.Load()?.GamePassword == "hunter2");
            Check("saved login doesn't contain the game password in clear on Windows",
                !OperatingSystem.IsWindows() || !File.ReadAllText(sessionPath).Contains("hunter2", StringComparison.Ordinal));
            File.WriteAllText(sessionPath, "not json");
            Check("damaged saved login is ignored", sessions.Load() is null);
            sessions.Clear();
            Check("logout clears the saved login", !File.Exists(sessionPath));

            // Game password handed to the login patch through Config.wtf.
            var prefix = RealmLaunchService.GamePasswordPrefix;
            var config = "SET locale \"enUS\"\r\nSET accountList \"" + prefix + "hunter2\"\r\nSET accountName \"ALICE\"\r\n";
            Check("password line is blanked", RealmLaunchService.WithoutGamePassword(config)
                == "SET locale \"enUS\"\r\nSET accountList \"\"\r\nSET accountName \"ALICE\"\r\n");
            Check("an account list that isn't ours is kept",
                RealmLaunchService.WithoutGamePassword("SET accountList \"!ALICE|BOB|\"\n") == "SET accountList \"!ALICE|BOB|\"\n");
            var wtfClient = Path.Combine(root, "wtf-client");
            Directory.CreateDirectory(Path.Combine(wtfClient, "WTF"));
            var wtf = Path.Combine(wtfClient, "WTF", "Config.wtf");
            File.WriteAllText(wtf, config, new UTF8Encoding(true));
            RealmLaunchService.ForgetGamePassword(wtfClient);
            var forgotten = File.ReadAllBytes(wtf);
            Check("forgetting the password leaves no password", !Encoding.UTF8.GetString(forgotten).Contains("hunter2", StringComparison.Ordinal));
            Check("forgetting the password keeps the rest and the BOM", forgotten.AsSpan().StartsWith(Encoding.UTF8.GetPreamble())
                && Encoding.UTF8.GetString(forgotten).Contains("SET accountName \"ALICE\"", StringComparison.Ordinal));
            RealmLaunchService.ForgetGamePassword(Path.Combine(root, "no-such-client"));
            Check("forgetting without a Config.wtf does nothing", true);

            // Out of date addons load and mob tooltips show quest progress, whatever reset Config.wtf.
            File.WriteAllText(wtf, "SET locale \"enUS\"\r\nSET checkAddonVersion \"1\"\r\n");
            RealmLaunchService.WriteConfigSettings(wtf, RealmLaunchService.ClientSettings, wtfClient);
            var kept = File.ReadAllText(wtf);
            Check("out of date addons are turned back on", kept.Contains("SET checkAddonVersion \"0\"\r\n", StringComparison.Ordinal)
                && !kept.Contains("checkAddonVersion \"1\"", StringComparison.Ordinal));
            Check("quest tooltips are added when missing", kept.EndsWith("SET showQuestTrackingTooltips \"1\"\r\n", StringComparison.Ordinal)
                && kept.StartsWith("SET locale \"enUS\"\r\n", StringComparison.Ordinal));
            var backups = Path.Combine(wtfClient, ".portalkeeper", "backups", "config");
            var backupCount = Directory.Exists(backups) ? Directory.GetFiles(backups).Length : 0;
            RealmLaunchService.WriteConfigSettings(wtf, RealmLaunchService.ClientSettings, wtfClient);
            Check("settings already in place leave Config.wtf and its backups alone", File.ReadAllText(wtf) == kept
                && (Directory.Exists(backups) ? Directory.GetFiles(backups).Length : 0) == backupCount);
            var fresh = Path.Combine(root, "wtf-fresh");
            RealmLaunchService.WriteConfigSettings(Path.Combine(fresh, "WTF", "Config.wtf"), RealmLaunchService.ClientSettings, fresh);
            Check("a client without Config.wtf gets both settings", File.ReadAllText(Path.Combine(fresh, "WTF", "Config.wtf"))
                .Contains("SET showQuestTrackingTooltips \"1\"", StringComparison.Ordinal));

            // View Distance goes to its top once per client; what the player picks afterwards stays.
            var farClient = Path.Combine(root, "farclip-client");
            Directory.CreateDirectory(Path.Combine(farClient, "WTF"));
            var farWtf = Path.Combine(farClient, "WTF", "Config.wtf");
            File.WriteAllText(farWtf, "SET locale \"enUS\"\r\nSET farclip \"477\"\r\n");
            void LaunchSettings(string client = "", bool builtIn = true) =>
                RealmLaunchService.WriteLaunchSettings(client.Length > 0 ? client : farClient,
                    RealmLaunchService.ClientSettings.ToList(), builtIn);
            Check("a new client has farclip to raise", RealmLaunchService.PendingRaises(farClient).Any(s => s.Name == "farclip" && s.Value == "777"));
            LaunchSettings();
            Check("a lower farclip is raised to 777 in place", File.ReadAllText(farWtf).Contains("SET farclip \"777\"\r\n", StringComparison.Ordinal)
                && !File.ReadAllText(farWtf).Contains("477", StringComparison.Ordinal));
            Check("the raise is remembered for the client", RealmLaunchService.PendingRaises(farClient).Count == 0
                && File.ReadAllText(Path.Combine(farClient, ".portalkeeper", "raised-settings.txt")).Contains("farclip=777", StringComparison.Ordinal));
            File.WriteAllText(farWtf, "SET locale \"enUS\"\r\nSET farclip \"300\"\r\nSET checkAddonVersion \"0\"\r\nSET showQuestTrackingTooltips \"1\"\r\n");
            LaunchSettings();
            Check("a farclip the player lowers afterwards stays", RealmLaunchService.ReadConfigSetting(farClient, "farclip") == "300");
            Check("a farclip at or above the value isn't raised", !RealmLaunchService.NeedsRaise("777", "777")
                && !RealmLaunchService.NeedsRaise("1277", "777") && RealmLaunchService.NeedsRaise("776.5", "777"));
            Check("a missing or odd farclip is raised", RealmLaunchService.NeedsRaise(null, "777") && RealmLaunchService.NeedsRaise("", "777")
                && RealmLaunchService.NeedsRaise("far", "777"));
            var farFresh = Path.Combine(root, "farclip-fresh");
            Directory.CreateDirectory(farFresh);
            LaunchSettings(farFresh);
            Check("a client without Config.wtf gets farclip 777", RealmLaunchService.ReadConfigSetting(farFresh, "farclip") == "777");
            Check("a new client's raise waits for the game's first Config.wtf", RealmLaunchService.PendingRaises(farFresh).Count == 1);
            LaunchSettings(farFresh);
            Check("a Config.wtf only the launcher wrote (the game never saved) still waits", RealmLaunchService.PendingRaises(farFresh).Count == 1);
            File.WriteAllText(Path.Combine(farFresh, "WTF", "Config.wtf"), "SET hwDetect \"0\"\r\nSET farclip \"507\"\r\n");
            LaunchSettings(farFresh);
            Check("a farclip the first start's hardware detection lowered is raised again", RealmLaunchService.ReadConfigSetting(farFresh, "farclip") == "777"
                && RealmLaunchService.PendingRaises(farFresh).Count == 0);
            var farOther = Path.Combine(root, "farclip-other-realm");
            Directory.CreateDirectory(Path.Combine(farOther, "WTF"));
            File.WriteAllText(Path.Combine(farOther, "WTF", "Config.wtf"), "SET farclip \"400\"\r\n");
            LaunchSettings(farOther, builtIn: false);
            Check("another realm's client keeps its farclip", RealmLaunchService.ReadConfigSetting(farOther, "farclip") == "400"
                && !File.Exists(Path.Combine(farOther, ".portalkeeper", "raised-settings.txt")));
            // A .portalkeeper the backup can't go into: the launch goes on without the raise.
            var farLocked = Path.Combine(root, "farclip-no-backup");
            Directory.CreateDirectory(Path.Combine(farLocked, "WTF"));
            Directory.CreateDirectory(Path.Combine(farLocked, ".portalkeeper"));
            File.WriteAllText(Path.Combine(farLocked, ".portalkeeper", "backups"), "not a folder");
            File.WriteAllText(Path.Combine(farLocked, "WTF", "Config.wtf"),
                "SET gxApi \"d3d9\"\r\nSET farclip \"400\"\r\nSET checkAddonVersion \"0\"\r\nSET showQuestTrackingTooltips \"1\"\r\n");
            var lockedLaunched = true;
            try { LaunchSettings(farLocked); } catch { lockedLaunched = false; }
            Check("a backup that can't be written drops the raise, not the launch", lockedLaunched
                && RealmLaunchService.ReadConfigSetting(farLocked, "farclip") == "400"
                && RealmLaunchService.PendingRaises(farLocked).Count == 1);
            Check("the game's own lines tell its Config.wtf from the launcher's", RealmLaunchService.GameSavedConfig("SET locale \"enUS\"\n")
                && !RealmLaunchService.GameSavedConfig("SET checkAddonVersion \"0\"\nSET farclip \"777\"\nSET accountName \"A\"\nSET readEULA \"1\"\n"));

            // Client files the realm dropped from its client torrent (TheraWoW's login screen).
            var removeClient = Path.Combine(root, "remove", "WoW");
            Directory.CreateDirectory(Path.Combine(removeClient, "Data", "enUS"));
            File.WriteAllBytes(Path.Combine(removeClient, "Wow.exe"), RandomNumberGenerator.GetBytes(40_000));
            var glue = RandomNumberGenerator.GetBytes(50_000);
            var gluePath = Path.Combine(removeClient, "Data", "enUS", "patch-enUS-4.MPQ");
            File.WriteAllBytes(gluePath, glue);
            var glueEntry = new RetiredPatchService.Entry
            {
                Path = "Data/enUS/patch-enUS-4.MPQ", Size = glue.Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(glue)).ToLowerInvariant()
            };
            var withGlue = FixtureTorrent(removeClient, 16_384);
            Check("a file the client torrent still carries is kept",
                RetiredPatchService.RemoveClientFiles(removeClient, [glueEntry], withGlue) == 0 && File.Exists(gluePath));
            File.Move(gluePath, gluePath + ".aside");
            var withoutGlue = FixtureTorrent(removeClient, 16_384);
            File.Move(gluePath + ".aside", gluePath);
            var changedGlue = (byte[])glue.Clone();
            changedGlue[10] ^= 0xFF;
            File.WriteAllBytes(gluePath, changedGlue);
            Check("a changed copy is the player's and is kept",
                RetiredPatchService.RemoveClientFiles(removeClient, [glueEntry], withoutGlue) == 0 && File.Exists(gluePath));
            File.WriteAllBytes(gluePath, glue);
            Check("the shipped file is removed once the torrent drops it",
                RetiredPatchService.RemoveClientFiles(removeClient, [glueEntry], withoutGlue) == 1 && !File.Exists(gluePath));
            Check("nothing is put back", !File.Exists(gluePath) && RetiredPatchService.RemoveClientFiles(removeClient, [glueEntry], withoutGlue) == 0);

            // The client is held to exactly what the realm ships; addons and settings are the player's.
            ClientConformanceChecks(root, Check);
            await ClientBaseDownloadChecks(root, Check);

            // INSTALL WOW brings the player's own addons and settings from the old client.
            var oldClient = Path.Combine(root, "import", "Old WoW");
            var newClient = Path.Combine(root, "import", "Evermore");
            var oldAddOns = Path.Combine(oldClient, "Interface", "AddOns");
            void Write(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); }
            Write(Path.Combine(oldAddOns, "MyAddon", "MyAddon.toc"), "## Title: Mine");
            Write(Path.Combine(oldAddOns, "MyAddon", "Libs", "Lib.lua"), "lib");
            Write(Path.Combine(oldAddOns, "Shared", "Shared.toc"), "old");
            Write(Path.Combine(oldAddOns, "Blizzard_Old", "Blizzard_Old.toc"), "x");
            Write(Path.Combine(oldAddOns, "NotAnAddon", "readme.txt"), "x");
            Write(Path.Combine(oldClient, "WTF", "Config.wtf"), "SET gxApi \"d3d9\"");
            Write(Path.Combine(oldClient, "WTF", "Account", "ALICE", "SavedVariables", "MyAddon.lua"), "saved");
            Write(Path.Combine(oldClient, "WTF", "Account", "ALICE", "bindings-cache.wtf"), "old bindings");
            Write(Path.Combine(newClient, "Interface", "AddOns", "Shared", "Shared.toc"), "new");
            Write(Path.Combine(newClient, "WTF", "Account", "ALICE", "bindings-cache.wtf"), "new bindings");
            Write(Path.Combine(oldAddOns, "DragonUI", "DragonUI.toc"), "## Title: DragonUI\n## OptionalDeps: Blizzard_TimeManager");
            Write(Path.Combine(oldAddOns, "DragonUI_Options", "DragonUI_Options.toc"), "## Dependencies: DragonUI");
            Write(Path.Combine(oldAddOns, "DragonUI_NewEra", "DragonUI_NewEra.toc"), "## Dependencies: DragonUI, !!!ClassicAPI");
            Write(Path.Combine(oldAddOns, "NewEraPlugin", "NewEraPlugin.toc"), "## RequiredDeps: DragonUI_NewEra");
            Write(Path.Combine(oldAddOns, "!!!ClassicAPI", "!!!ClassicAPI.toc"), "## Title: ClassicAPI");
            var outside = Path.Combine(root, "import", "outside");
            Write(Path.Combine(outside, "Linked.toc"), "x");
            Directory.CreateSymbolicLink(Path.Combine(oldAddOns, "Linked"), outside);
            var imported = ClientImportService.Import(oldClient, newClient, ["DragonUI", "Auctionator"]);
            var newAddOns = Path.Combine(newClient, "Interface", "AddOns");
            Check("player's addon is copied whole", File.Exists(Path.Combine(newAddOns, "MyAddon", "Libs", "Lib.lua")));
            Check("an addon the new client has is not overwritten", File.ReadAllText(Path.Combine(newAddOns, "Shared", "Shared.toc")) == "new");
            Check("Blizzard_ folders, folders without a .toc and links are left out",
                !Directory.Exists(Path.Combine(newAddOns, "Blizzard_Old")) && !Directory.Exists(Path.Combine(newAddOns, "NotAnAddon"))
                && !Directory.Exists(Path.Combine(newAddOns, "Linked")));
            Check("an addon the realm offers is left to the realm", !Directory.Exists(Path.Combine(newAddOns, "DragonUI")));
            Check("addons that need it are left out too, all the way down",
                !Directory.Exists(Path.Combine(newAddOns, "DragonUI_Options")) && !Directory.Exists(Path.Combine(newAddOns, "DragonUI_NewEra"))
                && !Directory.Exists(Path.Combine(newAddOns, "NewEraPlugin")));
            Check("an unrelated library still comes along", Directory.Exists(Path.Combine(newAddOns, "!!!ClassicAPI")));
            Check("SavedVariables are copied", File.ReadAllText(Path.Combine(newClient, "WTF", "Account", "ALICE", "SavedVariables", "MyAddon.lua")) == "saved");
            Check("settings the new client has are not overwritten", File.ReadAllText(Path.Combine(newClient, "WTF", "Account", "ALICE", "bindings-cache.wtf")) == "new bindings");
            Check("Config.wtf is not copied", !File.Exists(Path.Combine(newClient, "WTF", "Config.wtf")));
            Check("the old client is left as it was", File.ReadAllText(Path.Combine(oldAddOns, "Shared", "Shared.toc")) == "old"
                && File.Exists(Path.Combine(oldAddOns, "MyAddon", "MyAddon.toc")));
            Check("import counts", imported == new ClientImportResult(2, 1, 0));
            Check("importing again copies nothing", ClientImportService.Import(oldClient, newClient, ["DragonUI", "Auctionator"]) == new ClientImportResult(0, 0, 0));
            Check("no import from inside the new client", ClientImportService.Import(newAddOns, newClient) == new ClientImportResult(0, 0, 0));
            Check("no import without an old client", ClientImportService.Import("", newClient) == new ClientImportResult(0, 0, 0));

            // Sharing rules.
            var client = Path.Combine(root, "client", "WoW");
            Directory.CreateDirectory(Path.Combine(client, "Data"));
            File.WriteAllBytes(Path.Combine(client, "Wow.exe"), RandomNumberGenerator.GetBytes(70_000));
            File.WriteAllBytes(Path.Combine(client, "Data", "common.MPQ"), RandomNumberGenerator.GetBytes(300_000));
            var torrent = FixtureTorrent(client, 32_768);
            Check("torrent name comes from the folder", TorrentService.NameOf(torrent) == "WoW");

            var cache = Path.Combine(root, "torrent-cache");
            var sharing = new TorrentService(cache);
            await sharing.ConfigureAsync(false, 0, 47391);
            Check("nothing is shared while sharing is off", !await sharing.ShareAsync(torrent, client));
            await sharing.ConfigureAsync(true, 0, 47391);
            Check("an exact copy is shared", await sharing.ShareAsync(torrent, client));
            Check("summary counts it", (await sharing.SummaryAsync(TorrentService.InfoHashOf(torrent))) is { Shared: 1, ClientShared: true, PatchesShared: 0 });
            await sharing.StopSharingExceptAsync(Array.Empty<string>());
            Check("stop sharing removes it", (await sharing.SummaryAsync()).Shared == 0);

            var modified = Path.Combine(root, "modified", "WoW");
            Directory.CreateDirectory(Path.Combine(modified, "Data"));
            File.Copy(Path.Combine(client, "Wow.exe"), Path.Combine(modified, "Wow.exe"));
            var changed = File.ReadAllBytes(Path.Combine(client, "Data", "common.MPQ"));
            changed[1234] ^= 0xFF;
            File.WriteAllBytes(Path.Combine(modified, "Data", "common.MPQ"), changed);
            Check("a modified copy is not shared", !await sharing.ShareAsync(torrent, modified));
            Check("a modified copy is left untouched", File.ReadAllBytes(Path.Combine(modified, "Data", "common.MPQ")).SequenceEqual(changed));

            var partial = Path.Combine(root, "partial", "WoW");
            Directory.CreateDirectory(partial);
            File.Copy(Path.Combine(client, "Wow.exe"), Path.Combine(partial, "Wow.exe"));
            Check("an incomplete copy is not shared", !await sharing.ShareAsync(torrent, partial));
            Check("nothing is downloaded into an incomplete copy", !Directory.Exists(Path.Combine(partial, "Data")));

            Check("the copy is shared again", await sharing.ShareAsync(torrent, client));
            await sharing.ShutdownAsync();
            var hash = TorrentService.InfoHashOf(torrent);
            Check("shutdown saves resume data", File.Exists(Path.Combine(cache, "resume", hash + ".fresume")));

            var restarted = new TorrentService(cache);
            await restarted.ConfigureAsync(true, 0, 47391);
            Check("restart shares from resume data", await restarted.ShareAsync(torrent, client));
            await restarted.ShutdownAsync();

            // Discarding a download only ever deletes files in the folder it was downloading into.
            var copy = Path.Combine(root, "copy", "WoW");
            Directory.CreateDirectory(Path.Combine(copy, "Data"));
            File.Copy(Path.Combine(client, "Wow.exe"), Path.Combine(copy, "Wow.exe"));
            File.Copy(Path.Combine(client, "Data", "common.MPQ"), Path.Combine(copy, "Data", "common.MPQ"));
            var discard = new TorrentService(Path.Combine(root, "torrent-cache-discard"));
            await discard.ConfigureAsync(true, 0, 47392);
            Check("copy shared before discard", await discard.ShareAsync(torrent, copy));
            await discard.DiscardAsync(torrent, Path.Combine(root, "somewhere-else"));
            Check("discard with another folder keeps the files", File.Exists(Path.Combine(copy, "Data", "common.MPQ")) && (await discard.SummaryAsync()).Shared == 1);
            await discard.DiscardAsync(torrent, copy);
            Check("discard removes the torrent and its files", !File.Exists(Path.Combine(copy, "Data", "common.MPQ")) && (await discard.SummaryAsync()).Shared == 0);
            await discard.PauseAllAsync();
            await discard.ResumeAllAsync();
            Check("pause/resume with nothing running is harmless", (await discard.SummaryAsync()).Shared == 0);
            await discard.ShutdownAsync();

            // Same size and timestamp but different bytes can't be caught by the stamp check, so a
            // changed timestamp is what forces the re-hash: prove a re-hash then refuses the copy.
            var mpq = Path.Combine(client, "Data", "common.MPQ");
            var original = File.ReadAllBytes(mpq);
            var tampered = (byte[])original.Clone();
            tampered[99] ^= 0xFF;
            File.WriteAllBytes(mpq, tampered);
            var rehash = new TorrentService(cache);
            await rehash.ConfigureAsync(true, 0, 47391);
            Check("a changed file forces a re-hash and is not shared", !await rehash.ShareAsync(torrent, client));
            Check("the changed file is left untouched", File.ReadAllBytes(mpq).SequenceEqual(tampered));
            await rehash.ShutdownAsync();

            // Self-update: the installer is only kept when it matches GitHub's published SHA-256.
            var payload = RandomNumberGenerator.GetBytes(50_000);
            var payloadHash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
            string ReleaseJson(string? digest) => "{\"draft\":false,\"prerelease\":false,\"assets\":[{\"name\":\"Portalkeeper-Setup-9.9.9.exe\",\"size\":"
                + payload.Length + (digest is null ? "" : ",\"digest\":\"" + digest + "\"") + "}]}";
            var downloads = 0;
            var apiCalls = 0;
            byte[] served = payload;
            string? checksumFile = null; // the release's .sha256 asset; null = an older release without one
            var updates = Path.Combine(root, "updates");
            PortalkeeperUpdateService Updater(string? digest) => new(
                new HttpClient(new FakeHandler(_ => { apiCalls++; return new System.Net.Http.StringContent(ReleaseJson(digest)); })),
                new HttpClient(new FakeHandler(request =>
                {
                    if (!request.RequestUri!.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal))
                    {
                        downloads++;
                        return new System.Net.Http.ByteArrayContent(served);
                    }
                    return checksumFile is null ? null : new System.Net.Http.StringContent(checksumFile);
                })),
                updates);
            var installer = await Updater("sha256:" + payloadHash).DownloadInstallerAsync("v9.9.9");
            Check("verified installer is kept", File.ReadAllBytes(installer).SequenceEqual(payload) && Path.GetFileName(installer) == "Portalkeeper-Setup-9.9.9.exe");
            await Updater("sha256:" + payloadHash).DownloadInstallerAsync("v9.9.9");
            Check("a verified installer isn't downloaded twice", downloads == 1);
            File.Delete(installer);
            served = (byte[])payload.Clone();
            served[10] ^= 0xFF;
            Check("a tampered installer is refused", await ThrowsAsync(() => Updater("sha256:" + payloadHash).DownloadInstallerAsync("v9.9.9")));
            Check("nothing is left behind after a refused download", !Directory.EnumerateFiles(updates).Any());
            Check("a release without a checksum is refused", await ThrowsAsync(() => Updater(null).DownloadInstallerAsync("v9.9.9")));
            Check("a non-release tag is refused", await ThrowsAsync(() => Updater("sha256:" + payloadHash).DownloadInstallerAsync("main")));

            // A release's .sha256 file is used before GitHub's API, whose hourly limit addon checks can use up.
            served = payload;
            checksumFile = payloadHash.ToUpperInvariant() + "  Portalkeeper-Setup-9.9.9.exe\n";
            apiCalls = 0;
            installer = await Updater(null).DownloadInstallerAsync("v9.9.9");
            Check("the release's checksum file verifies the installer without the API",
                File.ReadAllBytes(installer).SequenceEqual(payload) && apiCalls == 0);
            File.Delete(installer);
            served = (byte[])payload.Clone();
            served[10] ^= 0xFF;
            Check("the checksum file refuses a tampered installer", await ThrowsAsync(() => Updater(null).DownloadInstallerAsync("v9.9.9")));
            checksumFile = "not a checksum";
            Check("a malformed checksum file is refused", await ThrowsAsync(() => Updater("sha256:" + payloadHash).DownloadInstallerAsync("v9.9.9")));
            Check("nothing is left behind after the checksum file refusals", !Directory.EnumerateFiles(updates).Any());
            checksumFile = null;
            Check("this test build can't update itself in place", !PortalkeeperUpdateService.CanUpdateInPlace);

            // Release checks ask the portal first; GitHub only when the portal can't say.
            var portalUri = new Uri("https://portal.test/api/launcher/version.php");
            var githubAsked = 0;
            async Task<ReleaseCheckResult> ReleaseCheck(string portalBody, PortalkeeperSettings settings)
            {
                var releases = new PortalkeeperReleaseService(new HttpClient(new FakeHandler(request =>
                {
                    if (request.RequestUri == portalUri) return new StringContent(portalBody);
                    githubAsked++;
                    return new StringContent("""{"tag_name":"v2.0.0","draft":false,"prerelease":false}""");
                })), () => DateTimeOffset.UtcNow, portalUri);
                return await releases.CheckAsync(settings, "1.0.0", manual: false, _ => { });
            }
            var releaseSettings = new PortalkeeperSettings { LastPortalkeeperUpdateCheckUtc = DateTimeOffset.UtcNow };
            var fromPortal = await ReleaseCheck("""{"tag":"v1.2.0","version":"1.2.0"}""", releaseSettings);
            Check("the portal's release is used without asking GitHub", fromPortal.State == ReleaseCheckState.Available
                && fromPortal.Tag == "v1.2.0" && githubAsked == 0 && releaseSettings.LatestPortalkeeperReleaseTag == "v1.2.0");
            Check("the portal is asked again right away", (await ReleaseCheck("""{"tag":"v1.0.0"}""", releaseSettings)).State == ReleaseCheckState.UpToDate);
            Check("a portal tag that isn't a stable release falls back without asking GitHub inside 6 hours",
                (await ReleaseCheck("""{"tag":"main"}""", releaseSettings)).State == ReleaseCheckState.UpToDate && githubAsked == 0);
            var fallback = await ReleaseCheck("not json", new PortalkeeperSettings());
            Check("without a portal answer GitHub is asked", fallback.Tag == "v2.0.0" && githubAsked == 1);

            // Combat logs shared with the realm: the launcher's own addon and the upload.
            await CombatLogChecks(root, Check);

            // Installed optional addons updated in the background.
            await AddonUpdateChecks(root, Check);

            // The coin shop.
            await ShopChecks(Check);

            // Legacy: points from challenges, perk trees per character.
            await LegacyChecks(Check);

            // REALM PATCHES: which non-Required patches a player keeps.
            var requiredPatch = new PatchDefinition { Id = "Required", Requirement = ComponentRequirement.Required };
            var recommendedPatch = new PatchDefinition { Id = "RetailTrees", Requirement = ComponentRequirement.Recommended };
            var optionalPatch = new PatchDefinition { Id = "Extra", Requirement = ComponentRequirement.Optional };
            Check("untouched: Required and Recommended patches install, Optional ones don't",
                MainViewModel.IsPatchWanted(requiredPatch, null, false) && MainViewModel.IsPatchWanted(recommendedPatch, null, false)
                && !MainViewModel.IsPatchWanted(optionalPatch, null, false));
            Check("untouched: an Optional patch already in the client is kept", MainViewModel.IsPatchWanted(optionalPatch, null, true));
            var turned = new Dictionary<string, bool> { ["Required"] = false, ["RetailTrees"] = false, ["Extra"] = true };
            Check("a Recommended patch can be turned off and an Optional one on",
                !MainViewModel.IsPatchWanted(recommendedPatch, turned, true) && MainViewModel.IsPatchWanted(optionalPatch, turned, false));
            Check("a Required patch can't be turned off", MainViewModel.IsPatchWanted(requiredPatch, turned, true));
            Check("an Optional patch turned off goes even when it's in the client",
                !MainViewModel.IsPatchWanted(optionalPatch, new Dictionary<string, bool> { ["Extra"] = false }, true));
            Check("a choice for another patch changes nothing",
                MainViewModel.IsPatchWanted(recommendedPatch, new Dictionary<string, bool> { ["Other"] = false }, false));
            var choiceSettings = Path.Combine(root, "choice-settings", "settings.json");
            var choiceStore = new SettingsService(choiceSettings);
            var withChoice = new PortalkeeperSettings();
            withChoice.PatchChoicesByRealm["REALM"] = new Dictionary<string, bool> { ["RetailTrees"] = false };
            choiceStore.Save(withChoice);
            Check("patch choices are saved with the settings",
                choiceStore.Load().PatchChoicesByRealm.TryGetValue("REALM", out var savedChoices) && savedChoices["RetailTrees"] == false);
            File.WriteAllText(choiceSettings, "{\"ClientPath\":\"\"}");
            Check("settings from before patch choices load with none", choiceStore.Load().PatchChoicesByRealm.Count == 0);
            File.WriteAllText(choiceSettings, "{\"PatchChoicesByRealm\":null}");
            Check("null patch choices in the settings load as none", choiceStore.Load().PatchChoicesByRealm is { Count: 0 });

            // Crash reports and the home screen's live info and patch notes.
            await HomeAndCrashChecks(root, Check);
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
            failures++;
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (Exception) { }
        }

        Console.WriteLine(failures == 0 ? "Launcher: all checks passed." : $"Launcher: {failures} check(s) failed.");
        return failures == 0 ? 0 : 1;
    }

    private static async Task<bool> ThrowsAsync(Func<Task> action)
    {
        try { await action(); return false; }
        catch (Exception) { return true; }
    }

    /// <summary>Answers with <paramref name="respond"/>'s content, or 404 when it returns null.</summary>
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpContent?> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request) is { } content
                ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content }
                : new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
    }

    /// <summary>A private multi-file torrent of <paramref name="folder"/>, named after it.</summary>
    private static byte[] FixtureTorrent(string folder, int pieceLength, string? webSeed = null)
    {
        var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .OrderBy(p => Path.GetRelativePath(folder, p).Replace('\\', '/'), StringComparer.Ordinal).ToArray();
        using var data = new MemoryStream();
        foreach (var file in files)
            data.Write(File.ReadAllBytes(file));
        var all = data.ToArray();
        using var pieces = new MemoryStream();
        for (var offset = 0; offset < all.Length; offset += pieceLength)
            pieces.Write(SHA1.HashData(all.AsSpan(offset, Math.Min(pieceLength, all.Length - offset))));

        var fileList = files.Select(f => (object)new SortedDictionary<string, object>(StringComparer.Ordinal)
        {
            ["length"] = new FileInfo(f).Length,
            ["path"] = Path.GetRelativePath(folder, f).Split(Path.DirectorySeparatorChar).Cast<object>().ToList(),
        }).ToList();
        var info = new SortedDictionary<string, object>(StringComparer.Ordinal)
        {
            ["files"] = fileList,
            ["name"] = Path.GetFileName(folder),
            ["piece length"] = (long)pieceLength,
            ["pieces"] = pieces.ToArray(),
            ["private"] = 1L,
        };
        var torrent = new SortedDictionary<string, object>(StringComparer.Ordinal)
        {
            ["announce"] = "http://127.0.0.1:9/announce",
            ["info"] = info,
        };
        if (webSeed is not null) torrent["url-list"] = new List<object> { webSeed };
        using var output = new MemoryStream();
        Bencode(output, torrent);
        return output.ToArray();
    }

    private static void Bencode(Stream output, object value)
    {
        void Write(string text) => output.Write(Encoding.ASCII.GetBytes(text));
        switch (value)
        {
            case long number: Write("i" + number + "e"); break;
            case string text: var bytes = Encoding.UTF8.GetBytes(text); Write(bytes.Length + ":"); output.Write(bytes); break;
            case byte[] raw: Write(raw.Length + ":"); output.Write(raw); break;
            case List<object> list: Write("l"); foreach (var item in list) Bencode(output, item); Write("e"); break;
            case SortedDictionary<string, object> dict:
                Write("d");
                foreach (var (key, item) in dict) { Bencode(output, key); Bencode(output, item); }
                Write("e");
                break;
            default: throw new ArgumentException("Can't bencode " + value.GetType());
        }
    }

    /// <summary>
    /// INSTALL WOW into a folder that already holds a client: only what fails the torrent's hashes comes
    /// down (from a local web seed that counts what it serves), extras are gone, everything matches.
    /// </summary>
    private static async Task ClientBaseDownloadChecks(string root, Action<string, bool> check)
    {
        static byte[] Bytes(int seed, int length) { var b = new byte[length]; new Random(seed).NextBytes(b); return b; }
        void Put(string path, byte[] bytes) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, bytes); }
        var shipped = Path.Combine(root, "base-src", "Evermore");
        Put(Path.Combine(shipped, "Wow.exe"), Bytes(21, 64 * 1024));
        Put(Path.Combine(shipped, "Data", "common.MPQ"), Bytes(22, 256 * 1024));
        Put(Path.Combine(shipped, "Data", "patch-C.mpq"), Bytes(23, 64 * 1024));
        Put(Path.Combine(shipped, "Data", "enUS", "locale-enUS.MPQ"), Bytes(24, 128 * 1024));
        var total = Directory.EnumerateFiles(shipped, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);

        var port = 0;
        using (var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0))
        { probe.Start(); port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port; probe.Stop(); }
        var listener = new System.Net.HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        long served = 0;
        var serving = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                System.Net.HttpListenerContext context;
                try { context = await listener.GetContextAsync(); } catch (Exception) { return; }
                var relative = Uri.UnescapeDataString(context.Request.Url!.AbsolutePath.TrimStart('/'));
                var file = Path.Combine(Path.GetDirectoryName(shipped)!, relative.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(file)) { context.Response.StatusCode = 404; context.Response.Close(); continue; }
                var bytes = File.ReadAllBytes(file);
                long start = 0, end = bytes.Length - 1;
                var range = context.Request.Headers["Range"];
                if (range is not null && range.StartsWith("bytes="))
                {
                    var parts = range[6..].Split('-');
                    start = long.Parse(parts[0]);
                    if (parts[1].Length > 0) end = long.Parse(parts[1]);
                    context.Response.StatusCode = 206;
                    context.Response.Headers["Content-Range"] = $"bytes {start}-{end}/{bytes.Length}";
                }
                var length = (int)(end - start + 1);
                Interlocked.Add(ref served, length);
                context.Response.ContentLength64 = length;
                try { await context.Response.OutputStream.WriteAsync(bytes.AsMemory((int)start, length)); context.Response.Close(); }
                catch (Exception) { }
            }
        });
        var torrent = FixtureTorrent(shipped, 16 * 1024, $"http://127.0.0.1:{port}/");

        // Another client: same exe and locale, one changed piece in common.MPQ, patch-C replaced by a
        // longer file, an MPQ the realm doesn't ship, and the player's addon.
        var client = Path.Combine(root, "base", "WoW");
        foreach (var file in Directory.EnumerateFiles(shipped, "*", SearchOption.AllDirectories))
            Put(Path.Combine(client, Path.GetRelativePath(shipped, file)), File.ReadAllBytes(file));
        var common = File.ReadAllBytes(Path.Combine(client, "Data", "common.MPQ"));
        common[100_000] ^= 0xFF;
        File.WriteAllBytes(Path.Combine(client, "Data", "common.MPQ"), common);
        Put(Path.Combine(client, "Data", "patch-C.mpq"), Bytes(25, 70 * 1024));
        Put(Path.Combine(client, "Data", "patch-Z.MPQ"), Bytes(26, 1000));
        var addon = Path.Combine(client, "Interface", "AddOns", "Mine", "Mine.toc");
        Put(addon, Bytes(27, 100));

        var cache = Path.Combine(root, "base-torrent-cache");
        var torrents = new TorrentService(cache);
        try
        {
            ClientConformanceService.DeleteNonConforming(client, ClientConformanceService.Check(client, torrent, []));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var webSeeds = 0;
            await torrents.DownloadAsync(torrent, client, keepSharing: false,
                new SyncProgress<TorrentProgress>(p => webSeeds = Math.Max(webSeeds, p.WebSeeds)), timeout.Token);
            check("installing over another client leaves exactly the realm's client",
                ClientConformanceService.Check(client, torrent, []).Matches && File.Exists(addon)
                && Directory.EnumerateFiles(shipped, "*", SearchOption.AllDirectories).All(f =>
                    File.ReadAllBytes(f).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(client, Path.GetRelativePath(shipped, f))))));
            check($"only what differs is downloaded ({served:N0} of {total:N0} bytes)", served > 0 && served <= 64 * 1024 + 2 * 16 * 1024);
            check("the realm's web seed shows up in the download status", webSeeds == 1);

            // REPAIR GAME: a file damaged without its size or time changing passes the saved resume
            // data; only a check that forgets that data finds the damage and downloads the piece again.
            // The client is being shared (still in the engine), as it is in the launcher.
            await torrents.ConfigureAsync(share: true, uploadBytesPerSecond: 0, port: 0);
            await torrents.DownloadAsync(torrent, client, keepSharing: true, null, timeout.Token);
            var mpq = Path.Combine(client, "Data", "common.MPQ");
            var written = File.GetLastWriteTimeUtc(mpq);
            var damaged = File.ReadAllBytes(mpq);
            damaged[200_000] ^= 0xFF;
            File.WriteAllBytes(mpq, damaged);
            File.SetLastWriteTimeUtc(mpq, written);
            var servedBefore = Interlocked.Read(ref served);
            await torrents.DownloadAsync(torrent, client, keepSharing: true, null, timeout.Token);
            check("an ordinary check trusts the shared copy (the damage stays)",
                File.ReadAllBytes(mpq)[200_000] == damaged[200_000] && Interlocked.Read(ref served) == servedBefore);
            await torrents.ForgetResumeAsync(torrent);
            var checking = new List<double>();
            await torrents.DownloadAsync(torrent, client, keepSharing: false,
                new SyncProgress<TorrentProgress>(p => { if (p.Checking) lock (checking) checking.Add(p.Percent); }), timeout.Token);
            var original = File.ReadAllBytes(Path.Combine(shipped, "Data", "common.MPQ"));
            check("after ForgetResumeAsync every file is hash-checked and the damaged piece downloaded again",
                File.ReadAllBytes(mpq).AsSpan().SequenceEqual(original)
                && Interlocked.Read(ref served) - servedBefore is > 0 and <= 2 * 16 * 1024);
            check($"the check reports its progress ({checking.Count} reports, last {checking.LastOrDefault():F0}%)",
                checking.Count > 1 && checking.Last() >= 99);
        }
        catch (Exception ex) { check("installing over another client: " + ex.Message, false); }
        finally
        {
            await torrents.ShutdownAsync();
            listener.Stop();
            listener.Close();
            await serving;
        }
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private static void ClientConformanceChecks(string root, Action<string, bool> check)
    {
        static byte[] Bytes(int seed, int length) { var b = new byte[length]; new Random(seed).NextBytes(b); return b; }
        void Put(string path, byte[] bytes) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, bytes); }
        var shipped = Path.Combine(root, "conform-src", "Evermore");
        Put(Path.Combine(shipped, "Wow.exe"), Bytes(1, 40_000));
        Put(Path.Combine(shipped, "Data", "common.MPQ"), Bytes(2, 60_000));
        Put(Path.Combine(shipped, "Data", "patch-C.mpq"), Bytes(3, 30_000));
        Put(Path.Combine(shipped, "Data", "enUS", "locale-enUS.MPQ"), Bytes(4, 20_000));
        var torrent = FixtureTorrent(shipped, 16_384);

        var client = Path.Combine(root, "conform", "WoW");
        foreach (var file in Directory.EnumerateFiles(shipped, "*", SearchOption.AllDirectories))
            Put(Path.Combine(client, Path.GetRelativePath(shipped, file)), File.ReadAllBytes(file));
        var realmPatch = Path.Combine(client, "Data", "patch-K.MPQ");
        var realmFiles = new[] { Path.Combine(client, "Data", "PATCH-K.MPQ") };
        var players = new[]
        {
            Path.Combine(client, "Interface", "AddOns", "Mine", "Mine.toc"),
            Path.Combine(client, "WTF", "Config.wtf"),
            Path.Combine(client, "Data", "enUS", "realmlist.wtf"),
            Path.Combine(client, "Screenshots", "WoWScrnShot.jpg"),
            Path.Combine(client, "Cache", "WDB", "enUS", "creaturecache.wdb"),
            Path.Combine(client, "Logs", "Sound.log"),
            Path.Combine(client, ".portalkeeper", "client-install.json"),
        };
        foreach (var path in players) Put(path, Bytes(5, 100));
        Put(realmPatch, Bytes(6, 5_000));

        var result = ClientConformanceService.Check(client, torrent, realmFiles);
        check("a client that is the realm's matches", result.Matches && result.Expected == 4);

        var extras = new[]
        {
            Path.Combine(client, "Data", "patch-Z.MPQ"),
            Path.Combine(client, "Data", "enUS", "patch-enUS-9.MPQ"),
            Path.Combine(client, "d3d9.dll"),
            Path.Combine(client, "Data", "Interface", "AddOns", "Hidden", "Hidden.toc"),
        };
        foreach (var path in extras) Put(path, Bytes(7, 900));
        result = ClientConformanceService.Check(client, torrent, realmFiles);
        check("files the realm didn't ship are found, wherever they are",
            result.HasAllClientFiles && result.Extra.Count == extras.Length
            && extras.All(e => result.Extra.Contains(RetiredPatchService.Normalize(Path.GetRelativePath(client, e)))));
        check("realm patches, addons, settings, Cache, Logs and Screenshots aren't extras",
            !result.Extra.Any(e => e.Contains("patch-K", StringComparison.OrdinalIgnoreCase) || e.StartsWith("Interface/") || e.StartsWith("WTF/")
                || e.StartsWith("Cache/") || e.StartsWith("Logs/") || e.StartsWith("Screenshots/") || e.EndsWith("realmlist.wtf") || e.StartsWith(".portalkeeper/")));

        var common = Path.Combine(client, "Data", "common.MPQ");
        var patchC = Path.Combine(client, "Data", "patch-C.mpq");
        var locale = Path.Combine(client, "Data", "enUS", "locale-enUS.MPQ");
        File.WriteAllBytes(common, Bytes(8, 61_000));
        File.WriteAllBytes(patchC, Bytes(9, 8_396));
        File.Delete(locale);
        result = ClientConformanceService.Check(client, torrent, realmFiles);
        check("missing and resized client files are found", !result.HasAllClientFiles && result.Different.Count == 3);
        check("only the longer file counts as too long", result.TooLong.SequenceEqual(new[] { "Data/common.MPQ" }));

        var deleted = ClientConformanceService.DeleteNonConforming(client, result);
        check("extras and too-long files are deleted", deleted == extras.Length + 1
            && extras.All(e => !File.Exists(e)) && !File.Exists(common));
        check("a shorter file stays for the download to finish", File.Exists(patchC));
        check("the player's files, Cache and realm patches stay", players.All(File.Exists) && File.Exists(realmPatch));

        Put(Path.Combine(client, "Data", "patch-Y.MPQ"), Bytes(10, 10));
        result = ClientConformanceService.Check(client, torrent, realmFiles);
        ClientConformanceService.DeleteNonConforming(client, result with { TooLong = [] });
        check("an extra is deleted while client files are still missing", !File.Exists(Path.Combine(client, "Data", "patch-Y.MPQ")) && File.Exists(patchC));

        var stranger = Path.Combine(root, "conform", "Games");
        Put(Path.Combine(stranger, "other-game.exe"), Bytes(11, 10));
        check("a folder without WoW isn't treated as a client", ClientService.FindWowExecutable(stranger) is null
            && ClientService.FindWowExecutable(client) is not null);

        ClientConformanceService.SaveCachedTorrent(client, torrent);
        check("the client torrent is kept for offline checks", ClientConformanceService.LoadCachedTorrent(client) is { } cached && cached.SequenceEqual(torrent));
    }

    private static async Task RetiredPatchChecks(string root, Action<string, bool> check)
    {
        static byte[] Bytes(int seed, int length) { var b = new byte[length]; new Random(seed).NextBytes(b); return b; }
        var clientSrc = Path.Combine(root, "retired-src", "Evermore");
        Directory.CreateDirectory(Path.Combine(clientSrc, "Data"));
        var reforgedC = Bytes(1, 300_000);
        File.WriteAllBytes(Path.Combine(clientSrc, "Data", "patch-B.mpq"), Bytes(2, 70_001));
        File.WriteAllBytes(Path.Combine(clientSrc, "Data", "patch-C.mpq"), reforgedC);
        File.WriteAllBytes(Path.Combine(clientSrc, "Data", "patch-D.mpq"), Bytes(3, 50_003));
        var torrent = FixtureTorrent(clientSrc, 32_768, "https://seed.example/api/launcher/seed.php/key/");

        var ourC = Bytes(10, 8_396);
        var ourF = Bytes(11, 9_000);
        var ourK = Bytes(12, 7_000);
        string Hash(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();
        var known = new List<RetiredPatchService.Entry>
        {
            new() { Path = "Data/patch-C.MPQ", Size = ourC.Length, Sha256 = Hash(ourC) },
            new() { Path = "Data/Patch-F.MPQ", Size = ourF.Length, Sha256 = Hash(ourF) },
        };
        string NewClient(string name)
        {
            var c = Path.Combine(root, "retired-" + name);
            Directory.CreateDirectory(Path.Combine(c, "Data"));
            foreach (var f in Directory.GetFiles(Path.Combine(clientSrc, "Data")))
                File.Copy(f, Path.Combine(c, "Data", Path.GetFileName(f)));
            // Our old patch replaced the client's patch-C (one file on Windows; a second name elsewhere).
            File.Delete(Path.Combine(c, "Data", "patch-C.mpq"));
            File.WriteAllBytes(Path.Combine(c, "Data", "patch-C.MPQ"), ourC);
            File.WriteAllBytes(Path.Combine(c, "Data", "Patch-F.MPQ"), ourF);
            return c;
        }
        var current = new[] { "Data/patch-K.MPQ" };

        // 1. The original comes back from PatchService's backup once it passes the torrent's hashes.
        var a = NewClient("backup");
        var junk = Path.Combine(a, ".portalkeeper", "backups", "patches", "1", "patch-C.mpq");
        Directory.CreateDirectory(Path.GetDirectoryName(junk)!);
        File.WriteAllBytes(junk, Bytes(99, reforgedC.Length)); // same size, wrong content
        var good = Path.Combine(a, ".portalkeeper", "backups", "patches", "2", "patch-C.mpq");
        Directory.CreateDirectory(Path.GetDirectoryName(good)!);
        File.WriteAllBytes(good, reforgedC);
        var downloads = 0;
        var stops = 0;
        var result = await RetiredPatchService.CleanupAsync(a, "r1", current, known, torrent,
            (_, _, _) => { downloads++; return Task.CompletedTask; },
            () => { stops++; return Task.CompletedTask; }, null, CancellationToken.None);
        check("sharing is stopped once before the first change", stops == 1);
        check("retired patch removed and original restored from a verified backup",
            result is { Removed: 2, Restored: 1, Pending: 0 } && downloads == 0
            && File.ReadAllBytes(Path.Combine(a, "Data", "patch-C.mpq")).AsSpan().SequenceEqual(reforgedC));
        check("a retired patch the client torrent doesn't have is just removed", !File.Exists(Path.Combine(a, "Data", "Patch-F.MPQ")));
        check("a backup failing the torrent's hashes is not restored", File.Exists(junk));
        check("a second run finds nothing to do",
            await RetiredPatchService.CleanupAsync(a, "r1", current, known, torrent, null, null, null, CancellationToken.None) is { Removed: 0, Pending: 0 });

        // 2. No backup: the file comes from the web seed, at the BEP 19 URL, and is verified.
        var b = NewClient("download");
        Uri? asked = null;
        result = await RetiredPatchService.CleanupAsync(b, "r1", current, known, torrent,
            (url, path, _) => { asked = url; File.WriteAllBytes(path, reforgedC); return Task.CompletedTask; }, null, null, CancellationToken.None);
        check("without a backup the original is downloaded from the web seed",
            result is { Restored: 1, Pending: 0 } && File.ReadAllBytes(Path.Combine(b, "Data", "patch-C.mpq")).AsSpan().SequenceEqual(reforgedC));
        check("web seed URL is seed + torrent name + path",
            asked?.ToString() == "https://seed.example/api/launcher/seed.php/key/Evermore/Data/patch-C.mpq");

        // 3. A bad download leaves our patch where it is (a stale patch beats a hole) and retries later.
        var c = NewClient("bad");
        result = await RetiredPatchService.CleanupAsync(c, "r1", current, known, torrent,
            (_, path, _) => { File.WriteAllBytes(path, Bytes(5, reforgedC.Length)); return Task.CompletedTask; }, null, null, CancellationToken.None);
        check("a download failing the torrent's hashes keeps our patch and reports it pending",
            result.Pending == 1 && File.ReadAllBytes(Path.Combine(c, "Data", "patch-C.MPQ")).AsSpan().SequenceEqual(ourC)
            && !Directory.EnumerateFiles(Path.Combine(c, ".portalkeeper", "downloads")).Any());

        // 4. Files that aren't byte-for-byte ours, and patches realm.conf still lists, are left alone.
        var d = NewClient("foreign");
        File.WriteAllBytes(Path.Combine(d, "Data", "Patch-F.MPQ"), Bytes(6, ourF.Length));
        result = await RetiredPatchService.CleanupAsync(d, "r1", new[] { "Data/patch-K.MPQ", "Data/patch-C.MPQ" }, known, torrent, null, null, null, CancellationToken.None);
        check("a same-size file that isn't ours stays", File.Exists(Path.Combine(d, "Data", "Patch-F.MPQ")));
        check("a patch realm.conf still lists stays", File.Exists(Path.Combine(d, "Data", "patch-C.MPQ")) && result.Removed == 0);

        // 5. Ledger: the first install's backup is the original; a later update keeps it.
        var e = NewClient("ledger");
        var k = Path.Combine(e, "Data", "patch-K.MPQ");
        var mine = Path.Combine(e, "Data", "mine.bak");
        File.WriteAllBytes(mine, Bytes(7, 1234)); // a player's own file our patch-K replaced
        File.WriteAllBytes(k, ourK);
        RetiredPatchService.RecordInstall(e, "r1", k, Hash(ourK), mine, known);
        var ourK2 = Bytes(13, 7_100);
        var k2Backup = Path.Combine(e, "Data", "k1.bak");
        File.WriteAllBytes(k2Backup, ourK);
        File.WriteAllBytes(k, ourK2);
        RetiredPatchService.RecordInstall(e, "r1", k, Hash(ourK2), k2Backup, known);
        var ledger = RetiredPatchService.Load(e);
        check("ledger keeps the first install's original across updates",
            ledger.Count == 1 && ledger[0].Original == "Data/mine.bak" && ledger[0].Sha256 == Hash(ourK2));
        var backupOfKnown = Path.Combine(e, "Data", "c.bak");
        File.WriteAllBytes(backupOfKnown, ourC);
        File.WriteAllBytes(Path.Combine(e, "Data", "patch-W.MPQ"), ourK);
        RetiredPatchService.RecordInstall(e, "r1", Path.Combine(e, "Data", "patch-W.MPQ"), Hash(ourK), backupOfKnown, known);
        check("an old realm patch is never recorded as the original",
            RetiredPatchService.Load(e).Single(x => x.Path == "Data/patch-W.MPQ").Original is null);
        result = await RetiredPatchService.CleanupAsync(e, "r1", new[] { "Data/patch-W.MPQ" }, known, torrent,
            (_, path, _) => { File.WriteAllBytes(path, reforgedC); return Task.CompletedTask; }, null, null, CancellationToken.None);
        var other = NewClient("other-realm");
        var ok = Path.Combine(other, "Data", "patch-K.MPQ");
        File.WriteAllBytes(ok, ourK);
        RetiredPatchService.RecordInstall(other, "r2", ok, Hash(ourK), null, known);
        await RetiredPatchService.CleanupAsync(other, "r1", current, [], torrent, null, null, null, CancellationToken.None);
        check("another realm's patches are never touched", File.Exists(ok) && RetiredPatchService.Load(other).Count == 1);
        check("a dropped ledger patch is removed and its original put back",
            File.ReadAllBytes(k).AsSpan().SequenceEqual(Bytes(7, 1234)) && !File.Exists(mine)
            && RetiredPatchService.Load(e).All(x => x.Path != "Data/patch-K.MPQ"));
    }

    private static void LongPatchNameChecks(string root, Action<string, bool> check)
    {
        static byte[] Bytes(int seed, int length) { var b = new byte[length]; new Random(seed).NextBytes(b); return b; }
        string Hash(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

        // Which names a stock client loads by itself.
        check("one-character patches are stock names", LongPatchNameService.IsStockName("patch-W.MPQ")
            && LongPatchNameService.IsStockName("patch-4.mpq") && LongPatchNameService.IsStockName("patch-enUS-3.MPQ")
            && LongPatchNameService.IsStockName("PATCH-enUS.mpq"));
        check("longer names are not", !LongPatchNameService.IsStockName("patch-z40-druid-forms.MPQ")
            && !LongPatchNameService.IsStockName("patch-WW.MPQ") && !LongPatchNameService.IsStockName("patch-enUS-10.MPQ")
            && !LongPatchNameService.IsStockName("patch-test.MPQ"));
        RealmInfo Realm(params string[] files) => new()
        {
            Patches = files.Select(f => new PatchDefinition { Id = f, FileName = f, InstallDirectory = "Data" }).ToArray(),
        };
        check("a realm of one-character patches needs nothing", !LongPatchNameService.Needed(Realm("patch-K.MPQ", "patch-W.MPQ")));
        check("a long-named patch needs the client set up", LongPatchNameService.Needed(Realm("patch-K.MPQ", "patch-z80-spells-and-items.MPQ")));
        check("a file that isn't a patch MPQ doesn't", !LongPatchNameService.Needed(Realm("d3d9.dll", "notes-for-players.txt")));

        // The edit itself, on a stand-in for Wow.exe that holds the client's two patterns.
        var client = Path.Combine(root, "longnames");
        Directory.CreateDirectory(client);
        var exe = Path.Combine(client, "Wow.exe");
        var stock = Bytes(40, 4096).Select(b => b == (byte)'p' ? (byte)'q' : b)
            .Concat(Encoding.ASCII.GetBytes("patch-%s-?.MPQ\0\0patch-?.MPQ\0patch-%s.MPQ\0")).Concat(Bytes(41, 512).Select(b => b == (byte)'p' ? (byte)'q' : b)).ToArray();
        File.WriteAllBytes(exe, stock);
        string[] Backups() => Directory.Exists(Path.Combine(client, ".portalkeeper", "backups", "executable"))
            ? Directory.EnumerateFiles(Path.Combine(client, ".portalkeeper", "backups", "executable"), "*", SearchOption.AllDirectories).ToArray()
            : [];
        check("a stock client is not set up yet", !LongPatchNameService.IsSetUp(exe) && Backups().Length == 0
            && File.ReadAllBytes(exe).AsSpan().SequenceEqual(stock));
        check("a stock client is changed", LongPatchNameService.Ensure(client, exe) && LongPatchNameService.IsSetUp(exe));
        var edited = File.ReadAllBytes(exe);
        var differences = Enumerable.Range(0, stock.Length).Where(i => stock[i] != edited[i]).ToArray();
        check("exactly the two wildcard characters change", edited.Length == stock.Length && differences.Length == 2
            && differences.All(i => stock[i] == (byte)'?' && edited[i] == (byte)'*'));
        check("both patterns take any name now", Encoding.ASCII.GetString(edited).Contains("patch-%s-*.MPQ\0\0patch-*.MPQ\0patch-%s.MPQ\0", StringComparison.Ordinal));
        check("the file it replaced is kept", Backups().Length == 1 && Path.GetFileName(Backups()[0]) == "Wow.exe"
            && File.ReadAllBytes(Backups()[0]).AsSpan().SequenceEqual(stock));
        check("a client that's set up is left alone", !LongPatchNameService.Ensure(client, exe)
            && File.ReadAllBytes(exe).AsSpan().SequenceEqual(edited) && Backups().Length == 1
            && Directory.EnumerateFiles(client).Count() == 1);
        File.WriteAllBytes(exe, stock);
        check("the same client set up again keeps one backup", LongPatchNameService.Ensure(client, exe)
            && File.ReadAllBytes(exe).AsSpan().SequenceEqual(edited) && Backups().Length == 1);

        // A read-only Wow.exe (some installs have one) is still set up, and stays read-only.
        File.WriteAllBytes(exe, stock);
        if (OperatingSystem.IsWindows()) File.SetAttributes(exe, File.GetAttributes(exe) | FileAttributes.ReadOnly);
        else File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        var readOnlyDone = LongPatchNameService.Ensure(client, exe);
        var stillReadOnly = OperatingSystem.IsWindows()
            ? (File.GetAttributes(exe) & FileAttributes.ReadOnly) != 0
            : File.GetUnixFileMode(exe) == (UnixFileMode.UserRead | UnixFileMode.UserExecute);
        check("a read-only client is set up and stays read-only", readOnlyDone && stillReadOnly
            && File.ReadAllBytes(exe).AsSpan().SequenceEqual(edited) && Backups().Length == 1
            && Directory.EnumerateFiles(client).Count() == 1);
        if (OperatingSystem.IsWindows()) File.SetAttributes(exe, FileAttributes.Normal);
        else File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        foreach (var backup in Backups())
            if (OperatingSystem.IsWindows()) File.SetAttributes(backup, FileAttributes.Normal);
            else File.SetUnixFileMode(backup, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var half = (byte[])edited.Clone();
        half[differences[0]] = (byte)'?';
        File.WriteAllBytes(exe, half);
        check("a half-edited client is finished", !LongPatchNameService.IsSetUp(exe) && LongPatchNameService.Ensure(client, exe)
            && File.ReadAllBytes(exe).AsSpan().SequenceEqual(edited));
        var backupsSoFar = Backups().Length;
        var foreign = Path.Combine(client, "Other.exe");
        File.WriteAllBytes(foreign, Bytes(42, 2048).Select(b => b == (byte)'p' ? (byte)'q' : b).ToArray());
        var refused = false;
        try { LongPatchNameService.Ensure(client, foreign); } catch (InvalidDataException) { refused = true; }
        check("a file without the patterns is refused and untouched", refused
            && File.ReadAllBytes(foreign).AsSpan().SequenceEqual(Bytes(42, 2048).Select(b => b == (byte)'p' ? (byte)'q' : b).ToArray()));
        var twice = stock.Concat(Encoding.ASCII.GetBytes("patch-?.MPQ\0")).ToArray();
        File.WriteAllBytes(foreign, twice);
        refused = false;
        try { LongPatchNameService.Ensure(client, foreign); } catch (InvalidDataException) { refused = true; }
        var asked = false;
        try { LongPatchNameService.IsSetUp(foreign); } catch (InvalidDataException) { asked = true; }
        check("a pattern that's there twice is refused and untouched", refused && asked
            && File.ReadAllBytes(foreign).AsSpan().SequenceEqual(twice));
        check("a refused file leaves no backup and no temp file", Backups().Length == backupsSoFar
            && Directory.EnumerateFiles(client).Count() == 2);

        // The login patch is known by its section, whatever its file is called.
        check("the login patch is found under a new name", RealmBranding.IsLoginPatch(new PatchDefinition { Id = "LauncherLogin", FileName = "patch-z20-login-screen.MPQ" })
            && RealmBranding.IsLoginPatch(new PatchDefinition { Id = "Other", FileName = "patch-L.MPQ" })
            && !RealmBranding.IsLoginPatch(new PatchDefinition { Id = "SpellCooldowns", FileName = "patch-z80-spells-and-items.MPQ" }));

        // A renamed patch is copied from the old name instead of downloaded again.
        var data = Path.Combine(client, "Data");
        Directory.CreateDirectory(data);
        var w = Bytes(43, 9_000);
        var oldW = Path.Combine(data, "patch-W.MPQ");
        var newW = Path.Combine(data, "patch-z80-spells-and-items.MPQ");
        File.WriteAllBytes(oldW, w);
        RetiredPatchService.RecordInstall(client, "r1", oldW, Hash(w), null, []);
        check("another realm's patch is not adopted", RetiredPatchService.AdoptRenamed(client, "r2", [(newW, Hash(w))]) == 0 && !File.Exists(newW));
        check("a patch with other content is not adopted", RetiredPatchService.AdoptRenamed(client, "r1", [(newW, Hash(Bytes(44, 9_000)))]) == 0 && !File.Exists(newW));
        check("a renamed patch is copied to its new name", RetiredPatchService.AdoptRenamed(client, "r1", [(newW, Hash(w))]) == 1
            && File.ReadAllBytes(newW).AsSpan().SequenceEqual(w) && File.Exists(oldW));
        check("both names are in the ledger until cleanup", RetiredPatchService.Load(client).Count == 2
            && RetiredPatchService.Load(client).Any(e => e.Path == "Data/patch-z80-spells-and-items.MPQ" && e.Original is null));
        check("an installed patch is not copied again", RetiredPatchService.AdoptRenamed(client, "r1", [(newW, Hash(w))]) == 0);
        var cleaned = RetiredPatchService.CleanupAsync(client, "r1", ["Data/patch-z80-spells-and-items.MPQ"], [], null, null, null, null, CancellationToken.None)
            .GetAwaiter().GetResult();
        check("cleanup then removes the old name only", cleaned is { Removed: 1, Restored: 0, Pending: 0 } && !File.Exists(oldW)
            && File.ReadAllBytes(newW).AsSpan().SequenceEqual(w) && !Directory.EnumerateFiles(data, "*.tmp").Any());
        var k = Bytes(46, 9_000);
        var oldK = Path.Combine(data, "patch-K.MPQ");
        var newK = Path.Combine(data, "patch-z10-class-combos.MPQ");
        File.WriteAllBytes(oldK, k);
        RetiredPatchService.RecordInstall(client, "r1", oldK, Hash(k), null, []);
        File.WriteAllBytes(oldK, Bytes(45, 9_000)); // same size, not our file any more
        check("a changed file under the old name is not adopted", RetiredPatchService.AdoptRenamed(client, "r1", [(newK, Hash(k))]) == 0
            && !File.Exists(newK) && !Directory.EnumerateFiles(data, "*.tmp").Any());
    }
}
