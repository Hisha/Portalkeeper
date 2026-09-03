using System.ComponentModel;
using System.Runtime.CompilerServices;
using Portalkeeper.Models;
using Portalkeeper.Services;

namespace Portalkeeper.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly ClientService _clientService;
    private readonly SettingsService _settingsService;

    private string _clientPath = "No client installation configured.";
    private string _clientStatus = "World of Warcraft 3.3.5a client required.";
    private bool _clientValid;

    public MainWindowViewModel()
    {
        _clientService = new ClientService();
        _settingsService = new SettingsService();

        LoadSavedClient();
    }

    public string RealmName => "No realm configuration loaded";

    public string RealmStatus => "Realm configuration will be added next.";

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
        ClientValid;

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