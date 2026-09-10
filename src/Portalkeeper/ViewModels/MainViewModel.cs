using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Portalkeeper.Models;
using Portalkeeper.Services;

namespace Portalkeeper.ViewModels;

public sealed partial class MainViewModel : INotifyPropertyChanged
{
    private readonly GitHubAddonSourceService _gitHubAddonSourceService;
    private readonly AddonService _addonService;
    private readonly AddonInstallerService _addonInstallerService;
    private readonly PersonalAddonService _personalAddonService;
    private readonly ClientService _clientService;
    private readonly SettingsService _settingsService;
    private readonly RealmLaunchService _realmLaunchService;
    private readonly RealmHealthService _realmHealthService;
    private readonly RealmCalendarService _realmCalendarService;
    private readonly RealmNewsService _realmNewsService;
    private readonly RealmArmoryService _realmArmoryService;
    private readonly SemaphoreSlim _addonRefreshLock = new(1, 1);
    private readonly SemaphoreSlim _realmHealthLock = new(1, 1);

    private bool _isRefreshingConfiguration;
    private bool _isManagingComponents;
    private async Task RunComponentOperationAsync(Func<Task> operation)
    {
        if (_isManagingComponents || _isRefreshingConfiguration || IsLaunching || IsGameRunning)
            throw new InvalidOperationException("Wait for the current operation and close World of Warcraft before modifying components.");
        _isManagingComponents = true;
        OnPropertyChanged(nameof(CanEnterRealm)); OnPropertyChanged(nameof(CanSwitchRealm));
        try { await operation(); }
        finally { _isManagingComponents = false; OnPropertyChanged(nameof(CanEnterRealm)); OnPropertyChanged(nameof(CanSwitchRealm)); UpdateLaunchReadinessStatus(); }
    }
    private RealmInfo? _realmInfo;
    private readonly RealmConfigurationStore _realmStore = new();
    private readonly PatchService _patchService = new();
    private readonly SemaphoreSlim _configLock = new(1, 1);
    private string _configurationStatus = "Checking realm configuration...";
    public string ConfigurationStatus => _configurationStatus;
    public string ApplicationVersion => "Portalkeeper " + RealmConfigurationService.CurrentVersion;
    public IReadOnlyList<PatchInfo> Patches { get; private set; } = Array.Empty<PatchInfo>();
    public bool PatchesReady => Patches.All(p => p.Definition.Requirement != ComponentRequirement.Required || p.IsValid);
    public string RealmDescription => _realmInfo?.Description ?? "";
    public string RealmWebsite => _realmInfo?.WebsiteUrl ?? "";
    public string RealmConnection => _realmInfo is null ? "" : $"{_realmInfo.Address} • Auth {_realmInfo.AuthPort} • World {_realmInfo.WorldPort}";
    private void RefreshPatches()
    {
        Patches = _realmInfo?.Patches.Select(p => _patchService.Inspect(ClientPath, p)).ToArray() ?? Array.Empty<PatchInfo>();
        OnPropertyChanged(nameof(Patches));
        OnPropertyChanged(nameof(HasManagedPatches));
        OnPropertyChanged(nameof(PatchesReady));
        OnPropertyChanged(nameof(CanEnterRealm)); OnPropertyChanged(nameof(CanSwitchRealm));
        UpdateLaunchReadinessStatus();
    }
    public Task ManagePatchAsync(string id, bool remove) => RunComponentOperationAsync(() => ManagePatchCoreAsync(id, remove));
    private async Task ManagePatchCoreAsync(string id, bool remove)
    {
        if (IsGameRunning || IsLaunching) throw new InvalidOperationException("Close World of Warcraft before managing patches.");
        var patch = _realmInfo?.Patches.SingleOrDefault(p => p.Id == id) ?? throw new InvalidOperationException("Patch is not in the active realm configuration.");
        if (remove) _patchService.Remove(ClientPath, patch);
        else await _patchService.InstallAsync(ClientPath, patch);
        RefreshPatches();
    }


    private string _realmStatus =
        "No realm configuration available.";

    private RealmHealthState _realmHealthState =
        RealmHealthState.Unknown;

    private string _clientPath =
        "No client installation configured.";

    private string _clientStatus =
        "World of Warcraft 3.3.5a client required.";

    private bool _clientValid;

    private AddonManifest? _addonManifest;

    private IReadOnlyList<AddonInfo> _addons =
        Array.Empty<AddonInfo>();

    private string _addonStatus =
        "No addon manifest loaded.";

    private bool _addonsLoaded;
    private bool _isCheckingAddons;

    private bool _isLaunching;
    private bool _isGameRunning;
    private bool _hidePortalkeeperWhileGameRuns = true;
    private PortalkeeperSettings _savedSettings = new();
    private bool _transmogrificationSupported;
    public bool TransmogrificationSupported => _transmogrificationSupported;
    public string? TransmogAvailabilityHint => TransmogrificationSupported ? null :
        "This realm doesn’t support transmogrification.";
    public bool ShowTransmogrifiedAppearances
    {
        get => _savedSettings.ShowTransmogFor(ArmoryUrl);
        set
        {
            if (!TransmogrificationSupported || string.IsNullOrWhiteSpace(ArmoryUrl) || value == ShowTransmogrifiedAppearances) return;
            _savedSettings.ShowTransmogrifiedAppearancesByRealm[ArmoryUrl] = value;
            OnPropertyChanged();
            SaveSettings();
        }
    }
    private void SetTransmogSupported(bool supported)
    {
        _transmogrificationSupported = supported;
        OnPropertyChanged(nameof(TransmogrificationSupported));
        OnPropertyChanged(nameof(TransmogAvailabilityHint));
        OnPropertyChanged(nameof(ShowTransmogrifiedAppearances));
    }


    private string _launchStatus =
        "Checking configuration...";

    public MainViewModel(SettingsService? settingsService = null)
    {
        _gitHubAddonSourceService = new GitHubAddonSourceService();
        _addonService = new AddonService();
        _addonInstallerService = new AddonInstallerService();
        _personalAddonService = new PersonalAddonService();
        _clientService = new ClientService();
        _settingsService = settingsService ?? new SettingsService();
        _realmLaunchService = new RealmLaunchService();
        _realmHealthService = new RealmHealthService();
        _realmCalendarService = new RealmCalendarService();
        _realmNewsService = new RealmNewsService();
        _realmArmoryService = new RealmArmoryService();

        LoadSavedClient();
        _ = RediscoverRealmConfigurationAsync();
        _ = RunRealmHealthLoopAsync();
        _ = CheckForUpdatesAsync(manual: false);
    }

    public IReadOnlyList<AddonInfo> Addons =>
        _addons;

    public string AddonStatus =>
        _addonStatus;

    public bool AddonsLoaded =>
        _addonsLoaded;

    public bool IsCheckingAddons
    {
        get => _isCheckingAddons;
        private set
        {
            if (_isCheckingAddons == value)
                return;

            _isCheckingAddons = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AddonStatusSymbol));
            OnPropertyChanged(nameof(CanEnterRealm)); OnPropertyChanged(nameof(CanSwitchRealm));
        }
    }

    public bool AddonsReady =>
        _addonsLoaded &&
        _addons.All(addon =>
            !addon.Definition.Required ||
            (addon.IsInstalled && !addon.IsUpdateAvailable));

    public string AddonStatusSymbol =>
        IsCheckingAddons
            ? "● Checking"
            : AddonsReady
                ? "● Ready"
                : AddonsLoaded
                    ? "● Needs Attention"
                    : "● Not Configured";

    public Task RefreshAddonsAsync() => RediscoverRealmConfigurationAsync();

    public Task InstallOrUpdateAddonAsync(string addonId) => RunComponentOperationAsync(() => InstallOrUpdateAddonCoreAsync(addonId));
    private async Task InstallOrUpdateAddonCoreAsync(string addonId)
    {
        if (_isRefreshingConfiguration || IsGameRunning || IsLaunching) throw new InvalidOperationException("Wait for configuration checking and close World of Warcraft before modifying addons.");
        var addon = _addons.FirstOrDefault(item =>
            item.Definition.Id.Equals(
                addonId,
                StringComparison.OrdinalIgnoreCase));

        if (addon is null)
        {
            throw new InvalidOperationException(
                "Addon was not found in the active manifest.");
        }

        await _addonInstallerService.InstallOrUpdateAsync(
            ClientPath,
            addon.Definition);

        await LoadAddonsAsync();
    }

    public Task InstallOrUpdateAllAsync() => RunComponentOperationAsync(() => InstallOrUpdateAllCoreAsync());
    private async Task InstallOrUpdateAllCoreAsync()
    {
        if (_isRefreshingConfiguration || IsGameRunning || IsLaunching) throw new InvalidOperationException("Wait for configuration checking and close World of Warcraft before modifying addons.");
        var pending = _addons
            .Where(addon => addon.CanInstallOrUpdate)
            .ToArray();

        foreach (var addon in pending)
        {
            await _addonInstallerService.InstallOrUpdateAsync(
                ClientPath,
                addon.Definition);
        }

        await LoadAddonsAsync();
    }


    public async Task<AddonDefinition> DiscoverPersonalAddonAsync(string gitUrl)
    {
        if (string.IsNullOrWhiteSpace(gitUrl))
            throw new InvalidOperationException("Enter a GitHub repository URL.");

        var candidate = new AddonDefinition
        {
            Id = "personal-preview",
            Name = string.Empty,
            GitUrl = gitUrl.Trim(),
            IsPersonal = true
        };

        var resolved = await _gitHubAddonSourceService.ResolveAsync(candidate);

        if (_addons.Any(addon =>
                addon.Definition.GitUrl.Equals(
                    resolved.GitUrl,
                    StringComparison.OrdinalIgnoreCase) &&
                addon.Definition.AddonPath.Equals(
                    resolved.AddonPath,
                    StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "That addon is already managed by Portalkeeper.");
        }

        return resolved;
    }

    public async Task AddPersonalAddonAsync(
        AddonDefinition discovered,
        bool installIfMissing)
    {
        var source = new PersonalAddonSource
        {
            Id = "personal-" + Guid.NewGuid().ToString("N"),
            GitUrl = discovered.GitUrl,
            AddonPath = discovered.AddonPath
        };

        _personalAddonService.Add(source);
        await LoadAddonsAsync();

        if (!installIfMissing)
            return;

        var addon = _addons.FirstOrDefault(item =>
            item.Definition.Id.Equals(
                source.Id,
                StringComparison.OrdinalIgnoreCase));

        if (addon is not null && !addon.IsInstalled)
        {
            await _addonInstallerService.InstallOrUpdateAsync(
                ClientPath,
                addon.Definition);

            await LoadAddonsAsync();
        }
    }

    public Task RemoveInstalledAddonAsync(string addonId) => RunComponentOperationAsync(() => RemoveInstalledAddonCoreAsync(addonId));
    private async Task RemoveInstalledAddonCoreAsync(string addonId)
    {
        if (IsGameRunning || IsLaunching) throw new InvalidOperationException("Close World of Warcraft before removing addons.");
        var addon = _addons.Single(a => a.Definition.Id == addonId);
        _addonInstallerService.Remove(ClientPath, addon.Definition);
        await LoadAddonsAsync();
    }
    public async Task RemovePersonalAddonAsync(string addonId)
    {
        var addon = _addons.FirstOrDefault(item =>
            item.Definition.Id.Equals(
                addonId,
                StringComparison.OrdinalIgnoreCase));

        if (addon is null || !addon.Definition.IsPersonal)
        {
            throw new InvalidOperationException(
                "Only personal addons can be removed from management.");
        }

        _personalAddonService.Remove(addon.Definition.Id);
        await LoadAddonsAsync();
    }

    // ---------------------------------------------------------
    // Realm
    // ---------------------------------------------------------

    public string RealmName =>
        RealmConfigured
            ? _realmInfo!.Name
            : "No realm configuration loaded";

    public string RealmStatus =>
        _realmStatus;

    public bool RealmConfigured =>
        _realmInfo?.IsConfigured == true;

    public bool ShowRealmCheckAgain =>
        !RealmConfigured;

    public string RealmStatusSymbol =>
        !RealmConfigured
            ? "● Not Configured"
            : _realmHealthState switch
            {
                RealmHealthState.Checking => "● Checking",
                RealmHealthState.Online => "● Online",
                RealmHealthState.AuthOnly => "● Auth Only",
                RealmHealthState.WorldOnly => "● World Only",
                RealmHealthState.Offline => "● Offline",
                _ => "● Unknown"
            };

    public bool CalendarAvailable => RealmConfigured && !string.IsNullOrWhiteSpace(_realmInfo!.CalendarUrl);

    public async Task<RealmCalendarLoadResult> LoadCalendarAsync()
    {
        if (!CalendarAvailable || _realmInfo is null)
            return new RealmCalendarLoadResult(null, false, "This realm does not provide a calendar feed.");
        return await _realmCalendarService.LoadAsync(_realmInfo.CalendarUrl);
    }

    public bool ArmoryAvailable => RealmConfigured && !string.IsNullOrWhiteSpace(_realmInfo!.ArmoryUrl);

    public async Task<RealmArmoryIndexLoadResult> LoadArmoryAsync()
    {
        if (!ArmoryAvailable || _realmInfo is null)
            return new RealmArmoryIndexLoadResult(null, false, "This realm does not provide an armory feed.");
        var url = _realmInfo.ArmoryUrl;
        var result = await _realmArmoryService.LoadIndexAsync(url);
        if (url == ArmoryUrl) SetTransmogSupported(result.Feed?.Capabilities?.Transmogrification == true);
        return result;
    }

    public RealmArmoryService ArmoryService => _realmArmoryService;
    public string ArmoryUrl => _realmInfo?.ArmoryUrl ?? string.Empty;

    public bool NewsAvailable => RealmConfigured && !string.IsNullOrWhiteSpace(_realmInfo!.NewsUrl);

    public async Task<RealmNewsLoadResult> LoadNewsAsync()
    {
        if (!NewsAvailable || _realmInfo is null)
            return new RealmNewsLoadResult(null, false, "This realm does not provide a news feed.");
        return await _realmNewsService.LoadAsync(_realmInfo.NewsUrl);
    }

    // ---------------------------------------------------------
    // Client
    // ---------------------------------------------------------

    public string ClientPath
    {
        get => _clientPath;
        private set
        {
            if (_clientPath == value)
                return;

            _clientPath = value;
            OnPropertyChanged();
        }
    }

    public string ClientStatus
    {
        get => _clientStatus;
        private set
        {
            if (_clientStatus == value)
                return;

            _clientStatus = value;
            OnPropertyChanged();
        }
    }

    public bool ClientValid
    {
        get => _clientValid;
        private set
        {
            if (_clientValid == value)
                return;

            _clientValid = value;

            OnPropertyChanged();
            OnPropertyChanged(nameof(ClientStatusSymbol));
            OnPropertyChanged(nameof(ClientButtonText));
            OnPropertyChanged(nameof(CanEnterRealm)); OnPropertyChanged(nameof(CanSwitchRealm));
        }
    }

    public string ClientStatusSymbol =>
        ClientValid
            ? "● Ready"
            : "● Not Ready";

    public string ClientButtonText =>
        ClientValid
            ? "CHANGE CLIENT"
            : "LOCATE CLIENT";

    // ---------------------------------------------------------
    // Settings
    // ---------------------------------------------------------

    public bool HidePortalkeeperWhileGameRuns
    {
        get => _hidePortalkeeperWhileGameRuns;
        set
        {
            if (_hidePortalkeeperWhileGameRuns == value)
                return;

            _hidePortalkeeperWhileGameRuns = value;
            OnPropertyChanged();
            SaveSettings();
        }
    }

    public string LaunchEnvironmentStatus =>
        _realmLaunchService.GetLaunchEnvironmentSummary(ClientPath);

    // ---------------------------------------------------------
    // Launch readiness
    // ---------------------------------------------------------

    public bool IsLaunching
    {
        get => _isLaunching;
        private set
        {
            if (_isLaunching == value)
                return;

            _isLaunching = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanEnterRealm)); OnPropertyChanged(nameof(CanSwitchRealm));
            OnPropertyChanged(nameof(EnterRealmButtonText));
        }
    }

    public string LaunchStatus
    {
        get => _launchStatus;
        private set
        {
            if (_launchStatus == value)
                return;

            _launchStatus = value;
            OnPropertyChanged();
        }
    }

    public bool IsGameRunning
    {
        get => _isGameRunning;
        private set
        {
            if (_isGameRunning == value)
                return;

            _isGameRunning = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanEnterRealm)); OnPropertyChanged(nameof(CanSwitchRealm));
            OnPropertyChanged(nameof(EnterRealmButtonText));
        }
    }

    public string EnterRealmButtonText =>
        IsLaunching
            ? "LAUNCHING..."
            : IsGameRunning
                ? "WORLD OF WARCRAFT RUNNING"
                : "ENTER REALM";

    public bool CanEnterRealm =>
        !_isRefreshingConfiguration &&
        !_isManagingComponents &&
        ClientValid &&
        RealmConfigured &&
        AddonsReady &&
        PatchesReady &&
        !IsCheckingAddons &&
        !IsLaunching &&
        !IsGameRunning;

    public async Task EnterRealmAsync(Action? onLaunched = null)
    {
        if (!CanEnterRealm || _realmInfo is null)
            return;

        IsLaunching = true;
        LaunchStatus = "Preparing client...";

        RealmLaunchResult? result = null;

        try
        {
            ApplyClientInfo(_clientService.ValidateClient(ClientPath, _realmInfo.Client));
            RefreshPatches();
            if (!ClientValid || !PatchesReady) throw new InvalidOperationException(!ClientValid ? ClientStatus : "Required patches need attention.");
            var currentAddons = _addonService.InspectAddons(ClientPath, _addonManifest!);
            var unsatisfied = currentAddons.Where(a => a.Definition.Required && (!a.IsInstalled || a.IsUpdateAvailable)).ToArray();
            if (unsatisfied.Length > 0) throw new InvalidOperationException("Required addons need attention: " + string.Join(", ", unsatisfied.Select(a => a.Definition.Name)));
            result = _realmLaunchService.PrepareAndLaunch(
                ClientPath,
                _realmInfo);

            IsGameRunning = true;
            IsLaunching = false;
            LaunchStatus =
                $"World of Warcraft is running ({result.Locale}).";

            onLaunched?.Invoke();

            await _realmLaunchService.WaitForGameExitAsync(result);

            LaunchStatus = "World of Warcraft exited.";
        }
        catch (Exception ex)
        {
            LaunchStatus =
                UserErrorService.Format(ex, "Unable to launch World of Warcraft");
        }
        finally
        {
            IsLaunching = false;
            IsGameRunning = false;
        }
    }

    // ---------------------------------------------------------
    // Realm health
    // ---------------------------------------------------------

    public async Task RefreshRealmHealthAsync()
    {
        if (_realmInfo is null || !_realmInfo.IsConfigured)
            return;

        await _realmHealthLock.WaitAsync();

        try
        {
            var checkedRealm = _realmInfo;
            if (checkedRealm is null || !checkedRealm.IsConfigured) return;
            _realmHealthState = RealmHealthState.Checking;
            _realmStatus = "Checking realm server status...";
            NotifyRealmChanged();

            var health =
                await _realmHealthService.CheckAsync(checkedRealm);
            if (!ReferenceEquals(checkedRealm, _realmInfo)) return;

            _realmHealthState = health.State;
            _realmStatus = health.State switch
            {
                RealmHealthState.Online =>
                    "Authentication and world servers are reachable.",
                RealmHealthState.AuthOnly =>
                    "Authentication server is reachable; world server appears offline.",
                RealmHealthState.WorldOnly =>
                    "World server is reachable; authentication server appears offline.",
                RealmHealthState.Offline =>
                    "Realm servers appear offline or unreachable.",
                _ =>
                    "Realm server status could not be determined."
            };

            NotifyRealmChanged();
        }
        finally
        {
            _realmHealthLock.Release();
        }
    }

    private async Task RunRealmHealthLoopAsync()
    {
        using var timer =
            new PeriodicTimer(TimeSpan.FromSeconds(45));

        while (await timer.WaitForNextTickAsync())
        {
            await RefreshRealmHealthAsync();
        }
    }

    // ---------------------------------------------------------
    // Client operations
    // ---------------------------------------------------------

    public void SetClientDirectory(string directoryPath)
    {
        if (_isManagingComponents || IsLaunching || IsGameRunning) return;
        var client =
            _clientService.ValidateClient(directoryPath, _realmInfo?.Client);

        ApplyClientInfo(client);

        if (!client.IsSupportedClient)
            return;

        SaveSettings();
        OnPropertyChanged(nameof(LaunchEnvironmentStatus));

        RefreshPatches();
        _ = LoadAddonsAsync();
    }

    private void LoadSavedClient()
    {
        var settings =
            _settingsService.Load();

        _savedSettings = settings;
        _hidePortalkeeperWhileGameRuns =
            settings.HidePortalkeeperWhileGameRuns;

        if (string.IsNullOrWhiteSpace(settings.ClientPath))
            return;

        var client =
            _clientService.ValidateClient(
                settings.ClientPath);

        ApplyClientInfo(client);
        OnPropertyChanged(nameof(LaunchEnvironmentStatus));
    }

    private void SaveSettings()
    {
        if (ClientValid) _savedSettings.ClientPath = ClientPath;
        _savedSettings.HidePortalkeeperWhileGameRuns = HidePortalkeeperWhileGameRuns;
        _settingsService.Save(_savedSettings);
    }

    private void ApplyClientInfo(ClientInfo client)
    {
        ClientPath =
            string.IsNullOrWhiteSpace(client.DirectoryPath)
                ? "No client installation configured."
                : client.DirectoryPath;

        ClientStatus = client.StatusMessage;
        ClientValid = client.IsSupportedClient;
        UpdateLaunchReadinessStatus();
    }

    // ---------------------------------------------------------
    // Realm discovery
    // ---------------------------------------------------------

    public async Task RediscoverRealmConfigurationAsync(string? selectedPath = null)
    {
        if (_isManagingComponents || _isRefreshingConfiguration || IsGameRunning || IsLaunching) return;
        _isRefreshingConfiguration = true;
        OnPropertyChanged(nameof(CanEnterRealm)); OnPropertyChanged(nameof(CanSwitchRealm));
        try
        {
            await LoadRealmConfigurationAsync(selectedPath);
            await Task.WhenAll(RefreshRealmHealthAsync(), LoadAddonsAsync());
        }
        finally
        {
            _isRefreshingConfiguration = false;
            OnPropertyChanged(nameof(CanEnterRealm)); OnPropertyChanged(nameof(CanSwitchRealm));
            UpdateLaunchReadinessStatus();
        }
    }

    private async Task LoadRealmConfigurationAsync(string? selectedPath = null)
    {
        await _configLock.WaitAsync();
        try
        {
            SetTransmogSupported(false);
            RefreshRealmChoices();
            var selected = selectedPath is null
                ? RealmChoice.Select(AvailableRealms, _savedSettings.SelectedRealmPath)
                : AvailableRealms.FirstOrDefault(c => RealmChoice.SamePath(c.Path, selectedPath));
            _activeRealmPath = null;
            _realmInfo = null;
            _addonManifest = null;
            _addons = Array.Empty<AddonInfo>();
            _addonsLoaded = false;
            NotifyAddonsChanged();
            if (selected is null)
            {
                if (selectedPath is not null)
                    _configurationStatus = "The selected realm is no longer available. Choose a realm again in Settings.";
                else if (AvailableRealms.Count > 1)
                    _configurationStatus = "Choose your realm in Settings using CHANGE REALM.";
                else
                {
                    // Keep the existing actionable validation/MinimumVersion error for one unusable file.
                    var candidates = _realmStore.Discover(Directory.GetCurrentDirectory(), AppContext.BaseDirectory);
                    _configurationStatus = candidates.Count == 1
                        ? (await _realmStore.LoadAsync(candidates[0])).Status
                        : "Place a usable *.realm.conf in " + RealmConfigurationStore.DefaultDirectory + ", then click CHECK AGAIN.";
                }
            }
            else
            {
                var loadedPath = selected.Path;
                var result = await _realmStore.LoadAsync(selected.Path, migrated: path => loadedPath = path);
                _realmInfo = result.Realm;
                _configurationStatus = result.Status;
                if (_realmInfo?.IsConfigured == true)
                {
                    _activeRealmPath = loadedPath;
                    _savedSettings.SelectedRealmPath = loadedPath;
                    try { SaveSettings(); }
                    catch (Exception) { _configurationStatus += " The active realm could not be saved for next startup."; }
                }
                RefreshRealmChoices();
            }
            _realmStatus = _configurationStatus;
            _realmHealthState = RealmHealthState.Unknown;
            if (_realmInfo is not null)
            {
                ApplyClientInfo(_clientService.ValidateClient(ClientPath, _realmInfo.Client));
                if (ArmoryAvailable) _ = LoadArmoryAsync();
            }
        }
        catch (Exception ex)
        {
            _realmInfo = null;
            _configurationStatus = UserErrorService.Format(ex, "Realm configuration needs attention");
            _realmStatus = _configurationStatus;
        }
        finally
        {
            RefreshPatches();
            NotifyRealmChanged();
            _configLock.Release();
        }
    }

    private void NotifyRealmChanged()
    {
        OnPropertyChanged(nameof(ConfigurationStatus));
        OnPropertyChanged(nameof(SelectedRealmPath));
        OnPropertyChanged(nameof(ArmoryUrl));
        OnPropertyChanged(nameof(RealmDescription));
        OnPropertyChanged(nameof(RealmWebsite));
        OnPropertyChanged(nameof(RealmConnection));
        OnPropertyChanged(nameof(RealmName));
        OnPropertyChanged(nameof(RealmStatus));
        OnPropertyChanged(nameof(RealmConfigured));
        OnPropertyChanged(nameof(ShowRealmCheckAgain));
        OnPropertyChanged(nameof(RealmStatusSymbol));
        OnPropertyChanged(nameof(CalendarAvailable));
        OnPropertyChanged(nameof(NewsAvailable));
        OnPropertyChanged(nameof(ArmoryAvailable));
        OnPropertyChanged(nameof(CanEnterRealm)); OnPropertyChanged(nameof(CanSwitchRealm));
        UpdateLaunchReadinessStatus();
    }

    // ---------------------------------------------------------
    // Addons
    // ---------------------------------------------------------

    private async Task LoadAddonsAsync()
    {
        await _addonRefreshLock.WaitAsync();

        try
        {
            await LoadAddonsCoreAsync();
        }
        finally
        {
            _addonRefreshLock.Release();
        }
    }

    private async Task LoadAddonsCoreAsync()
    {
        if (!ClientValid)
        {
            _addonStatus =
                "Configure a valid WoW client before checking addons.";

            _addonsLoaded = false;
            IsCheckingAddons = false;
            NotifyAddonsChanged();
            UpdateLaunchReadinessStatus();
            return;
        }

        if (_realmInfo is null)
        {
            _addons = Array.Empty<AddonInfo>();
            _addonsLoaded = false;
            NotifyAddonsChanged();
            return;
        }

        IsCheckingAddons = true;
        _addonStatus = "Checking managed addons...";
        NotifyAddonsChanged();

        try
        {
            var rawManifest = new AddonManifest { Addons = _realmInfo.Addons.ToList() };
            var resolvedDefinitions = new List<AddonDefinition>();
            var realmSourceErrors = new Dictionary<string, string>();
            foreach (var definition in rawManifest.Addons)
            {
                try { resolvedDefinitions.Add(definition.IsGitHubSource ? await _gitHubAddonSourceService.ResolveAsync(definition) : definition); }
                catch (Exception ex)
                {
                    // Keep inspecting existing installs when the source is offline.
                    resolvedDefinitions.Add(definition);
                    realmSourceErrors[definition.Id] = UserErrorService.Format(ex);
                }
            }
            var resolvedRealmManifest = new AddonManifest { Addons = resolvedDefinitions.ToList() };

            // Realm policy always wins over a matching personal addon.
            // Reconcile before loading personal sources so the same addon cannot
            // appear twice or be updated independently by two management entries.
            _personalAddonService.ReconcileRealmManaged(
                resolvedRealmManifest.Addons);

            var personalSourceErrors =
                new List<AddonInfo>();

            foreach (var personal in _personalAddonService.Load())
            {
                var personalDefinition =
                    new AddonDefinition
                    {
                        Id = personal.Id,
                        GitUrl = personal.GitUrl,
                        AddonPath = personal.AddonPath,
                        IsPersonal = true
                    };

                try
                {
                    var resolved = await _gitHubAddonSourceService.ResolveAsync(personalDefinition);
                    if (!resolvedDefinitions.Any(d => d.Folder.Equals(resolved.Folder, StringComparison.OrdinalIgnoreCase)))
                        resolvedDefinitions.Add(resolved);
                }
                catch (Exception ex)
                {
                    personalSourceErrors.Add(
                        new AddonInfo
                        {
                            Definition = personalDefinition,
                            DiscoveryError = UserErrorService.Format(ex)
                        });
                }
            }

            _addonManifest =
                new AddonManifest
                {
                    ManifestVersion = resolvedRealmManifest.ManifestVersion,
                    Addons = resolvedDefinitions
                };

            _addons =
                _addonService.InspectAddons(
                    ClientPath,
                    _addonManifest)
                .Select(info => realmSourceErrors.TryGetValue(info.Definition.Id, out var error)
                    ? new AddonInfo { Definition = info.Definition, DirectoryPath = info.DirectoryPath,
                        IsInstalled = info.IsInstalled, InstalledVersion = info.InstalledVersion,
                        InstalledSourceCommit = info.InstalledSourceCommit, DiscoveryError = error }
                    : info)
                .Concat(personalSourceErrors)
                .ToArray();

            var installed =
                _addons.Count(addon => addon.IsInstalled);

            var sourceErrors =
                _addons.Count(addon => addon.IsSourceError);

            var missing =
                _addons.Count(addon =>
                    !addon.IsSourceError &&
                    !addon.IsInstalled && addon.Definition.Recommended);

            var requiredMissing =
                _addons.Count(addon =>
                    addon.Definition.Required &&
                    !addon.IsInstalled);

            var updatesAvailable =
                _addons.Count(addon => addon.IsUpdateAvailable);

            var requiredUpdates =
                _addons.Count(addon =>
                    addon.Definition.Required &&
                    addon.IsUpdateAvailable);

            if (requiredMissing > 0)
            {
                _addonStatus =
                    $"{installed}/{_addons.Count} managed addons installed; " +
                    $"{requiredMissing} required addon(s) missing.";
            }
            else if (requiredUpdates > 0)
            {
                _addonStatus =
                    $"{requiredUpdates} required addon update(s) available.";
            }
            else if (updatesAvailable > 0)
            {
                _addonStatus =
                    $"{updatesAvailable} recommended/optional/personal addon update(s) available.";
            }
            else if (sourceErrors > 0)
            {
                _addonStatus =
                    $"Realm addons ready; {sourceErrors} addon source(s) unavailable; existing installations retained.";
            }
            else if (missing > 0)
            {
                _addonStatus =
                    $"{installed}/{_addons.Count} managed addons installed; " +
                    $"{missing} recommended addon(s) available to install.";
            }
            else
            {
                _addonStatus =
                    $"Realm addon requirements satisfied; {installed}/{_addons.Count} managed addons installed.";
            }

            _addonsLoaded = true;
            NotifyAddonsChanged();
        }
        catch (Exception ex)
        {
            _addonManifest = null;
            _addons = Array.Empty<AddonInfo>();

            _addonStatus =
                UserErrorService.Format(ex, "Unable to load addon configuration");

            _addonsLoaded = false;
            NotifyAddonsChanged();
        }
        finally
        {
            IsCheckingAddons = false;
            NotifyAddonsChanged();
            UpdateLaunchReadinessStatus();
        }
    }

    private void NotifyAddonsChanged()
    {
        OnPropertyChanged(nameof(AddonStatus));
        OnPropertyChanged(nameof(AddonsLoaded));
        OnPropertyChanged(nameof(AddonsReady));
        OnPropertyChanged(nameof(AddonStatusSymbol));
        OnPropertyChanged(nameof(Addons));
        OnPropertyChanged(nameof(CanEnterRealm)); OnPropertyChanged(nameof(CanSwitchRealm));
    }

    private void UpdateLaunchReadinessStatus()
    {
        if (IsLaunching || IsGameRunning)
            return;

        if (!RealmConfigured)
        {
            LaunchStatus = ConfigurationStatus;
            return;
        }

        if (!ClientValid)
        {
            LaunchStatus = "Locate a supported World of Warcraft 3.3.5a client.";
            return;
        }

        if (IsCheckingAddons)
        {
            LaunchStatus = "Checking managed addons...";
            return;
        }

        if (!AddonsLoaded)
        {
            LaunchStatus = "Addon configuration needs attention before launch.";
            return;
        }

        if (!AddonsReady)
        {
            LaunchStatus = "Required addons need attention: " + string.Join(", ", _addons.Where(a => a.Definition.Required && (!a.IsInstalled || a.IsUpdateAvailable)).Select(a => a.Definition.Name + " (" + a.StatusText + ")"));
            return;
        }

        if (!PatchesReady)
        {
            LaunchStatus = "Required patches need attention: " + string.Join(", ", Patches.Where(p => p.Definition.Requirement == ComponentRequirement.Required && !p.IsValid).Select(p => p.Definition.Name + " (" + p.Status + ")"));
            return;
        }
        LaunchStatus = "Ready to enter realm.";
    }

    // ---------------------------------------------------------
    // Property notification
    // ---------------------------------------------------------

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(
        [CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(propertyName));
    }
}
