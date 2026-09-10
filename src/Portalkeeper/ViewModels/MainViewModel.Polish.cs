using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Portalkeeper.Models;
using Portalkeeper.Services;
namespace Portalkeeper.ViewModels;

public sealed partial class MainViewModel
{
    private readonly PortalkeeperReleaseService _releaseService = new();
    private string? _activeRealmPath;
    private string? _releaseTag;
    private string _availableReleaseVersion = "";
    private string _updateStatus = "Check for newer stable Portalkeeper releases.";
    private bool _isCheckingUpdates;
    public IReadOnlyList<RealmChoice> AvailableRealms { get; private set; } = Array.Empty<RealmChoice>();
    public string? SelectedRealmPath => _activeRealmPath;
    public bool CanChangeRealm => AvailableRealms.Count > 1;
    public bool CanSwitchRealm => !_isRefreshingConfiguration && !_isManagingComponents && !IsLaunching && !IsGameRunning;
    public bool HasManagedPatches => _realmInfo?.Patches.Count > 0;
    public bool CanCheckForUpdates => !_isCheckingUpdates;
    public string UpdateStatus => _updateStatus;
    public bool IsUpdateAvailable => _availableReleaseVersion.Length > 0;
    public string AvailableReleaseText => $"Portalkeeper {_availableReleaseVersion} is available";

    public void RefreshRealmChoices()
    {
        AvailableRealms = _realmStore.GetAvailableRealms(System.IO.Directory.GetCurrentDirectory(), AppContext.BaseDirectory);
        OnPropertyChanged(nameof(AvailableRealms));
        OnPropertyChanged(nameof(CanChangeRealm));
    }
    public async Task<bool> SelectRealmAsync(string path)
    {
        if (!CanSwitchRealm || !AvailableRealms.Any(c => RealmChoice.SamePath(c.Path, path))) return false;
        await RediscoverRealmConfigurationAsync(path);
        return _realmInfo?.IsConfigured == true;
    }
    public async Task CheckForUpdatesAsync(bool manual = true)
    {
        if (_isCheckingUpdates) return;
        _isCheckingUpdates = true;
        if (manual) _updateStatus = "Checking for updates...";
        OnPropertyChanged(nameof(CanCheckForUpdates));
        OnPropertyChanged(nameof(UpdateStatus));
        try
        {
            var result = await _releaseService.CheckAsync(_savedSettings, RealmConfigurationService.CurrentVersion,
                manual, settings => _settingsService.Save(settings));
            if (result.State != ReleaseCheckState.Failed)
            {
                _releaseTag = result.State == ReleaseCheckState.Available ? result.Tag : null;
                _availableReleaseVersion = result.State == ReleaseCheckState.Available ? result.Version! : "";
                _updateStatus = result.Message;
            }
            else if (manual) _updateStatus = result.Message;
        }
        finally
        {
            _isCheckingUpdates = false;
            OnPropertyChanged(nameof(CanCheckForUpdates));
            OnPropertyChanged(nameof(UpdateStatus));
            OnPropertyChanged(nameof(IsUpdateAvailable));
            OnPropertyChanged(nameof(AvailableReleaseText));
        }
    }
    public void ViewRelease()
    {
        var uri = PortalkeeperReleaseService.ReleaseUri(_releaseTag);
        if (uri is null) return;
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception)
        {
            _updateStatus = "Unable to open the release page in your browser.";
            OnPropertyChanged(nameof(UpdateStatus));
        }
    }
}
