using System;
using System.IO;
using System.Threading.Tasks;
using Portalkeeper.Services;

namespace Portalkeeper.ViewModels;

// Explicit developer/test entry point for isolated runtime CONSTRUCTION and
// OPTIONAL explicit test LAUNCH (Checkpoint 2). This is intentionally NOT the
// normal realm flow: it never changes Settings.ClientPath, never alters
// RealmRuntimeResolver behavior, never launches through the normal ENTER REALM
// path, and never cleans or migrates anything.
public sealed partial class MainViewModel
{
    private readonly ManagedRuntimeBuilder _runtimeBuilder = new();
    private readonly ManagedRuntimeValidator _runtimeValidator = new();
    private string _runtimeTestStatus =
        "Experimental: construct an isolated test runtime from the selected source client. " +
        "It will not become the normal launch client.";
    private string? _constructedRuntimePath;

    public string RuntimeTestStatus
    {
        get => _runtimeTestStatus;
        private set
        {
            if (_runtimeTestStatus == value)
                return;

            _runtimeTestStatus = value;
            OnPropertyChanged();
        }
    }

    public string? ConstructedRuntimePath => _constructedRuntimePath;

    public string ProposedRuntimePath
    {
        get
        {
            if (!RealmConfigured || _realmInfo is null)
                return "Select a realm to see the proposed runtime location.";

            try
            {
                return RuntimePaths.RuntimeRootOfRealm(
                    RuntimePaths.DefaultRuntimeRoot(),
                    Models.RealmIdentity.FromRealm(_realmInfo));
            }
            catch
            {
                return "The proposed runtime location could not be determined.";
            }
        }
    }

    public bool CanConstructRuntime =>
        ClientValid &&
        RealmConfigured &&
        !_isRefreshingConfiguration &&
        !_isManagingComponents &&
        !IsLaunching &&
        !IsGameRunning;

    public bool CanLaunchConstructedRuntime =>
        RealmConfigured &&
        ClientValid &&
        !string.IsNullOrWhiteSpace(_constructedRuntimePath) &&
        Directory.Exists(_constructedRuntimePath) &&
        !IsLaunching &&
        !IsGameRunning;

    public async Task ConstructTestRuntimeAsync()
    {
        if (!CanConstructRuntime || _realmInfo is null)
            return;

        RuntimeTestStatus = "Constructing test runtime... (source client is not modified)";

        try
        {
            var result = await Task.Run(() =>
                _runtimeBuilder.Build(new ManagedRuntimeBuildOptions
                {
                    SourceClientPath = ClientPath,
                    Realm = _realmInfo
                }));

            _constructedRuntimePath = result.RuntimePath;
            RuntimeTestStatus =
                $"Test runtime constructed at {result.RuntimePath} (locale {result.Locale}). " +
                "This is a Portalkeeper-owned isolated runtime only; ENTER REALM still uses " +
                "your normal client and Settings.ClientPath is unchanged. Constructing again for " +
                "the same realm requires choosing a different runtime root or removing that " +
                "runtime directory.";
        }
        catch (Exception ex)
        {
            _constructedRuntimePath = null;
            RuntimeTestStatus =
                UserErrorService.Format(ex, "Test runtime construction failed");
        }
        finally
        {
            OnPropertyChanged(nameof(ConstructedRuntimePath));
            OnPropertyChanged(nameof(CanConstructRuntime));
            OnPropertyChanged(nameof(CanLaunchConstructedRuntime));
        }
    }

    // Extremely explicit second developer/test action: launch the CONSTRUCTED
    // runtime through the existing RealmLaunchService. This does not change
    // Settings.ClientPath, the normal resolver, or the regular ENTER REALM path.
    public async Task LaunchConstructedRuntimeAsync()
    {
        if (!CanLaunchConstructedRuntime || _realmInfo is null || _constructedRuntimePath is null)
            return;

        IsLaunching = true;
        LaunchStatus = "Launching constructed test runtime...";

        try
        {
            // Never launch something that is not a complete, manifest-backed
            // runtime, even on the explicit developer/test path.
            var validation = _runtimeValidator.Validate(
                _constructedRuntimePath,
                _realmInfo!,
                ClientPath,
                expectedFinalRuntimePath: _constructedRuntimePath);

            if (!validation.IsValid)
            {
                LaunchStatus =
                    "Refusing to launch: the constructed runtime is no longer valid. " +
                    Environment.NewLine + string.Join(Environment.NewLine, validation.Errors);
                IsLaunching = false;
                return;
            }

            if (_realmInfo.Client.RuntimeMode == Portalkeeper.Models.ClientRuntimeMode.Isolated)
            {
                var source = ClientPath;
                var realm = _realmInfo;
                var root = System.IO.Path.GetDirectoryName(_constructedRuntimePath);
                await Task.Run(() => new RealmRuntimePreparationService(root).PrepareAsync(source, realm));
            }

            var result = _realmLaunchService.PrepareAndLaunch(
                _constructedRuntimePath,
                _realmInfo, ClientPath);

            IsGameRunning = true;
            IsLaunching = false;
            LaunchStatus =
                $"Constructed runtime is running ({result.Locale}). This is NOT the normal " +
                "ENTER REALM launch path.";

            await _realmLaunchService.WaitForGameExitAsync(result);

            LaunchStatus = "Constructed runtime exited.";
        }
        catch (Exception ex)
        {
            LaunchStatus =
                UserErrorService.Format(ex, "Unable to launch the constructed test runtime");
        }
        finally
        {
            IsLaunching = false;
            IsGameRunning = false;
            OnPropertyChanged(nameof(CanConstructRuntime));
            OnPropertyChanged(nameof(CanLaunchConstructedRuntime));
        }
    }

    public void ForgetConstructedRuntime()
    {
        _constructedRuntimePath = null;
        OnPropertyChanged(nameof(ConstructedRuntimePath));
        OnPropertyChanged(nameof(CanLaunchConstructedRuntime));
    }
}