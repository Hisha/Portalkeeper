# Portalkeeper Development Guide

## Vaultrona fork (buildthehomelab/wow-Portalkeeper)

This fork is built for one realm and changes three upstream rules on purpose:

* **The realm is built in.** `src/Portalkeeper/Branding/azeroth.realm.conf` is embedded in the executable and copied into the realm store on first run (`RealmBranding`). Its `ConfigURL` keeps it current.
* **Players log in with their game account** before using the launcher (`LauncherAccountService`, the portal's `api/launcher/`). The token is stored with DPAPI on Windows and owner-only on Linux; the password is never stored.
* **The fork distributes the client and patches** over BitTorrent with the realm's web seed (`TorrentService`, MonoTorrent). This replaces the upstream "Portalkeeper does not distribute the World of Warcraft client" rule below for this fork only. The safety rules that come with it:
  * only a complete, hash-verified copy is ever shared; a modified or partial client is not shared and nothing is downloaded into it;
  * a new install goes into an empty folder, one this install started (marked by `.portalkeeper/client-install.json`), or a folder that already holds a WoW client, which becomes the realm's client in place: only what fails the torrent's hashes is downloaded;
  * **the client is held to exactly what the realm ships** (`ClientConformanceService`, built-in realm only): the client torrent's files plus the realm's patches. Anything else in the game folder (other MPQs, dlls, exes) is deleted; a client that isn't the realm's has to go through INSTALL WOW, and the launcher's own install updates itself in place when its files change. Only what `make-client-torrent.py` leaves out is the player's: the top-level Interface, WTF, Cache, Logs, Screenshots and Errors folders, realmlist.wtf and Config.wtf. Keep the two rule sets in step;
  * patches are still SHA-256 checked against realm.conf before they're installed, and fall back to HTTP when the torrent fails;
  * sharing pauses while the game runs and can be turned off in Settings.
* **Required and Recommended patches and Required addons install and update automatically**; players manage optional addons, and on the SETTINGS page's REALM PATCHES they turn the realm's non-Required patches on or off (there is no other patch list): a Recommended patch is on until turned off, an Optional one off until turned on (`PatchChoicesByRealm` in the settings, by realm and patch key). Without a choice, an Optional patch already in the client is kept. The sync takes a turned-off patch out of the client (`PatchService.Remove`; no backup when the file still matches the realm's copy) and puts it back when it's turned on; a choice made while something else is busy is applied after it, or on the next 45-second health tick (`ApplyPatchChoicesAsync`). ENTER REALM also takes out any turned-off patch the sync hasn't got to, so it's never loaded. The choices can't change while the game runs or launches, and isolated realms don't offer them. A launcher left open asks the realm's ConfigURL every 90 seconds and refreshes the realm when realm.conf changed (`CheckRealmConfigurationOnTickAsync`, only while nothing else runs), so a new patch version installs without a restart. Once the launcher has installed an optional (or recommended) realm addon, it updates automatically too until the player uninstalls it (`StartOptionalAddonUpdates`, after each sync). That runs beside everything else and never holds up ENTER REALM: launching, other addon/patch changes, refreshes and a new client folder cancel it first. Each version is tried once per launcher run, failures show in Manage Addons, GitHub addons go by commit, and folders the player put in by hand and personal addons stay manual. Addon backups keep the newest 3 sets per addon.

* **Installed Windows builds update themselves** (`PortalkeeperUpdateService`): a newer release's Setup exe is downloaded from this fork's releases, checked against GitHub's published SHA-256 for the asset (no checksum, no install), and run with `/VERYSILENT /RELAUNCH=1` after Portalkeeper closes, only when no game, install or download is running. The installer's `RelaunchAfterUpdate` entry reopens it. Portable zips and Linux keep the update notice. Release checks ask the portal's `api/launcher/version.php` (at startup and every minute; the release workflow announces each release to it, signed with the `PORTAL_RELEASE_SECRET` Actions secret) and fall back to GitHub's API, at most every 6 hours, only when the portal can't answer; the portal only names the tag, never a download URL.

* **The launcher is one window with pages** (`MainWindow.axaml`, `MainViewModel.Pages.cs`, `Views/Pages/`): a borderless window whose top bar is the navigation: HOME, SHOP, LEGACY, CHARACTERS, ARMORY, CALENDAR, PATCH NOTES and ADDONS as links (each shown only when the realm has it; ADDONS needs a valid client), WEBSITE (the browser), the settings gear, minimize and close. The page sits under the top bar; the status card, launcher-update and crash banners, ENTER REALM and the footer stay at the bottom of every page. Home is the logo with the realm status, live info and what's new beside it. A page is a `UserControl` bound to the `MainViewModel` (`ShopPage`, `LegacyPage`, `CharactersPage`, `ArmoryPage`, `CalendarPage`, `PatchNotesPage`, `AddonsPage`, `SettingsPage`) with a `PageBanner` header strip; the ones with a view model of their own (`Shop`, `Legacy`, `Armory`, `Calendar`, `PatchNotes`) bind their body to it. `GoToAsync` switches the page and asks the portal for what it shows: the armory and calendar feeds are asked again when their last answer is a minute old, the patch notes every visit. A page that stops applying (the shop after a logout, the armory on a realm without one, the addons page when the client goes) sends the player home (`LeaveUnavailablePage`). Dialogs stay dialogs: Add Addon, realm selection, confirms and error boxes open over the main window. A branded `Assets/Branding/background.jpg` (or `.png`) replaces the background (DragonUI's rock ground with a warm glow) when present.

* **The client's `Cache` folder is cleared** only once per client whenever the realm's `realm/cache-version.txt` changes (`ClearCacheIfRealmAsksAsync`, after refreshes and before launch) and by REPAIR GAME (`PatchService.ClearClientCache`). Patch installs, updates and removals leave it alone: Cache/WDB holds only server data, and an empty one makes the next login slow (the client asks the server about every item, creature and quest again, and everything the player does waits behind that).

* **Combat logs are shared with the realm only when the player turns it on** (`CombatLogUploadService`, `MainViewModel.CombatLog.cs`; Settings, off by default, and only shown when the portal's `api/launcher/combatlog.php` says the realm collects them). While it's on, the launcher writes its own addon into the client the game runs from (`Interface/AddOns/PortalkeeperCombatLog`, not in the realm's addon list) and deletes it when it's off; the addon turns combat logging on inside dungeons and raids. A log that was already there when the player opted in is renamed in place (`WoWCombatLog-before-sharing-...txt`), never uploaded. After the game exits, and once at start-up, `Logs/WoWCombatLog.txt` is moved to `Logs/PortalkeeperUpload/`, gzipped and uploaded in parts, and deleted only when the portal confirms it. Uploads never hold up a launch, stop when the game starts or Portalkeeper closes, fail quietly and continue next time; a log still waiting after 14 days is deleted.

* **The coin shop** (`CoinShopService`, `ShopViewModel`, `ShopPage`, `MainViewModel.Shop.cs` and `.Pages.cs`): the SHOP page, its link shown for the built-in realm once the portal's `api/launcher/shop.php` has answered a listing for this login (404 means the realm has no shop). The page's view model is made on the first visit and lives, with its listing and pictures, until the player logs out or changes realm. Players spend the Tarnished Undermine Reals in their account wallet (mod-boss-coins) on mounts, companions and character services for one character at a time; BUY always asks first. The portal caches a listing for 15 seconds, so the page never polls: it asks on every visit, after a purchase and on REFRESH. A purchase without an answer (502, a lost connection) may have gone through, so the listing is fetched again and the player is told to look at the balance; that warning stays up even when the refresh fails, the top bar's links are off while a purchase is on its way (`CanNavigate`), and a self-update waits for it. A 401 from the shop goes back to the login screen (a 403 is only shown). The page is laid out like the retail game's shop: category tabs (MOUNTS first, where it opens; no "all" tab; BLIZZCON and RECRUIT-A-FRIEND list their offers under a heading per year, an offer's `section`, the headings staying in the realm's order whatever the sort), a search box with Show (everything / can buy now / not owned / owned) and Sort (shop order, name, price) for the tab, offer tiles on the left, the picked offer large on a stage on the right with its description, price and BUY. An offer the portal gives a `preview` (a strip of turntable frames, `ShopPreviewService` keeps them on disk by URL) shows its model on the tile and on the stage, where the player turns it by dragging (`ShopTurntable`; it never turns on its own); others keep their drawn icon. An offer the portal marks `account` (a mount or companion on a realm that shares them) is bought for the whole account: the confirm step says so, the stage says every character gets it, and nothing is mailed. An offer the realm's progression hasn't opened for the account yet (mod-boss-coins `locked`, with `unlock` saying what opens it) shows a lock on its tile and the unlock line on the stage, and can't be bought. Tiles decode a small copy of the strip; only the last few stage pictures stay decoded at full size.

* **Legacy** (`LegacyService`, `LegacyRules`, `LegacyViewModel`, `LegacyPage`, `MainViewModel.Legacy.cs` and `.Pages.cs`): the LEGACY page, right after SHOP in the top bar, shown for the built-in realm once the portal's `api/launcher/legacy.php` has answered a listing for this login (404 means Legacy is off, and a 404 on a visit hides the link again). Challenges done anywhere on the account earn Legacy points (mod-legacy); each character spends up to the cap (16) in three perk trees, Adventure, Resourcefulness and Professions, each a grid of rows 0..3 by columns 0..2. A tree the realm marks `strip` (the General tree, Deep Pockets) is drawn as one row above the three, not beside them. A perk marked `permanent` keeps its saved ranks: − is refused down to them (`LegacyRules.RemoveBlocker` with the saved build) and CLEAR ALL leaves them in the draft. A hardcore character only has the points earned on hardcore characters (the server's `points`, shown with a note). Picking is a draft for the picked character: left-click (Enter, Space, +) adds a rank, right-click (−, Delete, Backspace) takes one back, and the − / + beside each perk do the same; SAVE (Ctrl+S) posts the whole build (`{}` resets it), UNDO goes back to what the realm has, CLEAR ALL empties the draft without saving. The draft follows the server's rules (`LegacyRules`): a rank needs a point left, `requiresTreePoints` spent in the tree not counting the perk itself, and `requiresPerk` at max rank; a rank another perk's requirement depends on can't be taken back (refused, never cascaded), but a perk that's already broken holds nothing up, so a build the realm no longer accepts can always be taken apart. A perk that can't be picked is dimmed; its tooltip has its current and next rank, its requirement and why it's blocked. Switching character, leaving the page or closing the window with unsaved changes asks first (KEEP EDITING / DISCARD; an app shutdown for a self-update doesn't ask); a refusal (in combat, points, locked) shows the server's message and keeps the draft, and only an in-combat refusal skips asking for the listing again; a 502 or lost answer may have saved, so the listing is fetched again with the draft kept. After a save the character the portal answers with is the saved build. The links are off while a save is on its way and a self-update waits for it. A CHALLENGES tab lists the account's challenges by category with done / total counts, who did one first and when, and a mark for those done on a hardcore character; not done first, each half sorted Easiest first (the default), Hardest first or in Realm order, by the challenge's `difficulty` (about the level it's done at; 0 from a module that doesn't send it, so ties keep the realm's order). The sort stays across categories and refreshes. Perk and tree icons are WoW icon names fetched like the armory's (`RealmArmoryService.LoadItemIconAsync`).

* **More than 10 characters, in the player's order** (`CharacterListLimitService`, `CharacterOrderService`, `CharactersViewModel`, `CharactersPage`, `MainViewModel.Characters.cs`): the built-in realm lets an account have more than 10 characters. The stock client empties the whole list when the server sends more than 10 (the `jbe` after `cmp byte [ebp-1],0x0a` in its SMSG_CHAR_ENUM handler), so every client of the built-in realm gets that one byte turned into a `jmp` (file offset 0x64057), with the same rules as the long patch names edit: only a client known to be the realm's, never under a running game or a client download, sharing stopped first, the replaced Wow.exe kept under `.portalkeeper/backups/executable` (`ClientExecutableEdit`, shared by both edits), anything that isn't the exact 12340 guard refused, and ENTER REALM refused until it's done. Stock 12340 (c6f92098) with the long patch names and this edit is 51306308, in either order. Isolated realms are never touched. Don't make the client register CVars Config.wtf names (the console set command's "unregistered" flag): tested in game, the next exit wiped Config.wtf. The CHARACTERS page, right after LEGACY, shows once the portal's `api/launcher/characters.php` has answered a list for this login (404 means the realm keeps no order): the account's characters in the order the character select screen shows them, reordered by dragging a row onto another, UP / DOWN (Ctrl+Up / Ctrl+Down) for the picked one, or the LEVEL / NAME / CLASS / LAST PLAYED sorts, as a draft until SAVE (Ctrl+S); REVERT goes back to the realm's order. Favourites (a row's star, the portal's `favourite`, which the realm sends to the screen) work as on the character select screen: always first (the realm's list is shown that way), starring puts one last of the group and unstarring right under it, a move or step across the line under the group stars or unstars, and the sorts keep the group on top; SAVE sends the favourites with the order. It can't be changed while the game runs. The game's character select screen reorders too but can't reach the portal: it leaves `evermoreorder:ACCOUNT:COUNT:HASH:p1,p2,...:F` in the `accountList` CVar in Config.wtf (1-based positions in the realm's list of COUNT characters, a partial list allowed; HASH is the glue's ListHash of the names in that list: UTF-8 bytes and a newline per name, h = (h*31 + b) mod 2^24, six hex digits; F, 0..COUNT, is how many of the first positions are favourites, sent as `favourites` with the guids. The older value without `:F` still parses and leaves the realm's favourites alone. A launcher before 0.5.43 drops a five-part value, so the realm's MinimumVersion goes to 0.5.43 before the glue that writes it ships). `CharacterOrderSync` sends it before each launch (its turn and the send within 5 seconds, before the login handoff writes its `portalkeeper:` value there; it never throws) and after the game exits. Positions must never be sent twice (they'd scramble an order the realm already applied), so: a value read is kept at once in the settings under its own account (`PendingCharacterOrders`, the raw `SourceValue`; another account's waits for that account's next login); as soon as the `characters.php` list is read it's turned into guids, only when COUNT and HASH match that list (else it's stale and dropped); then `accountList` is emptied (only while it still holds that value, and a sync after the game leaves Config.wtf alone once a launch has started), whatever the send does; from then on the kept guids are sent until a 200; a 400 or any 409 drops them (with a trace line) rather than sending them forever. A value equal to the kept `SourceValue` is that same order. One sync at a time, and the page's SAVE takes the same turn (`SavePageOrderAsync`): it waits for a send already on its way, so the realm ends with the page's order; once saved it drops the account's waiting order and its `accountList` value, and the overtaken sync changes nothing after it. `ForgetGamePassword` only ever blanks a `portalkeeper:` value. Config.wtf that isn't UTF-8 is read and written as Latin-1, so untouched lines keep their bytes.
* **196 slots a guild bank tab** (`GuildBankSlotsService`): the realm's `Guild.BankSlotsPerTab` can go up to 196, and a stock client given more than 98 writes the extra items past the end of its tab array (the SMSG_GUILD_BANK_LIST handler doesn't check), so every client of the built-in realm gets the edit with the character limit, under the same rules, and ENTER REALM is refused until it's done. 30 places in .text, the same bytes as wow-server's `tools/client-patches/guild_bank_slots.py`: the array sized 196, three Lua slot checks, and the cursor's item number tab * 256 + slot instead of tab * 98 + slot. All four edits (long patch names, character limit, warband, this) on stock 12340 give 36c0f62d, in any order; `RuntimeTests guild-bank-exe <Wow.exe>` applies it to a copy. DragonUI shows the extra rows once the realm whispers `EvermoreGuildBank` `slots:<n>` at the bank.
* **Bag sizes that change in the world** (`LiveBagSlotsService`): mod-legacy's Deep Pockets raises a bag's CONTAINER_FIELD_NUM_SLOTS while the player plays, but a stock client reads that field once, in CGContainer_C's constructor, and keeps a copy; a bag sent again doesn't help (the client re-enables the object it had instead of building it again). 2 places in .text, 12 bytes, the same as wow-server's `tools/client-patches/live_bag_slots.py`: GetContainerNumSlots' accessor (0x706AD0) returns the bag's slot list (+0x760: count, slot guids' address, ...) and now copies the bag's own field into its count first; the free-slot count (0x7069ED) reads the field. 0.5.46 and 0.5.47 returned the field's address instead of the slot list, which crashed the game (0x75475B, ERROR #132) as soon as it walked a bag; those bytes are replaced like stock ones. Optional, like the warband edit (`IsSetUpOrRefused` / `TryEnsure`, made with the character limit): a stock client is safe and sees the new size after a relog. All five edits on stock 12340 give 016f849f, in any order; `RuntimeTests live-bag-exe <Wow.exe>` applies it to a copy.
* **Two models with one file name** (`M2CacheFullPathsService`): build 12340's model cache (0x81C390, `.\M2Cache.cpp`) is keyed by the file name without the folder unless the caller passes flag 0x10, so the look packs' retail models (`Creature\RetailLibrary\Ridingraptor\Ridingraptor.m2`) and the client's own (`Creature\RidingRaptor\RidingRaptor.m2`) share one mesh for as long as it stays cached, each wearing its own textures: grey patches, an eye on the tail, bright green. One byte, the same as wow-server's `tools/client-patches/m2_cache_full_paths.py`: `and eax, 10h` at 0x81C44A becomes `or eax, 10h`, so every lookup takes the full path branch. Optional (`IsSetUpOrRefused` / `TryEnsure`, made with the character limit): a stock client is safe and only draws those models wrong, so `TryEnsure` also leaves a Wow.exe it can't write alone. All six edits on stock 12340 give 26251947, in any order; `RuntimeTests m2-cache-exe <Wow.exe>` applies it to a copy.

* **Crash reports** (`CrashReportService`, `MainViewModel.CrashReport.cs`): after the game exits, error reports it wrote while it ran (`Errors/*.txt`, newer than the launch) bring up a banner over the status card. When the portal's `api/launcher/crashreport.php` says the realm takes them, the banner offers SEND REPORT / ALWAYS SEND (Settings: "Send crash reports without asking", off by default). The `User:` and `Computer:` lines, the user folder in paths and the Windows user and computer names are taken out before anything is sent. The banner also points at REPAIR GAME.

* **REPAIR GAME** (`MainViewModel.Repair.cs`; Settings and the crash banner): clears Cache, reinstalls every required realm addon and every realm addon the launcher installed, then forgets the client torrent's resume data (`TorrentService.ForgetResumeAsync`) so the update that follows hash-checks every client file, with progress, and downloads what is damaged. The sync after it re-hashes the patches. The player's own addons and WTF settings are not touched.

* **Game options at ENTER REALM** (`RealmLaunchService.ClientSettings`, `RaisedOnceSettings`): `checkAddonVersion 0` and `showQuestTrackingTooltips 1` are put back in Config.wtf on every launch (the realm's addons need them). `farclip 777` (View Distance at its top, which the realm's clear-air fog in wow-server's patch-z98 is drawn for) is raised once per client for the built-in realm only: only when Config.wtf has no number or a lower one, and `.portalkeeper/raised-settings.txt` keeps a `name=value` line for each raise, so a lower value the player picks afterwards stays (`WriteLaunchSettings`). A raise is only remembered once the game has saved Config.wtf itself (`GameSavedConfig`: a line the launcher never writes, like gxApi or locale): a new client's first start runs hardware detection and may lower it, and a first run that crashed leaves only the launcher's lines, so a later launch raises it again and remembers it then. The raise never stops a launch: an unreadable list means nothing to raise, an unwritable one means it's tried again, and a Config.wtf backup that can't be written drops the raise for that launch. Isolated runtimes are left alone. A game started outside the launcher can still write its old value back on exit. A new value in `RaisedOnceSettings` raises it once more.

* **Live info and what's new on the home screen** (`RealmLiveService`, `MainViewModel.Live.cs`): under the realm status pill, from the built-in realm's portal only. The dots: players green; a restart amber, red within five minutes; events green, one row per event (at most four, then "+N more events on") with a line on what it is for the holidays the launcher knows (`DescribeEvent`: a holiday run as several game events, like the Stranglethorn fishing pools, shows once; the realm's own events show by name only); world bosses green with every one up, amber with some, red with none. `api/launcher/live.php` (players online and bots, a pending restart, world events on, rares and world bosses up) is asked with the realm status every 45 seconds; `api/launcher/whatsnew.php` (patch notes merged since `LastPlayedUtc`, the last ENTER REALM) at start, after the game exits and every 15 minutes. Both hide when the portal doesn't answer. The card shows real players online only, never bots (once booted every bot is online, and while the realm boots the status card counts them from realm-status.json); that line hides while the realm boots or its world server is down. Booting or restarting soon, the ENTER REALM button shows only STARTING UP / RESTARTING, since the status card beside it gives the bot count or restart time.

* **The PATCH NOTES page** (`PatchNotesViewModel`, `PatchNotesPage`): the same `whatsnew.php` notes, asked for the last 90 days and as many as the portal gives at once (50), grouped by day, newest first, with every line; "+N more" and FULL NOTES ON THE WEBSITE open `changelog.php` in the browser. ALL PATCH NOTES on the home screen opens the page. Shown for the built-in realm only.

* **DragonUI's look** (`Views/DragonUI.cs`, `Views/DragonStyles.axaml`, `Assets/DragonUI`, MIT): the window and its pages use the realm's in-game DragonUI style, drawn from DragonUI's own textures with its atlas offsets: a bronze window frame (`DragonFrame Kind=Bronze`, its title band is the top bar), gold-filigree insets over marble for cards and sections (`DragonFrame`, `Button.inset`), a rock ground, and red three-slice buttons (`RedButtonChrome`, every button without the link/chrome/gold/plain/inset class). The window is borderless: the bronze frame at Scale 1.6 with the top bar on its title band (the page links, with the open page's in gold over a gold line; WEBSITE; the gear; minimize; close; drag to move). The dialogs that remain (Add Addon, realm selection) keep `DragonTitleBar` on theirs. Page headers are `PageBanner` (`Assets/Calendar/header-banner.png`, the skyline without painted text, the page's name in Cinzel gold, `TextBlock.dragon-title`, and the page's own controls at the right). Buttons that keep their own look (calendar days, shop tabs and tiles) carry the `plain` class. No top-left portrait icon anywhere.

Keep these changes out of upstream pull requests.

## Project Purpose

Portalkeeper is a cross-platform launcher and client-management tool for World of Warcraft 3.3.5a private realms.

Its responsibilities include:

* validating an existing WoW 3.3.5a client installation
* managing realm configuration
* managing supported custom addons
* applying realm-specific client settings
* launching the client
* supporting realm-specific requirements such as addons and patch files

Upstream Portalkeeper does not distribute the World of Warcraft client; this fork does, only through the BitTorrent install described above.

## Supported Platforms

Portalkeeper must remain functional on:

* Windows x64
* Linux x64

Do not solve a platform-specific problem by breaking behavior on the other supported platform.

When introducing native libraries or platform-specific behavior:

* isolate platform-specific code where practical
* preserve existing behavior on unaffected platforms
* verify runtime and publish paths separately from compile-time behavior
* do not assume Windows filesystem, path, executable, or library-loading behavior applies to Linux
* do not assume Linux behavior applies to Windows

## Technology

Portalkeeper uses:

* .NET
* Avalonia UI

Prefer existing project patterns and APIs before introducing new abstractions or dependencies.

Do not introduce Visual Studio-specific project requirements.

The project should remain buildable and maintainable using standard .NET CLI tooling.

## Architecture Principles

### Realm Configuration

Realm-specific behavior belongs in realm configuration when practical.

Examples include:

* realm address
* realm-specific addon requirements
* realm-specific client patch requirements
* other settings required to connect correctly to a particular realm

Do not duplicate realm-specific state in global application configuration unless compatibility or migration requires it.

### Global Configuration

Application-wide preferences that are not specific to one realm belong in Portalkeeper's global configuration.

Keep the distinction between global application settings and realm-specific requirements clear.

### Existing Behavior

Preserve working behavior outside the requested task.

Do not casually change:

* configuration formats
* configuration defaults
* file locations
* addon installation behavior
* client validation behavior
* launch behavior
* native dependency handling
* backup behavior
* Linux behavior while fixing Windows
* Windows behavior while fixing Linux

If a requested feature requires one of these behaviors to change, make that change explicit and keep it narrowly scoped.

## Client Handling

Portalkeeper works with a user's existing World of Warcraft 3.3.5a installation.

Apart from the fork's hash-verified BitTorrent client install (see above), do not add functionality that assumes Portalkeeper distributes Blizzard client files.

Client-related functionality may inspect or manage files in the user's existing client installation when required for Portalkeeper features.

Be conservative when modifying client files.

When replacing or modifying existing files:

* preserve recoverability where appropriate
* avoid destructive behavior when a safer approach exists
* do not overwrite unrelated client data
* verify paths before performing file operations

## WoW 3.3.5a Validation

Portalkeeper validates that the selected client is appropriate for WoW 3.3.5a.

Existing validation behavior should be reused rather than duplicated.

Do not weaken validation merely to make a new feature easier to implement.

If validation must change, inspect the current validator and understand its assumptions first.

## Realm Configuration Files

Realm configuration files (Schema v1) are the authoritative source for realm-specific requirements.

When adding new realm configuration capabilities:

* extend the existing parser/model pattern
* preserve compatibility when required by the task
* validate malformed or missing values appropriately
* avoid scattering realm parsing logic across unrelated UI code
* keep parsing, runtime behavior, and UI responsibilities appropriately separated

Do not redesign the entire configuration system while implementing one configuration field or section.

## Addon Management

Portalkeeper manages selected custom addons used by supported realms.

Existing functionality includes operations such as:

* install
* update
* update all
* remove

Addon definitions may include repository or source information used by Portalkeeper to manage the addon.

When modifying addon management:

* preserve existing working install/update/remove behavior unless explicitly changing it
* distinguish addon metadata from installed addon state
* avoid embedding realm-specific addon requirements in global code when they belong in realm configuration
* keep filesystem operations safe and predictable
* validate paths before deleting or replacing addon directories

Do not broaden an addon-management change into an unrelated redesign of the launcher.

## Realm Patch Requirements

Portalkeeper supports realm-specific client patch requirements.

A realm configuration describes which client patch files that realm requires (`[Patch.<key>]` sections), and Portalkeeper determines whether the selected client satisfies them before connecting to the realm.

Do not assume patch files are identical across realms.

Do not distribute Blizzard-owned assets as part of Portalkeeper (in the release package; the client torrent described above is the exception).

Prefer validation and management of files already legitimately available to the user or supplied by the realm (the realm's own patches and, in this fork, the hash-verified client torrent).

## Native Libraries

Some functionality may require native dependencies such as StormLib.

When working with native libraries:

* account for Windows and Linux separately
* verify the native library is included in the correct publish output
* verify runtime loading searches the location where the library is actually deployed
* do not treat successful compilation as proof that native runtime loading works
* preserve unaffected platform behavior

## UI Changes

Portalkeeper uses Avalonia.

When modifying UI:

* follow existing visual and layout patterns
* avoid unnecessary redesign
* preserve existing functionality outside the requested change
* keep business logic out of UI code when an existing service/model layer is appropriate
* consider window resizing and normal layout behavior
* consider both Windows and Linux behavior

Do not introduce a new UI framework or unrelated styling system.

## Dependencies

Do not add a dependency merely because it makes a small task easier.

Before adding a dependency:

1. Check whether the project already has equivalent functionality.
2. Check whether the .NET runtime or existing dependencies provide what is needed.
3. Confirm the dependency supports Windows and Linux if the affected feature is cross-platform.
4. Ensure the dependency is necessary for the requested task.

Document why a new dependency is required.

## Scope Discipline

Treat each requested task as narrowly scoped.

Before editing:

* identify the requested behavior
* locate the relevant implementation
* inspect surrounding dependencies
* identify existing patterns to follow

Do not perform unrelated:

* refactoring
* renaming
* formatting
* cleanup
* modernization
* dependency updates
* architecture changes
* documentation rewrites

If an unrelated problem is discovered, report it separately rather than fixing it automatically.

## Debugging

Do not guess at fixes.

For bugs or failures:

1. Reproduce or inspect the failure.
2. Read the complete relevant error output.
3. Trace the relevant code path.
4. Find similar working behavior where useful.
5. Form a specific root-cause hypothesis.
6. Test the smallest practical change.
7. Implement the fix only after evidence supports the cause.

Do not stack speculative fixes.

If repeated fixes fail, stop and reconsider the assumptions or architecture.

## Verification

Implementation is not completion.

For code changes, use the appropriate verification available for the task.

At minimum, when applicable:

```bash
dotnet build
```

Also use relevant tests when they exist.

For packaging or publishing changes, create and inspect the actual publish/package output.

For native dependencies, verify the expected native files are present in the produced output.

For runtime-sensitive changes, distinguish between:

* compiled successfully
* verified by inspection
* actually run and tested

Do not claim testing occurred on a platform that was not actually tested.

## Git Review

Before reporting a coding task complete, inspect:

```bash
git status --short
git diff
```

Review all changed and untracked files.

Every intentional source change should be necessary for the requested task.

Generated build, publish, cache, temporary, and diagnostic artifacts should not become source changes unless explicitly required as deliverables.

The `dist/` directory is not source code and should remain excluded from normal repository changes.

## Completion Report

After completing an implementation task, report:

* files intentionally changed
* what behavior changed
* build or test commands actually run
* results of those commands
* anything verified only through inspection
* anything that still requires platform-specific or manual testing
* unrelated issues discovered but intentionally left unchanged

Do not say a feature is complete merely because the code was written.
