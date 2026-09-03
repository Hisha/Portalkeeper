using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Portalkeeper.Models;
using Portalkeeper.Services;

namespace Portalkeeper.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
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

    public MainViewModel()
    {
        _clientService = new ClientService();
        _settingsService = new SettingsService();
        _realmConfigurationService =
            new RealmConfigurationService();

        LoadSavedClient();
        LoadRealmConfiguration();
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
    // Addons
    // ---------------------------------------------------------

    public string AddonStatus =>
        "No addon manifest loaded.";

    // ---------------------------------------------------------
    // Launch readiness
    // ---------------------------------------------------------

    public bool CanEnterRealm =>
        ClientValid &&
        RealmConfigured;

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