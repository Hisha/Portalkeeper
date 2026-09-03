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
	private readonly AddonService _addonService;
    private readonly ClientService _clientService;
    private readonly SettingsService _settingsService;
    private readonly RealmConfigurationService _realmConfigurationService;

    private RealmInfo? _realmInfo;

    private string _realmStatus =
        "No realm configuration available.";

    private string _clientPath =
        "No client installation configured.";

    private string _clientStatus =
        "World of Warcraft 3.3.5a client required.";

    private bool _clientValid;

	private AddonManifest? _addonManifest;

	public IReadOnlyList<AddonInfo> Addons =>
    _addons;
    
    private IReadOnlyList<AddonInfo> _addons =
    Array.Empty<AddonInfo>();
	
	private string _addonStatus =
	    "No addon manifest loaded.";
	
	private bool _addonsLoaded;
	
	public string AddonStatus =>
	    _addonStatus;
	
	public bool AddonsLoaded =>
	    _addonsLoaded;
	    
	public bool AddonsReady =>
    _addonsLoaded &&
    _addons.All(addon =>
        !addon.Definition.Required ||
        addon.IsInstalled);
	
	public string AddonStatusSymbol =>
    AddonsReady
        ? "● Ready"
        : AddonsLoaded
            ? "● Needs Attention"
            : "● Not Configured";
	
	public MainViewModel()
    {
        _addonManifestService =
	    	new AddonManifestService();
		_addonService =
	    	new AddonService();
        _clientService = new ClientService();
        _settingsService = new SettingsService();
        _realmConfigurationService =
            new RealmConfigurationService();

        LoadSavedClient();
        LoadRealmConfiguration();
        _ = LoadAddonsAsync();
    }
    
    public Task RefreshAddonsAsync()
	{
	    return LoadAddonsAsync();
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
            {
                return;
            }

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
            {
                return;
            }

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
            {
                return;
            }

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
    // Launch readiness
    // ---------------------------------------------------------

    public bool CanEnterRealm =>
        ClientValid &&
        RealmConfigured&&
   		AddonsReady;

    // ---------------------------------------------------------
    // Client operations
    // ---------------------------------------------------------

    public void SetClientDirectory(string directoryPath)
    {
        var client =
            _clientService.ValidateClient(directoryPath);

        ApplyClientInfo(client);

        if (!client.IsSupportedClient)
        {
            return;
        }

        _settingsService.Save(
            new PortalkeeperSettings
            {
                ClientPath = client.DirectoryPath
            });
            
            _ = LoadAddonsAsync();
    }

    private void LoadSavedClient()
    {
        var settings =
            _settingsService.Load();

        if (string.IsNullOrWhiteSpace(settings.ClientPath))
        {
            return;
        }

        var client =
            _clientService.ValidateClient(
                settings.ClientPath);

        ApplyClientInfo(client);
    }

    private void ApplyClientInfo(ClientInfo client)
    {
        ClientPath =
            string.IsNullOrWhiteSpace(client.DirectoryPath)
                ? "No client installation configured."
                : client.DirectoryPath;

        ClientStatus =
            client.StatusMessage;

        ClientValid =
            client.IsSupportedClient;
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

    private static List<string>
        FindRealmConfigurationFiles()
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

        var files =
            new List<string>();

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
	        !string.IsNullOrWhiteSpace(
	            _realmInfo.ManifestUrl))
	    {
	        manifestLocation =
	            _realmInfo.ManifestUrl;
	    }
	
	    if (string.IsNullOrWhiteSpace(manifestLocation))
	    {
	        var localManifest =
	            Path.Combine(
	                Directory.GetCurrentDirectory(),
	                "config",
	                "addons.json");
	
	        if (File.Exists(localManifest))
	        {
	            manifestLocation =
	                localManifest;
	        }
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
	        _addonManifest =
	            await _addonManifestService.LoadAsync(
	                manifestLocation);
	
	        _addons =
	            _addonService.InspectAddons(
	                ClientPath,
	                _addonManifest);
	
	        var installed =
	            _addons.Count(addon =>
	                addon.IsInstalled);
	
	        var missing =
	            _addons.Count - installed;
	
	        var requiredMissing =
	            _addons.Count(addon =>
	                addon.Definition.Required &&
	                !addon.IsInstalled);
	
	        if (requiredMissing > 0)
	        {
	            _addonStatus =
	                $"{installed}/{_addons.Count} managed addons installed; " +
	                $"{requiredMissing} required addon(s) missing.";
	        }
	        else if (missing > 0)
	        {
	            _addonStatus =
	                $"{installed}/{_addons.Count} managed addons installed; " +
	                $"{missing} optional/recommended addon(s) missing.";
	        }
	        else
	        {
	            _addonStatus =
	                $"All {_addons.Count} managed addons are installed.";
	        }
	
	        _addonsLoaded = true;
	        NotifyAddonsChanged();
	    }
	    catch (Exception ex)
	    {
	        _addonManifest = null;
	        _addons =
	            Array.Empty<AddonInfo>();
	
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

    public event PropertyChangedEventHandler?
        PropertyChanged;

    private void OnPropertyChanged(
        [CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(propertyName));
    }
}