using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.IO;
using Portalkeeper.Models;
using Portalkeeper.Services;

namespace Portalkeeper.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly ClientService _clientService;
    private readonly SettingsService _settingsService;
    private readonly RealmConfigurationService _realmConfigurationService;

    private RealmInfo? _realmInfo;
    private string _clientPath = "No client installation configured.";
    private string _clientStatus = "World of Warcraft 3.3.5a client required.";
    private bool _clientValid;

    public MainViewModel()
    {
        _clientService = new ClientService();
	    _settingsService = new SettingsService();
	    _realmConfigurationService = new RealmConfigurationService();
	
	    LoadSavedClient();
	    LoadRealmConfiguration();
    }

    public string RealmName =>
    _realmInfo?.IsConfigured == true
        ? _realmInfo.Name
        : "No realm configuration loaded";

	public string RealmStatus =>
    _realmInfo?.IsConfigured == true
        ? $"Realm address configured: {_realmInfo.Address}"
        : "No realm configuration available.";

	public bool RealmConfigured =>
    _realmInfo?.IsConfigured == true;

	public string RealmStatusSymbol =>
    RealmConfigured
        ? "● Ready"
        : "● Not Configured";

    public string AddonStatus => "No addon manifest loaded.";

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
            OnPropertyChanged(nameof(CanEnterRealm));
        }
    }

    public string ClientStatusSymbol =>
        ClientValid ? "● Ready" : "● Not Ready";

    public bool CanEnterRealm =>
        ClientValid && RealmConfigured;

    public void SetClientDirectory(string directoryPath)
    {
        var client = _clientService.ValidateClient(directoryPath);

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
    }

	private void LoadRealmConfiguration()
	{
	    var candidates = new[]
	    {
	        Path.Combine(
	            Directory.GetCurrentDirectory(),
	            "eitrigg.conf"),
	
	        Path.Combine(
	            AppContext.BaseDirectory,
	            "eitrigg.conf")
	    };
	
	    foreach (var candidate in candidates)
	    {
	        if (!File.Exists(candidate))
	        {
	            continue;
	        }
	
	        try
	        {
	            _realmInfo =
	                _realmConfigurationService.Load(candidate);
	
	            OnPropertyChanged(nameof(RealmName));
	            OnPropertyChanged(nameof(RealmStatus));
	            OnPropertyChanged(nameof(RealmConfigured));
	            OnPropertyChanged(nameof(RealmStatusSymbol));
	            OnPropertyChanged(nameof(CanEnterRealm));
	
	            return;
	        }
	        catch
	        {
	            // We'll add proper logging/error reporting later.
	        }
	    }
	}

    private void LoadSavedClient()
    {
        var settings = _settingsService.Load();

        if (string.IsNullOrWhiteSpace(settings.ClientPath))
        {
            return;
        }

        var client =
            _clientService.ValidateClient(settings.ClientPath);

        ApplyClientInfo(client);
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

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(
        [CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(propertyName));
    }
}