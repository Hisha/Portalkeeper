using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Portalkeeper.Models;
using Portalkeeper.Services;

namespace Portalkeeper.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly AddonManifestService _addonManifestService;
    private readonly GitHubAddonSourceService _gitHubAddonSourceService;
    private readonly AddonService _addonService;
    private readonly AddonInstallerService _addonInstallerService;
    private readonly PersonalAddonService _personalAddonService;
    private readonly ClientService _clientService;
    private readonly SettingsService _settingsService;
    private readonly RealmConfigurationService _realmConfigurationService;
    private readonly RealmLaunchService _realmLaunchService;

    private RealmInfo? _realmInfo;

    private string _realmStatus =
        "No realm configuration available.";

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

    private bool _isLaunching;
    private bool _isGameRunning;
    private bool _hidePortalkeeperWhileGameRuns = true;

    private string _launchStatus =
        "Ready to enter realm.";

    public MainViewModel()
    {
        _addonManifestService = new AddonManifestService();
        _gitHubAddonSourceService = new GitHubAddonSourceService();
        _addonService = new AddonService();
        _addonInstallerService = new AddonInstallerService();
        _personalAddonService = new PersonalAddonService();
        _clientService = new ClientService();
        _settingsService = new SettingsService();
        _realmConfigurationService = new RealmConfigurationService();
        _realmLaunchService = new RealmLaunchService();

        LoadSavedClient();
        LoadRealmConfiguration();
        _ = LoadAddonsAsync();
    }

    public IReadOnlyList<AddonInfo> Addons =>
        _addons;

    public string AddonStatus =>
        _addonStatus;

    public bool AddonsLoaded =>
        _addonsLoaded;

    public bool AddonsReady =>
        _addonsLoaded &&
        _addons.All(addon =>
            !addon.Definition.Required ||
            (addon.IsInstalled && !addon.IsUpdateAvailable));

    public string AddonStatusSymbol =>
        AddonsReady
            ? "● Ready"
            : AddonsLoaded
                ? "● Needs Attention"
                : "● Not Configured";

    public Task RefreshAddonsAsync()
    {
        return LoadAddonsAsync();
    }

    public async Task InstallOrUpdateAddonAsync(string addonId)
    {
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

    public async Task InstallOrUpdateAllAsync()
    {
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

    public string RealmStatusSymbol =>
        RealmConfigured
            ? "● Ready"
            : "● Not Configured";

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
            OnPropertyChanged(nameof(CanEnterRealm));
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
            OnPropertyChanged(nameof(CanEnterRealm));
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
            OnPropertyChanged(nameof(CanEnterRealm));
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
        ClientValid &&
        RealmConfigured &&
        AddonsReady &&
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
                $"Unable to launch World of Warcraft: {ex.Message}";
        }
        finally
        {
            IsLaunching = false;
            IsGameRunning = false;
        }
    }

    // ---------------------------------------------------------
    // Client operations
    // ---------------------------------------------------------

    public void SetClientDirectory(string directoryPath)
    {
        var client =
            _clientService.ValidateClient(directoryPath);

        ApplyClientInfo(client);

        if (!client.IsSupportedClient)
            return;

        SaveSettings();
        OnPropertyChanged(nameof(LaunchEnvironmentStatus));

        _ = LoadAddonsAsync();
    }

    private void LoadSavedClient()
    {
        var settings =
            _settingsService.Load();

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
        _settingsService.Save(
            new PortalkeeperSettings
            {
                ClientPath = ClientValid ? ClientPath : string.Empty,
                HidePortalkeeperWhileGameRuns = HidePortalkeeperWhileGameRuns
            });
    }

    private void ApplyClientInfo(ClientInfo client)
    {
        ClientPath =
            string.IsNullOrWhiteSpace(client.DirectoryPath)
                ? "No client installation configured."
                : client.DirectoryPath;

        ClientStatus = client.StatusMessage;
        ClientValid = client.IsSupportedClient;
    }

    // ---------------------------------------------------------
    // Realm discovery
    // ---------------------------------------------------------

    private void LoadRealmConfiguration()
    {
        var candidates =
            FindRealmConfigurationFiles();

        if (candidates.Count == 0)
        {
            _realmInfo = null;
            _realmStatus =
                "Place a *.realm.conf file beside Portalkeeper.";

            NotifyRealmChanged();
            return;
        }

        if (candidates.Count > 1)
        {
            _realmInfo = null;
            _realmStatus =
                $"Multiple realm configurations found ({candidates.Count}).";

            NotifyRealmChanged();
            return;
        }

        try
        {
            var realm =
                _realmConfigurationService.Load(
                    candidates[0]);

            if (!realm.IsConfigured)
            {
                _realmInfo = null;
                _realmStatus =
                    "Realm configuration is missing required settings.";

                NotifyRealmChanged();
                return;
            }

            _realmInfo = realm;
            _realmStatus =
                "Realm configuration loaded.";

            NotifyRealmChanged();
        }
        catch (Exception ex)
        {
            _realmInfo = null;
            _realmStatus =
                $"Unable to load realm configuration: {ex.Message}";

            NotifyRealmChanged();
        }
    }

    private static List<string> FindRealmConfigurationFiles()
    {
        var searchDirectories =
            new[]
            {
                Directory.GetCurrentDirectory(),
                AppContext.BaseDirectory
            }
            .Where(Directory.Exists)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var files = new List<string>();

        foreach (var directory in searchDirectories)
        {
            files.AddRange(
                Directory.EnumerateFiles(
                    directory,
                    "*.realm.conf",
                    SearchOption.TopDirectoryOnly));
        }

        return files
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(
                path => path,
                StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void NotifyRealmChanged()
    {
        OnPropertyChanged(nameof(RealmName));
        OnPropertyChanged(nameof(RealmStatus));
        OnPropertyChanged(nameof(RealmConfigured));
        OnPropertyChanged(nameof(RealmStatusSymbol));
        OnPropertyChanged(nameof(CanEnterRealm));
    }

    // ---------------------------------------------------------
    // Addons
    // ---------------------------------------------------------

    private async Task LoadAddonsAsync()
    {
        if (!ClientValid)
        {
            _addonStatus =
                "Configure a valid WoW client before checking addons.";

            _addonsLoaded = false;
            NotifyAddonsChanged();
            return;
        }

        string? manifestLocation = null;

        if (_realmInfo is not null &&
            !string.IsNullOrWhiteSpace(_realmInfo.ManifestUrl))
        {
            manifestLocation = _realmInfo.ManifestUrl;
        }

        if (string.IsNullOrWhiteSpace(manifestLocation))
        {
            var localManifest = Path.Combine(
                Directory.GetCurrentDirectory(),
                "config",
                "addons.json");

            if (File.Exists(localManifest))
                manifestLocation = localManifest;
        }

        if (string.IsNullOrWhiteSpace(manifestLocation))
        {
            _addonStatus =
                "No addon manifest available.";

            _addonsLoaded = false;
            NotifyAddonsChanged();
            return;
        }

        try
        {
            var rawManifest =
                await _addonManifestService.LoadAsync(
                    manifestLocation);

            var resolvedRealmManifest =
                await _gitHubAddonSourceService.ResolveManifestAsync(
                    rawManifest);

            var resolvedDefinitions =
                resolvedRealmManifest.Addons.ToList();

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
                    resolvedDefinitions.Add(
                        await _gitHubAddonSourceService.ResolveAsync(
                            personalDefinition));
                }
                catch (Exception ex)
                {
                    personalSourceErrors.Add(
                        new AddonInfo
                        {
                            Definition = personalDefinition,
                            DiscoveryError = ex.Message
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
                .Concat(personalSourceErrors)
                .ToArray();

            var installed =
                _addons.Count(addon => addon.IsInstalled);

            var sourceErrors =
                _addons.Count(addon => addon.IsSourceError);

            var missing =
                _addons.Count(addon =>
                    !addon.IsSourceError &&
                    !addon.IsInstalled);

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
                    $"Realm addons ready; {sourceErrors} personal addon source(s) need attention.";
            }
            else if (missing > 0)
            {
                _addonStatus =
                    $"{installed}/{_addons.Count} managed addons installed; " +
                    $"{missing} optional/recommended/personal addon(s) missing.";
            }
            else
            {
                _addonStatus =
                    $"All {_addons.Count} managed addons are current.";
            }

            _addonsLoaded = true;
            NotifyAddonsChanged();
        }
        catch (Exception ex)
        {
            _addonManifest = null;
            _addons = Array.Empty<AddonInfo>();

            _addonStatus =
                $"Unable to load addon manifest: {ex.Message}";

            _addonsLoaded = false;
            NotifyAddonsChanged();
        }
    }

    private void NotifyAddonsChanged()
    {
        OnPropertyChanged(nameof(AddonStatus));
        OnPropertyChanged(nameof(AddonsLoaded));
        OnPropertyChanged(nameof(AddonsReady));
        OnPropertyChanged(nameof(AddonStatusSymbol));
        OnPropertyChanged(nameof(Addons));
        OnPropertyChanged(nameof(CanEnterRealm));
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
