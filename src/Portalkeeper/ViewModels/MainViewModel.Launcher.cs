using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Portalkeeper.Models;
using Portalkeeper.Services;

namespace Portalkeeper.ViewModels;

// Vaultrona fork: game-account login, installing the client over BitTorrent, downloading patches
// through their torrents and sharing finished downloads with other players.
public sealed partial class MainViewModel
{
    private readonly LauncherAccountService _accountService = new();
    private readonly LauncherSessionStore _sessionStore = new();
    private readonly TorrentService _torrents = new();
    private readonly SemaphoreSlim _sharingLock = new(1, 1);
    private readonly Dictionary<string, byte[]> _patchTorrents = new(StringComparer.OrdinalIgnoreCase);
    private LauncherSession? _session;
    private byte[]? _clientTorrent;
    private bool _isCheckingLogin = true;
    private bool _isLoggingIn;
    private string _loginError = "";
    private string _rememberedAccountName = "";
    private bool _isInstallingClient;
    private bool _isRepairingClient;
    private CancellationTokenSource? _installCancel;
    private string _installStatus = "";
    private double _installPercent;
    private bool _isInstallingRequired;
    private string _sharingStatus = "";

    private static readonly int[] UploadLimitsKiB = { 512, 1024, 2048, 5120, 10240, 0 };
    public IReadOnlyList<string> UploadLimitChoices { get; } = new[] { "512 KB/s", "1 MB/s", "2 MB/s", "5 MB/s", "10 MB/s", "Unlimited" };

    private void StartLauncher()
    {
        try { RealmBranding.SeedRealmStore(); }
        catch (Exception ex) { _configurationStatus = UserErrorService.Format(ex, "The built-in realm could not be set up"); }
        _patchService.Downloader = DownloadPatchViaTorrentAsync;
        _ = RestoreLoginAsync();
        _ = RunSharingStatusLoopAsync();
        StartSelfUpdates();
    }

    // ---------------------------------------------------------
    // Login
    // ---------------------------------------------------------

    public bool IsLoggedIn => _session is not null;
    public bool ShowLogin => _session is null;
    public bool IsCheckingLogin => _isCheckingLogin;
    public bool CanLogIn => !_isLoggingIn && !_isCheckingLogin;
    public string LoginError => _loginError;
    public bool HasLoginError => _loginError.Length > 0;
    public string AccountName => _session?.AccountName ?? "";
    public string AccountSummary => _session is null ? "" : "Logged in as " + _session.AccountName;
    public string AccountSignupUrl => RealmBranding.AccountSignupUrl;
    public string LoginStatus => _isCheckingLogin ? "Checking your login..." : _isLoggingIn ? "Logging in..." : "";
    /// <summary>The account of a saved login that has to be entered again, to pre-fill the login form.</summary>
    public string RememberedAccountName => _rememberedAccountName;

    private async Task RestoreLoginAsync()
    {
        try
        {
            var saved = _sessionStore.Load();
            if (saved is null) return;
            if (saved.GamePassword is null)
            {
                // Saved by a launcher before 0.5.7, which didn't keep the password the game signs in with.
                _sessionStore.Clear();
                _rememberedAccountName = saved.AccountName;
                _loginError = "Log in once more so Portalkeeper can sign you in to the game too.";
                return;
            }
            var (state, session) = await _accountService.CheckAsync(saved);
            switch (state)
            {
                case LauncherSessionState.Valid:
                    _session = session;
                    _sessionStore.Save(session!);
                    break;
                case LauncherSessionState.Unreachable:
                    // Offline or the portal is down: keep the saved login so the launcher still works.
                    _session = saved;
                    break;
                case LauncherSessionState.Banned:
                    _sessionStore.Clear();
                    _loginError = "This account is banned.";
                    break;
                default:
                    _sessionStore.Clear();
                    _loginError = "Your login has expired. Log in again.";
                    break;
            }
        }
        catch (Exception ex) { _loginError = UserErrorService.Format(ex, "Couldn't check your login"); }
        finally
        {
            _isCheckingLogin = false;
            NotifyLoginChanged();
        }
        if (IsLoggedIn) await AfterLoginAsync();
    }

    public async Task LogInAsync(string accountName, string password)
    {
        if (!CanLogIn) return;
        if (string.IsNullOrWhiteSpace(accountName) || password.Length == 0)
        {
            _loginError = "Enter your account name and password.";
            NotifyLoginChanged();
            return;
        }
        _isLoggingIn = true;
        _loginError = "";
        NotifyLoginChanged();
        try
        {
            _session = await _accountService.LoginAsync(accountName.Trim(), password);
            try { _sessionStore.Save(_session); }
            catch (Exception) { /* Still logged in for this run; the next start asks again. */ }
        }
        catch (LauncherApiException ex) { _loginError = ex.Message; }
        catch (Exception ex) { _loginError = UserErrorService.Format(ex, "Couldn't reach the login server"); }
        finally
        {
            _isLoggingIn = false;
            NotifyLoginChanged();
        }
        if (IsLoggedIn) await AfterLoginAsync();
    }

    public async Task LogOutAsync()
    {
        var session = _session;
        _session = null;
        _clientTorrent = null;
        _patchTorrents.Clear();
        _sessionStore.Clear();
        ForgetCombatLogOffer();
        ForgetCrashReportOffer();
        ForgetShop();
        ForgetLegacy();
        ForgetCharacters();
        NotifyLoginChanged();
        await ConfigureSharingAsync();
        if (session is not null) await _accountService.LogoutAsync(session);
    }

    private async Task AfterLoginAsync()
    {
        await ConfigureSharingAsync();
        await SyncRequiredAndShareAsync();
    }

    /// <summary>
    /// The game password for the client's login screen: only for the built-in realm, and only when it
    /// ships the login patch that takes it (a stock login screen would list it as an account name).
    /// </summary>
    private string? GamePasswordForLaunch(RealmInfo realm) =>
        RealmBranding.IsBuiltIn(realm) && realm.Patches.Any(RealmBranding.IsLoginPatch)
            ? _session?.GamePassword
            : null;

    private void NotifyLoginChanged()
    {
        OnPropertyChanged(nameof(IsLoggedIn));
        OnPropertyChanged(nameof(ShowLogin));
        OnPropertyChanged(nameof(IsCheckingLogin));
        OnPropertyChanged(nameof(CanLogIn));
        OnPropertyChanged(nameof(LoginError));
        OnPropertyChanged(nameof(HasLoginError));
        OnPropertyChanged(nameof(LoginStatus));
        OnPropertyChanged(nameof(AccountName));
        OnPropertyChanged(nameof(AccountSummary));
        OnPropertyChanged(nameof(RememberedAccountName));
        OnPropertyChanged(nameof(CombatLogSharingOffered));
        OnPropertyChanged(nameof(CrashReportsOffered));
        OnPropertyChanged(nameof(ShopAvailable));
        OnPropertyChanged(nameof(LegacyAvailable));
        OnPropertyChanged(nameof(CharactersAvailable));
        NotifyInstallChanged();
        UpdateLaunchReadinessStatus();
    }

    // ---------------------------------------------------------
    // Installing the client
    // ---------------------------------------------------------

    public bool IsInstallingClient => _isInstallingClient;
    public bool CanInstallClient => IsLoggedIn && (!ClientValid || _clientCheck == ClientCheck.NotRealmClient)
        && !_isInstallingClient && !IsLaunching && !IsGameRunning;
    public bool HasPendingClientInstall => !string.IsNullOrWhiteSpace(_savedSettings.PendingClientInstallPath)
        && Directory.Exists(_savedSettings.PendingClientInstallPath);
    public string InstallClientButtonText => HasPendingClientInstall ? "RESUME INSTALL" : "INSTALL WOW";
    public bool CanInstallElsewhere => CanInstallClient && HasPendingClientInstall;
    public string InstallStatus => _installStatus;
    public bool HasInstallStatus => _installStatus.Length > 0;
    public double InstallPercent => _installPercent;

    private const string InstallMarker = ClientConformanceService.InstallMarkerRelativePath;

    /// <summary>
    /// Installs the realm's client from <paramref name="folder"/> the player picked, or resumes the
    /// pending install when it's null. A folder that already holds a WoW client is used as the base:
    /// it becomes the realm's client in place (see <see cref="DownloadClientAsync"/>). Any other folder
    /// gets the client in a new folder inside it, named after the torrent.
    /// </summary>
    public async Task InstallClientAsync(string? folder)
    {
        var session = _session;
        if (!CanInstallClient || session is null) return;
        await DownloadClientAsync(session, folder, repair: false);
    }

    /// <summary>
    /// Downloads the realm's client into its target folder. Files already there are kept when they pass
    /// the torrent's checksums, so only what differs is downloaded; files the realm doesn't ship (and
    /// client files longer than the realm's) are deleted first. Addons, settings, Cache, Logs and
    /// Screenshots are never touched. True when the client ended up complete.
    /// </summary>
    private async Task<bool> DownloadClientAsync(LauncherSession session, string? folder, bool repair)
    {
        var done = false;
        _isInstallingClient = true;
        _isRepairingClient = repair;
        _installCancel = new CancellationTokenSource();
        var cancel = _installCancel.Token;
        SetInstallStatus(repair ? "Checking World of Warcraft..." : "Getting the client download...", 0);
        try
        {
            _clientTorrent ??= await _accountService.GetClientTorrentAsync(session, cancel);
            var infoHash = TorrentService.InfoHashOf(_clientTorrent);
            string target;
            if (repair)
                target = ClientPath;
            else if (folder is null && HasPendingClientInstall)
                target = _savedSettings.PendingClientInstallPath!;
            else if (folder is not null)
                target = ClientService.FindWowExecutable(folder) is not null || File.Exists(Path.Combine(folder, InstallMarker))
                    ? folder
                    : Path.Combine(folder, TorrentService.NameOf(_clientTorrent));
            else
                throw new InvalidOperationException("Choose where to install World of Warcraft.");

            // Never download into a folder that holds something other than WoW. A WoW client is the
            // base for the realm's client; a folder an earlier install started is fine too, even for an
            // older client torrent: the files already there are hash-checked and only what differs is
            // downloaded.
            var marker = Path.Combine(target, InstallMarker);
            var isWowClient = ClientService.FindWowExecutable(target) is not null;
            if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any() && !File.Exists(marker) && !isWowClient)
                throw new InvalidOperationException($"{target} already exists and isn't a WoW client. Choose another location.");
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            await File.WriteAllTextAsync(marker, JsonSerializer.Serialize(new { infoHash, started = DateTimeOffset.UtcNow }), cancel);
            _savedSettings.PendingClientInstallPath = target;
            SaveSettings();

            if (isWowClient || repair)
            {
                SetInstallStatus("Removing files the realm doesn't ship...", 0);
                await StopAllSharingAsync();
                var torrent = _clientTorrent;
                var realmFiles = RealmPatchFiles(target);
                await Task.Run(() => ClientConformanceService.DeleteNonConforming(target,
                    ClientConformanceService.Check(target, torrent, realmFiles)), cancel);
                SetInstallStatus("Checking your files against the realm's client (this takes a few minutes)...", 0);
            }

            var verb = repair ? "Updating World of Warcraft" : "Downloading World of Warcraft";
            var progress = new Progress<TorrentProgress>(p => SetInstallStatus(DescribeDownload(verb, p), p.Percent));
            await _torrents.DownloadAsync(_clientTorrent, target, keepSharing: true, progress, cancel);
            ClientConformanceService.SaveCachedTorrent(target, _clientTorrent);

            _savedSettings.PendingClientInstallPath = null;
            var installed = repair ? "World of Warcraft is up to date." : "World of Warcraft is installed.";
            // The player's own addons and settings live in the client they used until now: bring
            // them along (copied, never overwriting) before the launcher switches to the new one.
            var previousClient = ClientPath;
            if (!repair && ClientValid && !SameDirectory(previousClient, target))
            {
                var realmAddonFolders = _realmInfo?.Addons.Select(a => a.Folder).ToArray() ?? [];
                SetInstallStatus("Copying your addons and settings from your old client...", 100);
                try
                {
                    var imported = await Task.Run(() => ClientImportService.Import(previousClient, target, realmAddonFolders));
                    if (imported.Addons > 0 || imported.SettingsFiles > 0)
                        installed += $" Copied {imported.Addons} addon(s) and your game settings from {previousClient}.";
                    if (imported.Failed > 0)
                        installed += $" {imported.Failed} file(s) couldn't be copied; they're still in {previousClient}.";
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    installed += " Your addons couldn't be copied from your old client: " + ex.Message;
                }
            }
            _isInstallingClient = false; // lets the realm's required patches and addons install next
            _isRepairingClient = false;
            _clientCheck = ClientCheck.Unchecked;
            SetInstallStatus(installed, 100);
            SetClientDirectory(target);
            if (!ClientValid)
                throw new InvalidDataException("The downloaded client didn't pass validation: " + ClientStatus);
            done = true;
            await RediscoverRealmConfigurationAsync(); // also installs the realm's required patches and addons
        }
        catch (OperationCanceledException)
        {
            SetInstallStatus(repair ? "Update paused. TRY AGAIN continues where it stopped."
                : "Install paused. Click RESUME INSTALL to continue where it stopped.", _installPercent);
        }
        catch (LauncherApiException ex) when (ex.Status == HttpStatusCode.NotFound) { SetInstallStatus("The realm doesn't offer a client download yet.", 0); }
        catch (Exception ex) { SetInstallStatus(UserErrorService.Format(ex, repair ? "Update stopped" : "Install stopped"), _installPercent); }
        finally
        {
            _isInstallingClient = false;
            _isRepairingClient = false;
            _installCancel?.Dispose();
            _installCancel = null;
            try { SaveSettings(); } catch (Exception) { }
            // An update that stopped leaves the client as it was: say why on the card.
            if (repair && _clientCheck == ClientCheck.NeedsUpdate) SetClientCheck(ClientCheck.NeedsUpdate, _installStatus);
            NotifyInstallChanged();
            _ = RefreshSharingAsync();
        }
        return done;
    }

    private static bool SameDirectory(string a, string b)
    {
        try
        {
            return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    public void CancelClientInstall() => _installCancel?.Cancel();

    private void SetInstallStatus(string status, double percent)
    {
        _installStatus = status;
        _installPercent = percent;
        NotifyInstallChanged();
    }

    private void NotifyInstallChanged()
    {
        OnPropertyChanged(nameof(IsInstallingClient));
        OnPropertyChanged(nameof(CanInstallClient));
        OnPropertyChanged(nameof(HasPendingClientInstall));
        OnPropertyChanged(nameof(InstallClientButtonText));
        OnPropertyChanged(nameof(CanInstallElsewhere));
        OnPropertyChanged(nameof(InstallStatus));
        OnPropertyChanged(nameof(HasInstallStatus));
        OnPropertyChanged(nameof(InstallPercent));
        OnPropertyChanged(nameof(IsSyncingRequired));
        OnPropertyChanged(nameof(CanChangePatchChoices));
        OnPropertyChanged(nameof(CanEnterRealm));
        OnPropertyChanged(nameof(EnterRealmButtonText));
        NotifyRepairChanged();
        NotifyHome();
    }

    // ---------------------------------------------------------
    // Required patches and addons: always kept installed and current
    // ---------------------------------------------------------

    public bool IsSyncingRequired => _isInstallingRequired;
    private string _syncStatus = "";

    // Required patches always install. Recommended ones install unless the player turned them off on
    // the SETTINGS page (REALM PATCHES), Optional ones only once the player turned them on.
    private bool InstallsAutomatically(PatchInfo patch) => IsPatchWanted(patch);

    // A patch the player turned off comes out of the client again (and goes back in when turned on).
    private bool RemovesAutomatically(PatchInfo patch) => patch.IsInstalled && !IsPatchWanted(patch);

    private bool NeedsRequiredSync => IsLoggedIn && ClientValid && _clientCheck == ClientCheck.Ok && RealmConfigured && !IsIsolatedRealm && AddonsLoaded
        && (Patches.Any(p => InstallsAutomatically(p) && !p.IsValid) || Patches.Any(RemovesAutomatically) || !AddonsReady);

    /// <summary>
    /// Installs or updates every Required and Recommended patch and every Required addon the realm
    /// lists, without asking: players only ever see the optional addons. Runs after login, when the
    /// client is set and whenever the realm configuration is refreshed (which is how a new patch
    /// version arrives).
    /// </summary>
    private async Task SyncRequiredAsync()
    {
        if (!_isInstallingRequired && !NeedsRequiredSync && _syncStatus.Length > 0)
        {
            // Nothing left to install (another client, or it got fixed): drop the old error.
            _syncStatus = "";
            UpdateLaunchReadinessStatus();
        }
        if (_isInstallingRequired || !NeedsRequiredSync || _isInstallingClient || _isManagingComponents
            || _isRefreshingConfiguration || IsLaunching || IsGameRunning)
            return;
        _isInstallingRequired = true;
        _patchChoiceChanged = false;
        NotifyInstallChanged();
        try
        {
            foreach (var patch in Patches.Where(RemovesAutomatically).Select(p => p.Definition).ToArray())
            {
                // Each choice is asked again right before its patch: the player may have changed it since.
                if (IsPatchWanted(patch)) continue;
                SetSyncStatus("Removing realm files: " + patch.Name + "...");
                await ManagePatchAsync(patch.Id, remove: true, turnedOff: true);
            }
            var patches = Patches.Where(p => InstallsAutomatically(p) && !p.IsValid)
                .Select(p => p.Definition).ToArray();
            foreach (var patch in patches)
            {
                if (!IsPatchWanted(patch)) continue;
                SetSyncStatus("Updating realm files: " + patch.Name + "...");
                await ManagePatchAsync(patch.Id, remove: false);
            }
            var addons = _addons.Where(a => a.Definition.Required && (!a.IsInstalled || a.IsUpdateAvailable))
                .Select(a => a.Definition).ToArray();
            foreach (var addon in addons)
            {
                SetSyncStatus("Updating realm addons: " + addon.Name + "...");
                await InstallOrUpdateAddonAsync(addon.Id);
            }
            _syncStatus = "";
        }
        catch (Exception ex)
        {
            _syncStatus = UserErrorService.Format(ex, "Couldn't update the realm's files; CHECK AGAIN retries");
        }
        finally
        {
            _isInstallingRequired = false;
            NotifyInstallChanged();
            UpdateLaunchReadinessStatus();
        }
        if (_patchChoiceChanged)
        {
            // A patch was turned on or off while this run was busy: go again for it.
            _patchChoiceChanged = false;
            await SyncRequiredAsync();
        }
    }

    // ---------------------------------------------------------
    // REALM PATCHES: the Recommended and Optional patches players turn on or off
    // ---------------------------------------------------------

    // A choice no sync run has started on yet. Picked up by the next run, which is started straight
    // away, after a component operation or another sync, and on the 45-second health tick.
    private bool _patchChoiceChanged;

    /// <summary>The realm's patches a player may leave out, with what this player picked.</summary>
    public IReadOnlyList<PatchChoice> PatchChoices { get; private set; } = Array.Empty<PatchChoice>();
    // Isolated realms only ever get their Required patches, so there's nothing to pick there.
    public bool HasPatchChoices => PatchChoices.Count > 0 && !IsIsolatedRealm;
    public bool CanChangePatchChoices => !IsGameRunning && !IsLaunching;

    private IReadOnlyDictionary<string, bool>? CurrentPatchChoices =>
        _realmInfo is { } realm && _savedSettings.PatchChoicesByRealm.TryGetValue(RealmIdentity.FromRealm(realm), out var choices)
            ? choices : null;

    private bool IsPatchWanted(PatchInfo patch) => IsPatchWanted(patch.Definition, CurrentPatchChoices, patch.IsInstalled);

    private bool IsPatchWanted(PatchDefinition patch) => IsPatchWanted(patch, CurrentPatchChoices,
        Patches.FirstOrDefault(p => p.Definition.Id == patch.Id)?.IsInstalled ?? false);

    /// <summary>
    /// Whether a realm patch belongs in the client. Without a choice, a Recommended patch does and an
    /// Optional one only when it's already there (from before there were choices): it's kept, never
    /// taken out behind the player's back.
    /// </summary>
    internal static bool IsPatchWanted(PatchDefinition patch, IReadOnlyDictionary<string, bool>? choices, bool installed)
    {
        if (patch.Requirement == ComponentRequirement.Required) return true;
        if (choices is not null && choices.TryGetValue(patch.Id, out var on)) return on;
        return patch.Requirement == ComponentRequirement.Recommended || installed;
    }

    private void RefreshPatchChoices()
    {
        PatchChoices = Patches.Where(p => p.Definition.Requirement != ComponentRequirement.Required)
            .Select(p => new PatchChoice(this, p.Definition, IsPatchWanted(p), p.IsInstalled, p.IsValid)).ToArray();
        OnPropertyChanged(nameof(PatchChoices));
        OnPropertyChanged(nameof(HasPatchChoices));
    }

    internal void SetPatchChoice(PatchDefinition patch, bool on)
    {
        if (_realmInfo is not { } realm || patch.Requirement == ComponentRequirement.Required || IsGameRunning || IsLaunching
            || IsPatchWanted(patch) == on)
        {
            // Not taken: put the checkbox back to what's in force.
            RefreshPatchChoices();
            return;
        }
        var identity = RealmIdentity.FromRealm(realm);
        if (!_savedSettings.PatchChoicesByRealm.TryGetValue(identity, out var choices))
            _savedSettings.PatchChoicesByRealm[identity] = choices = new Dictionary<string, bool>();
        choices[patch.Id] = on;
        try { SaveSettings(); } catch (Exception) { }
        RefreshPatchChoices();
        _patchChoiceChanged = true;
        _ = ApplyPatchChoicesAsync();
    }

    /// <summary>
    /// Puts a changed choice into effect: the same steps a full sync takes for patches (Wow.exe set up
    /// for the realm's patch names first, the installed patches noted afterwards, optional addon updates
    /// started again). Leaves the choice pending when anything else is busy; it's picked up later.
    /// </summary>
    private async Task ApplyPatchChoicesAsync()
    {
        if (!_patchChoiceChanged || _syncRuns > 0 || _isInstallingRequired || _isManagingComponents || _isInstallingClient
            || _isRefreshingConfiguration || _isCleaningPatches || IsLaunching || IsGameRunning || !NeedsRequiredSync)
            return;
        _syncRuns++;
        NotifyRepairChanged();
        try
        {
            await PrepareCharacterListLimitAsync();
            if (!await PrepareLongPatchNamesAsync())
            {
                // Wow.exe can't be set up now: leave the choice to the next full sync (ENTER REALM still
                // takes out a turned-off patch) rather than stopping sharing again on every tick.
                _patchChoiceChanged = false;
                _ = RefreshSharingAsync();
                return;
            }
            await SyncRequiredAsync();
            RecordInstalledPatches();
            StartOptionalAddonUpdates();
        }
        finally
        {
            _syncRuns--;
            NotifyRepairChanged();
        }
    }

    /// <summary>
    /// ENTER REALM: a patch the player turned off is never loaded, even when the sync that takes it
    /// out hasn't got to it yet. (A patch turned on that isn't in yet just waits for the next sync.)
    /// </summary>
    private async Task RemoveTurnedOffPatchesAsync()
    {
        if (_realmInfo is not { } realm) return;
        var off = Patches.Where(RemovesAutomatically).Select(p => p.Definition).ToArray();
        if (off.Length == 0) return;
        var root = EffectiveClientPath;
        foreach (var patch in off)
        {
            try { await Task.Run(() => _patchService.Remove(root, patch, realm, backupUnlessCurrent: true)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                RefreshPatches();
                throw new InvalidOperationException(patch.Name + " is turned off in Settings but couldn't be taken out of the game ("
                    + ex.Message + "). Turn it back on, or check that the file isn't read-only or open elsewhere.", ex);
            }
        }
        RefreshPatches();
    }

    private void SetSyncStatus(string status)
    {
        _syncStatus = status;
        LaunchStatus = status;
    }

    private async Task SyncRequiredAndShareAsync()
    {
        _syncRuns++;
        NotifyRepairChanged();
        try { await SyncRequiredAndShareCoreAsync(); }
        finally
        {
            _syncRuns--;
            NotifyRepairChanged();
        }
        // A patch choice made while this ran, after its patch step.
        await ApplyPatchChoicesAsync();
    }

    private async Task SyncRequiredAndShareCoreAsync()
    {
        // By itself: combat logs never wait for patches, and patches never wait for an upload.
        _ = StartCombatLogSharingAsync();
        _ = RefreshCrashReportOfferAsync();
        _ = ProbeShopAsync();
        _ = ProbeLegacyAsync();
        _ = ProbeCharactersAsync();
        await ClearCacheIfRealmAsksAsync();
        // The client has to load the realm's patch names before any patch is moved to one: with a
        // Wow.exe that can't be set up, the patches it has are the only ones it loads.
        var ready = await PrepareLongPatchNamesAsync();
        if (!ready && _clientCheck != ClientCheck.Ok)
        {
            // Only the realm's own client is changed. Find out whether this is one, then try again.
            await EnforceRealmClientAsync();
            ready = await PrepareLongPatchNamesAsync();
        }
        // Not a reason to hold the patches back: a client that isn't set up yet only fails with more
        // than 10 characters, and ENTER REALM asks again.
        await PrepareCharacterListLimitAsync();
        if (ready)
        {
            await AdoptRenamedPatchesAsync();
            await CleanupRetiredPatchesAsync();
            await EnforceRealmClientAsync();
            await SyncRequiredAsync();
            RecordInstalledPatches();
        }
        StartOptionalAddonUpdates();
        await RefreshSharingAsync();
    }

    // ---------------------------------------------------------
    // Keeping the client exactly what the realm ships
    // ---------------------------------------------------------

    private enum ClientCheck { Unchecked, Ok, NeedsUpdate, NotRealmClient }
    private ClientCheck _clientCheck = ClientCheck.Unchecked;
    private string _clientCheckStatus = "";
    private readonly SemaphoreSlim _clientCheckLock = new(1, 1);
    private DateTimeOffset _lastAutoUpdate = DateTimeOffset.MinValue;
    private static readonly TimeSpan AutoUpdateInterval = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Holds the client to exactly what the realm ships (ClientConformanceService): files the realm
    /// doesn't ship are deleted, and when client files are missing or changed, a client the launcher
    /// installed (or made from another client) is updated in place by itself. Any other client goes
    /// through INSTALL WOW first, which uses it as the base. Only for the built-in realm. When the portal
    /// can't be reached the last client torrent seen is used; without any, the client isn't held back.
    /// Returns true when the client may be played.
    /// </summary>
    private async Task<bool> EnforceRealmClientAsync()
    {
        var session = _session;
        var realm = _realmInfo;
        if (session is null || realm is null || !ClientValid || !RealmBranding.IsBuiltIn(realm) || IsIsolatedRealm)
            return SetClientCheck(ClientCheck.Ok);
        if (_isInstallingClient || IsGameRunning) return _clientCheck == ClientCheck.Ok;
        await _clientCheckLock.WaitAsync();
        try
        {
            var root = ClientPath;
            byte[]? torrent;
            try { torrent = _clientTorrent ??= await _accountService.GetClientTorrentAsync(session); }
            catch (LauncherApiException ex) when (ex.Status == HttpStatusCode.NotFound) { torrent = null; }
            catch (Exception) { torrent = ClientConformanceService.LoadCachedTorrent(root); }
            if (torrent is null) return SetClientCheck(ClientCheck.Ok);

            var realmFiles = RealmPatchFiles(root);
            var result = await Task.Run(() => ClientConformanceService.Check(root, torrent, realmFiles));
            var ours = ClientConformanceService.IsRealmInstall(root);
            if (!result.HasAllClientFiles && !ours)
                return SetClientCheck(ClientCheck.NotRealmClient);
            if (result.Extra.Count > 0)
            {
                var deleted = await Task.Run(() => ClientConformanceService.DeleteNonConforming(root,
                    result with { TooLong = [] }));
                if (deleted > 0) RefreshPatches();
            }
            if (result.HasAllClientFiles)
            {
                ClientConformanceService.SaveCachedTorrent(root, torrent);
                return SetClientCheck(ClientCheck.Ok);
            }

            if (DateTimeOffset.UtcNow - _lastAutoUpdate >= AutoUpdateInterval)
            {
                _lastAutoUpdate = DateTimeOffset.UtcNow;
                _ = DownloadClientAsync(session, null, repair: true);
                return SetClientCheck(ClientCheck.NeedsUpdate, "Updating World of Warcraft...");
            }
            return SetClientCheck(ClientCheck.NeedsUpdate,
                $"{result.Different.Count} World of Warcraft file(s) don't match the realm's client; CHECK AGAIN retries the update.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return SetClientCheck(ClientCheck.NeedsUpdate, UserErrorService.Format(ex, "Couldn't check World of Warcraft's files"));
        }
        finally { _clientCheckLock.Release(); }
    }

    /// <summary>TRY AGAIN: an update that didn't fix the client may run again straight away.</summary>
    public void AllowClientUpdateRetry() => _lastAutoUpdate = DateTimeOffset.MinValue;

    private bool SetClientCheck(ClientCheck state, string status = "")
    {
        _clientCheck = state;
        _clientCheckStatus = status;
        NotifyInstallChanged();
        UpdateLaunchReadinessStatus();
        return state == ClientCheck.Ok;
    }

    /// <summary>Where the realm's patches go in <paramref name="root"/>: they belong in the client too, installed or not.</summary>
    private List<string> RealmPatchFiles(string root)
    {
        var files = new List<string>();
        if (_realmInfo is not { } realm) return files;
        files.AddRange(realm.Patches.Where(p => p.InstallMode == PatchInstallMode.File)
            .Select(p => Path.Combine(root, p.InstallDirectory, p.FileName)));
        if (SameDirectory(root, EffectiveClientPath))
            files.AddRange(Patches.Where(p => p.Destination.Length > 0).Select(p => p.Destination));
        // Patches this realm installed and hasn't taken out yet are RetiredPatchService's to remove
        // (it puts back what they replaced), not files the realm never shipped.
        var identity = RealmIdentity.FromRealm(realm);
        files.AddRange(RetiredPatchService.Load(root).Where(e => e.Realm == identity)
            .Select(e => Path.Combine(root, e.Path.Replace('/', Path.DirectorySeparatorChar))));
        return files;
    }

    // ---------------------------------------------------------
    // Patches the realm no longer lists
    // ---------------------------------------------------------

    private bool _isCleaningPatches;
    private static readonly System.Net.Http.HttpClient ClientFileHttp = new() { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    /// <summary>
    /// Takes out realm patches realm.conf dropped and restores the client files they replaced (see
    /// RetiredPatchService). Needs the client torrent to know which files are the client's, so it waits
    /// for a login and skips the run when the torrent can't be fetched (other than "this realm has none").
    /// </summary>
    private async Task CleanupRetiredPatchesAsync()
    {
        var session = _session;
        var realm = _realmInfo;
        if (session is null || realm is null || !ClientValid || !RealmConfigured || IsIsolatedRealm || IsGameRunning
            || IsLaunching || _isInstallingClient || _isManagingComponents || _isCleaningPatches)
            return;
        _isCleaningPatches = true;
        try
        {
            byte[]? torrent;
            try { torrent = _clientTorrent ??= await _accountService.GetClientTorrentAsync(session); }
            catch (LauncherApiException ex) when (ex.Status == HttpStatusCode.NotFound) { torrent = null; }
            catch (Exception) { return; } // offline or the portal is down: try again next sync

            var current = realm.Patches.Where(p => p.InstallMode == PatchInstallMode.File)
                .Select(p => Path.Combine(p.InstallDirectory, p.FileName));
            var status = new Progress<string>(SetSyncStatus);
            var result = await RetiredPatchService.CleanupAsync(EffectiveClientPath, RealmIdentity.FromRealm(realm), current,
                RealmBranding.IsBuiltIn(realm) ? RealmBranding.RetiredPatches : [],
                torrent, DownloadClientFileAsync, StopAllSharingAsync, status, CancellationToken.None);
            if (result.Removed > 0) RefreshPatches();
            if (torrent is not null && RealmBranding.IsBuiltIn(realm))
            {
                var root = EffectiveClientPath;
                await Task.Run(() => RetiredPatchService.RemoveClientFiles(root, RealmBranding.RemovedClientFiles, torrent));
            }
            _syncStatus = result.Pending > 0
                ? "Couldn't restore some client files the realm's old patches replaced; CHECK AGAIN retries."
                : "";
            UpdateLaunchReadinessStatus();
        }
        catch (Exception ex)
        {
            _syncStatus = UserErrorService.Format(ex, "Couldn't remove the realm's old patches; CHECK AGAIN retries");
            UpdateLaunchReadinessStatus();
        }
        finally { _isCleaningPatches = false; }
    }

    /// <summary>
    /// Before the old names are cleaned up: a patch the realm renamed is copied to its new name
    /// instead of being downloaded again (RetiredPatchService.AdoptRenamed).
    /// </summary>
    private async Task AdoptRenamedPatchesAsync()
    {
        var realm = _realmInfo;
        if (realm is null || !ClientValid || !RealmConfigured || IsIsolatedRealm || IsGameRunning
            || IsLaunching || _isInstallingClient || _isManagingComponents || _isCleaningPatches || _isInstallingRequired)
            return;
        try
        {
            var root = EffectiveClientPath;
            var identity = RealmIdentity.FromRealm(realm);
            var wanted = realm.Patches.Where(p => p.InstallMode == PatchInstallMode.File && p.Sha256.Length > 0 && IsPatchWanted(p))
                .Select(p => (ManagedPath.Resolve(root, Path.Combine(ManagedPath.Relative(p.InstallDirectory),
                    ManagedPath.Relative(p.FileName, true))), p.Sha256)).ToArray();
            if (wanted.All(w => File.Exists(w.Item1))) return;
            SetSyncStatus("Updating realm files...");
            // Copying and hashing a few hundred MB: not on the UI thread.
            var adopted = await Task.Run(() => RetiredPatchService.AdoptRenamed(root, identity, wanted));
            _syncStatus = "";
            if (adopted > 0) RefreshPatches();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { _syncStatus = ""; }
    }

    /// <summary>
    /// The built-in realm names its patches after what they do, which a stock client doesn't load:
    /// LongPatchNameService sets the client's Wow.exe up for them. Returns true when the client loads
    /// the realm's names (or the realm needs nothing), false when it doesn't yet: then no patch may be
    /// moved to a new name. Only a client known to be the realm's is changed, never under a running
    /// game or a client download, and sharing is stopped first (a torrent may have the file open).
    /// </summary>
    private async Task<bool> PrepareLongPatchNamesAsync()
    {
        var realm = _realmInfo;
        if (realm is null || !ClientValid || IsIsolatedRealm || !RealmBranding.IsBuiltIn(realm)
            || !LongPatchNameService.Needed(realm))
            return true;
        try
        {
            var root = EffectiveClientPath;
            var executable = ClientService.FindWowExecutable(root, realm.Client.Executable)
                ?? throw new FileNotFoundException("Wow.exe was not found in the client folder.");
            if (await Task.Run(() => LongPatchNameService.IsSetUp(executable))) return true;
            if (IsGameRunning || _isInstallingClient) return false;
            if (_clientCheck != ClientCheck.Ok && !ClientConformanceService.IsRealmInstall(root)) return false;
            await StopAllSharingAsync();
            await Task.Run(() => LongPatchNameService.Ensure(root, executable));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            _syncStatus = UserErrorService.Format(ex, "Couldn't set World of Warcraft up for the realm's patches; CHECK AGAIN retries");
            UpdateLaunchReadinessStatus();
            return false;
        }
    }

    /// <summary>
    /// The built-in realm lets an account have more than 10 characters, which a stock client answers
    /// with an empty character list: CharacterListLimitService sets the client's Wow.exe up for more.
    /// WarbandSelectService then lets the character select screen show its warband camp (several
    /// characters and their pets, each character's era); its screen stays stock without it, so a
    /// Wow.exe it can't edit (another tool's edits) still launches. GuildBankSlotsService lets a guild bank
    /// tab hold 196 items; a stock client on a realm with bigger tabs writes past its array, so like the
    /// limit it is required. Every client of the built-in realm needs both, whatever the realm lists.
    /// LiveBagSlotsService lets a bag's new size (Deep Pockets) show without a relog, and
    /// M2CacheFullPathsService keeps two models with the same file name apart, and MoonPhasesService gives
    /// the moons their phases and real nights; all optional, like the warband edit.
    /// Returns true when the
    /// client is set up (or isn't the built-in realm's), false when it isn't yet; the same rules as
    /// <see cref="PrepareLongPatchNamesAsync"/>: only a client known to be the realm's, never under a
    /// running game or a client download, and sharing stopped first.
    /// </summary>
    private async Task<bool> PrepareCharacterListLimitAsync()
    {
        var realm = _realmInfo;
        if (realm is null || !ClientValid || IsIsolatedRealm || !RealmBranding.IsBuiltIn(realm))
            return true;
        try
        {
            var root = EffectiveClientPath;
            var executable = ClientService.FindWowExecutable(root, realm.Client.Executable)
                ?? throw new FileNotFoundException("Wow.exe was not found in the client folder.");
            if (await Task.Run(() => CharacterListLimitService.IsSetUp(executable) && GuildBankSlotsService.IsSetUp(executable)
                && WarbandSelectService.IsSetUpOrRefused(executable) && LiveBagSlotsService.IsSetUpOrRefused(executable)
                && M2CacheFullPathsService.IsSetUpOrRefused(executable) && MoonPhasesService.IsSetUpOrRefused(executable)))
                return true;
            if (IsGameRunning || _isInstallingClient) return false;
            if (_clientCheck != ClientCheck.Ok && !ClientConformanceService.IsRealmInstall(root)) return false;
            await StopAllSharingAsync();
            await Task.Run(() =>
            {
                CharacterListLimitService.Ensure(root, executable);
                GuildBankSlotsService.Ensure(root, executable);
                WarbandSelectService.TryEnsure(root, executable);
                LiveBagSlotsService.TryEnsure(root, executable);
                M2CacheFullPathsService.TryEnsure(root, executable);
                MoonPhasesService.TryEnsure(root, executable);
            });
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            // A Wow.exe these edits refuse (another build, or another tool's bytes at their places) can't be
            // fixed by retrying: only the realm's own file can.
            _syncStatus = ex is InvalidDataException
                ? ex.Message + " Put the realm's Wow.exe back (reinstall the client, or copy Wow.exe from it), then CHECK AGAIN."
                : UserErrorService.Format(ex, "Couldn't set World of Warcraft up for the character select screen and guild bank; CHECK AGAIN retries");
            UpdateLaunchReadinessStatus();
            return false;
        }
    }

    /// <summary>A file about to be replaced may be open in a torrent: stop sharing until the next refresh.</summary>
    private async Task StopAllSharingAsync()
    {
        await _sharingLock.WaitAsync();
        try { await _torrents.StopSharingExceptAsync(Array.Empty<string>()); }
        finally { _sharingLock.Release(); }
    }

    private static async Task DownloadClientFileAsync(Uri url, string path, CancellationToken cancellationToken)
    {
        using var response = await ClientFileHttp.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await response.Content.CopyToAsync(output, cancellationToken);
    }

    /// <summary>Notes the realm patches installed now, so they can be taken out cleanly later.</summary>
    private void RecordInstalledPatches()
    {
        if (!ClientValid || IsIsolatedRealm || _realmInfo is not { } realm) return;
        try
        {
            RetiredPatchService.RecordCurrent(EffectiveClientPath, RealmIdentity.FromRealm(realm), Patches
                .Where(p => p.IsValid && p.Definition.InstallMode == PatchInstallMode.File && p.Destination.Length > 0)
                .Select(p => (p.Destination, p.Definition.Sha256)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
    }

    // ---------------------------------------------------------
    // Clearing the client's Cache when the realm asks
    // ---------------------------------------------------------

    private static readonly System.Net.Http.HttpClient CacheVersionHttp = new() { Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>
    /// Patch changes leave Cache alone (it holds only server data, see PatchService.ClearClientCache). For
    /// server-side changes the client caches (items, spells, quests): when the realm's cache-version.txt changes, Cache is cleared
    /// once for this client. Runs after realm refreshes and right before launching.
    /// </summary>
    private async Task ClearCacheIfRealmAsksAsync()
    {
        if (!ClientValid || IsGameRunning || IsIsolatedRealm) return;
        string version;
        try
        {
            using var response = await CacheVersionHttp.GetAsync(RealmBranding.CacheVersionUrl);
            if (!response.IsSuccessStatusCode) return; // no file: the realm doesn't use this
            version = (await response.Content.ReadAsStringAsync()).Trim();
        }
        catch (Exception) { return; }
        if (version.Length is 0 or > 64) return;

        var client = Path.GetFullPath(ClientPath);
        if (_savedSettings.ClientCacheVersions.TryGetValue(client, out var seen) && seen == version) return;
        PatchService.ClearClientCache(client);
        _savedSettings.ClientCacheVersions[client] = version;
        try { SaveSettings(); } catch (Exception) { }
    }

    // ---------------------------------------------------------
    // Optional addons: the only components players manage themselves
    // ---------------------------------------------------------

    public IReadOnlyList<AddonInfo> OptionalAddons => _addons.Where(a => !a.Definition.Required).ToArray();

    public string OptionalAddonStatus
    {
        get
        {
            if (!ClientValid) return "Install or locate World of Warcraft to add addons.";
            if (IsCheckingAddons) return "Checking addons...";
            var optional = OptionalAddons;
            if (optional.Count == 0) return AddonsLoaded ? "No extra addons are offered for this realm." : _addonStatus;
            var installed = optional.Count(a => a.IsInstalled);
            var updates = optional.Count(a => a.IsInstalled && a.IsUpdateAvailable);
            var failed = optional.Where(a => a.IsUpdateAvailable && _optionalUpdateFailures.ContainsKey(a.Definition.Id))
                .Select(a => _optionalUpdateFailures[a.Definition.Id]).ToArray();
            return $"{installed} of {optional.Count} addons installed" + (updates > 0 ? $" · {updates} update{(updates == 1 ? "" : "s")} available" : "") + "."
                + (failed.Length > 0 ? " Couldn't update " + string.Join("; ", failed) + "." : "");
        }
    }

    // The UPDATE INSTALLED button: every installed optional addon with an update it can install.
    private static bool CanUpdateInstalled(AddonInfo addon) =>
        !addon.Definition.Required && addon.IsInstalled && addon.CanInstallOrUpdate;

    /// <summary>Updates the optional addons the player installed (never installs new ones), right now.</summary>
    public Task UpdateInstalledAddonsAsync() => RunComponentOperationAsync(async () =>
    {
        foreach (var addon in OptionalAddons.Where(CanUpdateInstalled).Select(a => a.Definition).ToArray())
        {
            await _addonInstallerService.InstallOrUpdateAsync(EffectiveClientPath, addon);
            _optionalUpdateFailures.Remove(addon.Id);
        }
        await LoadAddonsAsync();
    });

    // Once the player installs an optional addon, it's kept current in the background until they
    // uninstall it. This never holds up ENTER REALM: launching, any other addon or patch change, a realm
    // refresh or a new client folder stops it first (StopOptionalAddonUpdatesAsync).

    // Each addon version is tried once per launcher run, so one that keeps failing (or still looks out
    // of date after installing) isn't downloaded again at every check.
    private readonly HashSet<string> _autoUpdateTried = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _optionalUpdateFailures = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _optionalUpdateCancel;
    private Task _optionalUpdateTask = Task.CompletedTask;
    private bool IsUpdatingOptionalAddons => !_optionalUpdateTask.IsCompleted;

    private static string AutoUpdateKey(AddonInfo addon) =>
        addon.Definition.Id + "|" + addon.Definition.SourceCommit + "|" + addon.Definition.Version + "|" + addon.Definition.DownloadUrl;

    // Realm addons (not personal ones) this launcher installed: a folder the player put there by hand
    // is left alone. A GitHub addon goes by its commit, since a multi-folder addon's toc versions can
    // disagree and the version alone would then ask for the same install again and again.
    private bool UpdatesAutomatically(AddonInfo addon) =>
        CanUpdateInstalled(addon) && !addon.Definition.IsPersonal && addon.InstalledByLauncher
        && (addon.HasTrackedSourceCommit ? addon.SourceUpdateAvailable : addon.VersionUpdateAvailable)
        && !_autoUpdateTried.Contains(AutoUpdateKey(addon));

    private void StartOptionalAddonUpdates()
    {
        if (IsUpdatingOptionalAddons || !IsLoggedIn || !ClientValid || _clientCheck != ClientCheck.Ok || !RealmConfigured
            || IsIsolatedRealm || !AddonsLoaded || _isInstallingRequired || _isInstallingClient || _isManagingComponents
            || _isRefreshingConfiguration || IsLaunching || IsGameRunning)
            return;
        var due = _addons.Where(UpdatesAutomatically).ToArray();
        if (due.Length == 0) return;
        _optionalUpdateCancel = new CancellationTokenSource();
        _optionalUpdateTask = UpdateOptionalAddonsAsync(due, EffectiveClientPath, _optionalUpdateCancel.Token);
    }

    private async Task UpdateOptionalAddonsAsync(AddonInfo[] due, string client, CancellationToken cancel)
    {
        var changed = false;
        foreach (var addon in due)
        {
            if (cancel.IsCancellationRequested) break;
            try
            {
                await _addonInstallerService.InstallOrUpdateAsync(client, addon.Definition, cancel);
                _optionalUpdateFailures.Remove(addon.Definition.Id);
                changed = true;
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // Shown in Manage Addons while the update is still missing.
                _optionalUpdateFailures[addon.Definition.Id] = addon.Definition.Name + " (" + UserErrorService.Format(ex) + ")";
            }
            _autoUpdateTried.Add(AutoUpdateKey(addon));
        }
        // Whoever stopped it reloads the addons itself; a launch shouldn't wait for GitHub.
        if (changed && !cancel.IsCancellationRequested) await LoadAddonsAsync();
        else OnPropertyChanged(nameof(OptionalAddonStatus));
    }

    private async Task StopOptionalAddonUpdatesAsync()
    {
        _optionalUpdateCancel?.Cancel();
        try { await _optionalUpdateTask; }
        catch (Exception) { }
    }

    // ---------------------------------------------------------
    // Patches over BitTorrent
    // ---------------------------------------------------------

    private async Task<byte[]> PatchTorrentAsync(LauncherSession session, string fileName, string sha256)
    {
        // One torrent per patch version: a new SHA-256 in realm.conf means a new file on the server.
        var key = fileName + "|" + sha256;
        if (_patchTorrents.TryGetValue(key, out var cached)) return cached;
        var bytes = await _accountService.GetPatchTorrentAsync(session, fileName);
        _patchTorrents[key] = bytes;
        return bytes;
    }

    private static readonly TimeSpan PatchStallTimeout = TimeSpan.FromSeconds(60);

    private async Task<bool> DownloadPatchViaTorrentAsync(PatchDefinition patch, string tempPath)
    {
        var fileName = RealmBranding.HostedPatchFileName(patch.SourceUrl);
        var session = _session;
        if (session is null || fileName is null) return false;
        var staging = Path.Combine(EffectiveClientPath, ".portalkeeper", "downloads");
        byte[]? bytes = null;
        try
        {
            bytes = await PatchTorrentAsync(session, fileName, patch.Sha256);
            // Give up on the torrent (and let PatchService use plain HTTP) after a minute without progress.
            using var stalled = new CancellationTokenSource(PatchStallTimeout);
            long lastDone = -1;
            var progress = new Progress<TorrentProgress>(p =>
            {
                LaunchStatus = DescribeDownload("Downloading " + patch.Name, p);
                if (p.DoneBytes > lastDone)
                {
                    lastDone = p.DoneBytes;
                    stalled.CancelAfter(PatchStallTimeout);
                }
            });
            await _torrents.DownloadAsync(bytes, staging, keepSharing: false, progress, stalled.Token);
            File.Move(Path.Combine(staging, TorrentService.NameOf(bytes)), tempPath);
            return true;
        }
        catch (Exception)
        {
            // PatchService falls back to the plain HTTP download; don't leave a partial copy behind.
            if (bytes is not null)
            {
                try { await _torrents.DiscardAsync(bytes, staging); } catch (Exception) { }
            }
            return false;
        }
    }

    // ---------------------------------------------------------
    // Sharing with other players
    // ---------------------------------------------------------

    public bool ShareDownloads
    {
        get => _savedSettings.ShareDownloads;
        set
        {
            if (_savedSettings.ShareDownloads == value) return;
            _savedSettings.ShareDownloads = value;
            OnPropertyChanged();
            SaveSettings();
            _ = ApplySharingSettingsAsync();
        }
    }

    public int UploadLimitIndex
    {
        get
        {
            var index = Array.IndexOf(UploadLimitsKiB, _savedSettings.ShareUploadLimitKiB);
            return index < 0 ? Array.IndexOf(UploadLimitsKiB, 2048) : index;
        }
        set
        {
            if (value < 0 || value >= UploadLimitsKiB.Length || value == UploadLimitIndex) return;
            _savedSettings.ShareUploadLimitKiB = UploadLimitsKiB[value];
            OnPropertyChanged();
            SaveSettings();
            _ = ApplySharingSettingsAsync();
        }
    }

    public string SharingStatus => _sharingStatus;

    private async Task ApplySharingSettingsAsync()
    {
        await ConfigureSharingAsync();
        await RefreshSharingAsync();
    }

    private async Task ConfigureSharingAsync()
    {
        if (_savedSettings.TorrentPort is < 1024 or > 65535)
        {
            _savedSettings.TorrentPort = Random.Shared.Next(20000, 60000);
            try { SaveSettings(); } catch (Exception) { }
        }
        await _torrents.ConfigureAsync(_savedSettings.ShareDownloads && IsLoggedIn,
            _savedSettings.ShareUploadLimitKiB * 1024, _savedSettings.TorrentPort);
    }

    /// <summary>
    /// Shares the client and every verified patch that has a torrent, and stops sharing anything
    /// that's no longer current (a replaced patch, a different client folder).
    /// </summary>
    public async Task RefreshSharingAsync()
    {
        var session = _session;
        if (session is null || IsGameRunning || _isInstallingClient) return;
        await _sharingLock.WaitAsync();
        try
        {
            var keep = new List<string>();
            if (_torrents.SharingEnabled && ClientValid && !IsIsolatedRealm)
            {
                try
                {
                    _clientTorrent ??= await _accountService.GetClientTorrentAsync(session);
                    if (await _torrents.ShareAsync(_clientTorrent, ClientPath))
                        keep.Add(TorrentService.InfoHashOf(_clientTorrent));
                }
                catch (LauncherApiException) { /* No client torrent on this realm: nothing to share. */ }

                foreach (var patch in Patches.Where(p => p.IsValid && p.Definition.Sha256.Length > 0))
                {
                    var fileName = RealmBranding.HostedPatchFileName(patch.Definition.SourceUrl);
                    // A patch can only be shared in place when it's installed under its server name.
                    if (fileName is null || !string.Equals(Path.GetFileName(patch.Destination), fileName, StringComparison.Ordinal))
                        continue;
                    try
                    {
                        var bytes = await PatchTorrentAsync(session, fileName, patch.Definition.Sha256);
                        if (await _torrents.ShareAsync(bytes, Path.GetDirectoryName(patch.Destination)!))
                            keep.Add(TorrentService.InfoHashOf(bytes));
                    }
                    catch (LauncherApiException) { }
                }
            }
            await _torrents.StopSharingExceptAsync(keep);
        }
        catch (Exception ex) { _sharingStatus = UserErrorService.Format(ex, "Sharing stopped"); OnPropertyChanged(nameof(SharingStatus)); }
        finally { _sharingLock.Release(); }
    }

    private async Task RunSharingStatusLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync())
        {
            var clientTorrent = _clientTorrent;
            var summary = await _torrents.SummaryAsync(clientTorrent is null ? null : TorrentService.InfoHashOf(clientTorrent));
            var status = !IsLoggedIn ? ""
                : !_savedSettings.ShareDownloads ? "Sharing with other players is off."
                : IsGameRunning ? "Sharing paused while you play."
                : summary.Shared == 0 ? ""
                : $"Sharing {DescribeShared(summary)} with {summary.Peers} player{(summary.Peers == 1 ? "" : "s")} · ↑ {FormatRate(summary.UploadRate)}";
            if (status != _sharingStatus)
            {
                _sharingStatus = status;
                OnPropertyChanged(nameof(SharingStatus));
                OnPropertyChanged(nameof(FooterText));
            }
        }
    }

    private async Task PauseSharingForGameAsync()
    {
        try { await _torrents.PauseAllAsync(); } catch (Exception) { }
    }

    private async Task ResumeSharingAfterGameAsync()
    {
        try { await _torrents.ResumeAllAsync(); } catch (Exception) { }
        await RefreshSharingAsync();
    }

    /// <summary>Stops every transfer and saves resume data; called as Portalkeeper closes.</summary>
    public Task ShutdownAsync()
    {
        // A client check holds the torrent engine for minutes: stop it rather than wait for it.
        CancelClientInstall();
        return Task.WhenAll(StopCombatLogUploadAsync(), _torrents.ShutdownAsync());
    }

    // ---------------------------------------------------------
    // Formatting
    // ---------------------------------------------------------

    private static string DescribeDownload(string what, TorrentProgress p)
    {
        if (p.Checking) return $"Checking your files against the realm's: {p.Percent:F0}% of {FormatSize(p.TotalBytes)}";
        var text = $"{what}: {p.Percent:F1}% · {FormatSize(p.DoneBytes)} of {FormatSize(p.TotalBytes)} · {FormatRate(p.DownloadRate)}";
        if (p.DownloadRate > 0 && p.TotalBytes > p.DoneBytes)
        {
            var left = TimeSpan.FromSeconds((p.TotalBytes - p.DoneBytes) / (double)p.DownloadRate);
            text += left.TotalHours >= 1 ? $" · {(int)left.TotalHours} h {left.Minutes} min left" : $" · {Math.Max(1, left.Minutes)} min left";
        }
        var players = $"{p.Peers} player{(p.Peers == 1 ? "" : "s")}";
        return text + " · " + (p.WebSeeds > 0 ? players + " + realm server" : players);
    }

    /// <summary>"the game and 5 patches", "the game", "3 patches".</summary>
    private static string DescribeShared(SharingSummary summary)
    {
        var patches = summary.PatchesShared == 0 ? "" : $"{summary.PatchesShared} patch{(summary.PatchesShared == 1 ? "" : "es")}";
        return !summary.ClientShared ? patches : patches.Length == 0 ? "the game" : "the game and " + patches;
    }

    private static string FormatSize(long bytes) => bytes >= 1L << 30
        ? $"{bytes / (double)(1L << 30):F1} GB"
        : $"{bytes / (double)(1L << 20):F0} MB";

    private static string FormatRate(long bytesPerSecond) => bytesPerSecond >= 1 << 20
        ? $"{bytesPerSecond / (double)(1 << 20):F1} MB/s"
        : $"{bytesPerSecond / 1024.0:F0} KB/s";
}
