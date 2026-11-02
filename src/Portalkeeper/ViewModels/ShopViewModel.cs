using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Portalkeeper.Models;
using Portalkeeper.Services;

namespace Portalkeeper.ViewModels;

/// <summary>
/// Evermore fork: the Shop window. Spends the Tarnished Undermine Reals in the account's wallet on
/// mounts, companions and character services for one of the account's characters
/// (<see cref="CoinShopService"/>). The listing is asked for when the window opens, after every
/// purchase and on REFRESH; the portal keeps it for 15 seconds, so nothing here polls.
/// Laid out like the retail game's shop: tiles on the left, the picked offer large on a stage on
/// the right, as a turntable picture the player can turn when the realm has one.
/// </summary>
public sealed class ShopViewModel : ViewModelBase, IDisposable
{
    /// <summary>Pixels across a tile's picture frame (decoded that small: a strip per tile stays a few MB).</summary>
    public const int TileFrameWidth = 168;
    /// <summary>Full-size stage pictures kept decoded, so going back to one is instant.</summary>
    private const int KeepStagePictures = 4;

    private readonly CoinShopService _service;
    private readonly ShopPreviewService _previews;
    private readonly LauncherSession _session;
    private readonly Dictionary<Uri, Task<Bitmap?>> _tilePictures = [];
    private readonly List<(Uri Url, Bitmap Picture)> _stagePictures = [];
    private CancellationTokenSource? _stageLoad;
    private int _stageVersion;
    private ShopOfferView? _selectedOffer;
    private Bitmap? _stagePicture;
    private bool _isStageLoading;
    private readonly CancellationTokenSource _closed = new();
    private readonly List<ShopOfferView> _allOffers = [];
    private int _loadVersion;

    private bool _isLoading;
    private bool _isBuying;
    private bool _hasListing;
    private string _loadError = "";
    private int _balance;
    private ShopCharacterView? _selectedCharacter;
    private ShopCategoryView? _selectedCategory;
    private ShopSortView _selectedSort = ShopSortView.Choices[0];
    private ShopShowView _selectedShow = ShopShowView.Choices[0];
    private string _searchText = "";
    private string _statusMessage = "";
    private bool _statusIsError;
    private ShopOfferView? _pendingOffer;
    private ShopCharacterView? _pendingCharacter;

    public ShopViewModel(CoinShopService service, LauncherSession session, ShopPreviewService? previews = null)
    {
        _service = service;
        _session = session;
        _previews = previews ?? new ShopPreviewService();
    }

    /// <summary>Raised when the portal says the login is no longer good (401/403), with its message.</summary>
    public event Action<string>? LoginExpired;

    public ObservableCollection<ShopCharacterView> Characters { get; } = [];
    public ObservableCollection<ShopCategoryView> Categories { get; } = [];
    public ObservableCollection<ShopOfferView> Offers { get; } = [];
    /// <summary>The tiles as they're drawn: <see cref="Offers"/> under their headings, the way the realm lists them.</summary>
    public ObservableCollection<ShopOfferSection> OfferSections { get; } = [];
    public IReadOnlyList<ShopSortView> Sorts => ShopSortView.Choices;
    public IReadOnlyList<ShopShowView> Shows => ShopShowView.Choices;

    public bool IsLoading { get => _isLoading; private set { if (SetProperty(ref _isLoading, value)) NotifyBusy(); } }
    public bool IsBuying { get => _isBuying; private set { if (SetProperty(ref _isBuying, value)) NotifyBusy(); } }
    public bool IsBusy => _isLoading || _isBuying;
    public bool CanRefresh => !IsBusy;

    public bool HasListing { get => _hasListing; private set { if (SetProperty(ref _hasListing, value)) NotifyContent(); } }
    public string LoadError { get => _loadError; private set { if (SetProperty(ref _loadError, value)) NotifyContent(); } }
    public bool HasLoadError => _loadError.Length > 0 && !_hasListing;
    /// <summary>The first listing is still on its way.</summary>
    public bool ShowFirstLoad => _isLoading && !_hasListing && _loadError.Length == 0;
    public bool ShowShop => _hasListing;
    public bool HasNoCharacters => _hasListing && Characters.Count == 0;
    public bool HasCharacters => Characters.Count > 0;
    public bool HasNoOffers => _hasListing && Offers.Count == 0;
    /// <summary>Why the tile column is empty: nothing for sale, or nothing left after the search and Show filter.</summary>
    public string NoOffersText => IsFiltered ? "Nothing here matches. Clear the search or show everything." : "Nothing is for sale right now.";
    public bool HasBalance => _hasListing;

    public int Balance
    {
        get => _balance;
        private set
        {
            if (!SetProperty(ref _balance, value)) return;
            OnPropertyChanged(nameof(BalanceText));
            OnPropertyChanged(nameof(BalanceCaption));
            UpdateOfferStates();
        }
    }
    public string BalanceText => _balance.ToString("N0", CultureInfo.CurrentCulture);
    public string BalanceCaption => _balance == 1 ? "Tarnished Undermine Real" : "Tarnished Undermine Reals";

    public ShopCharacterView? SelectedCharacter
    {
        get => _selectedCharacter;
        set
        {
            if (!SetProperty(ref _selectedCharacter, value)) return;
            UpdateOfferStates();
        }
    }

    public ShopCategoryView? SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (value is null || !SetProperty(ref _selectedCategory, value)) return;
            foreach (var category in Categories) category.IsSelected = ReferenceEquals(category, value);
            FilterOffers();
        }
    }

    /// <summary>How the tab's tiles are ordered. The shop's own order (the realm's sort) is the default.</summary>
    public ShopSortView SelectedSort
    {
        get => _selectedSort;
        set
        {
            if (value is null || !SetProperty(ref _selectedSort, value)) return;
            FilterOffers();
        }
    }

    /// <summary>Which of the tab's offers show: everything, or only what can be bought, isn't owned, or is owned.</summary>
    public ShopShowView SelectedShow
    {
        get => _selectedShow;
        set
        {
            if (value is null || !SetProperty(ref _selectedShow, value)) return;
            OnPropertyChanged(nameof(IsFiltered));
            FilterOffers();
        }
    }

    /// <summary>Narrows the tab to offers whose name or description has this text.</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value ?? "")) return;
            OnPropertyChanged(nameof(IsFiltered));
            FilterOffers();
        }
    }

    /// <summary>A search or a Show choice other than everything is hiding offers.</summary>
    public bool IsFiltered => _searchText.Trim().Length > 0 || !_selectedShow.IsEverything;

    /// <summary>Back to everything in the tab, in the shop's order kept as it is.</summary>
    public void ClearFilters()
    {
        var changed = false;
        if (_searchText.Length > 0) { _searchText = ""; OnPropertyChanged(nameof(SearchText)); changed = true; }
        if (!_selectedShow.IsEverything) { _selectedShow = ShopShowView.Choices[0]; OnPropertyChanged(nameof(SelectedShow)); changed = true; }
        if (!changed) return;
        OnPropertyChanged(nameof(IsFiltered));
        FilterOffers();
    }

    /// <summary>The offer on the stage. Picking one on a tile only shows it; BUY on the stage buys it.</summary>
    public ShopOfferView? SelectedOffer
    {
        get => _selectedOffer;
        set
        {
            if (ReferenceEquals(_selectedOffer, value)) return;
            if (_selectedOffer is not null) _selectedOffer.IsSelected = false;
            _selectedOffer = value;
            if (value is not null) value.IsSelected = true;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelectedOffer));
            OnPropertyChanged(nameof(ShowStageIcon));
            OnPropertyChanged(nameof(StageFrames));
            _ = ShowOnStageAsync(value);
        }
    }
    public bool HasSelectedOffer => _selectedOffer is not null;

    /// <summary>The picked offer's turntable picture at full size, once it's here.</summary>
    public Bitmap? StagePicture
    {
        get => _stagePicture;
        private set
        {
            if (!SetProperty(ref _stagePicture, value)) return;
            OnPropertyChanged(nameof(StageFrames));
            OnPropertyChanged(nameof(HasStagePicture));
            OnPropertyChanged(nameof(ShowStageIcon));
        }
    }
    public int StageFrames => _stagePicture is null ? 1 : _selectedOffer?.Offer.Preview?.Frames ?? 1;
    public bool HasStagePicture => _stagePicture is not null;
    public bool IsStageLoading { get => _isStageLoading; private set { if (SetProperty(ref _isStageLoading, value)) OnPropertyChanged(nameof(ShowStageIcon)); } }
    /// <summary>The drawn icon stands in on the stage for an offer without a picture (or one that didn't load).</summary>
    public bool ShowStageIcon => _selectedOffer is not null && _stagePicture is null && !_isStageLoading;

    public string StatusMessage { get => _statusMessage; private set { if (SetProperty(ref _statusMessage, value)) OnPropertyChanged(nameof(HasStatus)); } }
    public bool StatusIsError { get => _statusIsError; private set => SetProperty(ref _statusIsError, value); }
    public bool HasStatus => _statusMessage.Length > 0;

    public ShopOfferView? PendingOffer
    {
        get => _pendingOffer;
        private set
        {
            if (!SetProperty(ref _pendingOffer, value)) return;
            OnPropertyChanged(nameof(IsConfirming));
            OnPropertyChanged(nameof(ConfirmText));
            OnPropertyChanged(nameof(ConfirmDetail));
        }
    }
    public bool IsConfirming => _pendingOffer is not null;
    /// <summary>An account-wide offer is bought for the account; anything else for the picked character.</summary>
    public string ConfirmText => _pendingOffer is null || _pendingCharacter is null ? ""
        : _pendingOffer.IsFree ? $"Get {_pendingOffer.Name} for free for {ConfirmWho}?"
        : $"Buy {_pendingOffer.Name} for {_pendingOffer.PriceText} for {ConfirmWho}?";
    private string ConfirmWho => _pendingOffer is { IsAccount: true } ? "your account" : _pendingCharacter?.Name ?? "";
    public string ConfirmDetail => _pendingOffer is null ? ""
        : (_pendingOffer.IsFree ? ""
            : _balance >= _pendingOffer.Price
            ? $"You have {BalanceText} {CoinWord(_balance)}; {(_balance - _pendingOffer.Price).ToString("N0", CultureInfo.CurrentCulture)} will be left. "
            : $"You have {BalanceText} {CoinWord(_balance)}, which isn't enough for this. ")
          + _pendingOffer.Description
          + (_pendingOffer.IsAccount ? (_pendingOffer.Description.Length > 0 ? " " : "") + ShopOfferView.AccountText : "");

    private static string CoinWord(int count) => count == 1 ? "coin" : "coins";

    /// <summary>Asks the portal for the listing. A purchase's message stays up when <paramref name="keepStatus"/>.</summary>
    public async Task LoadAsync(bool keepStatus = false)
    {
        var version = ++_loadVersion;
        IsLoading = true;
        if (!keepStatus) SetStatus("", false);
        try
        {
            var listing = await _service.GetListingAsync(_session, _closed.Token);
            if (version != _loadVersion) return;
            if (listing is null)
            {
                LoadError = "This realm has no shop.";
                HasListing = false;
                return;
            }
            Apply(listing);
            LoadError = "";
        }
        catch (LauncherApiException ex) when (ex.Status == HttpStatusCode.Unauthorized)
        {
            if (version != _loadVersion) return;
            ShowLoadError(ex.Message, keepStatus);
            LoginExpired?.Invoke(ex.Message);
        }
        catch (LauncherApiException ex)
        {
            if (version == _loadVersion) ShowLoadError(ex.Message, keepStatus);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidDataException
            or InvalidOperationException or NotSupportedException)
        {
            if (_closed.IsCancellationRequested || version != _loadVersion) return;
            ShowLoadError(UserErrorService.Format(ex, "Couldn't reach the shop"), keepStatus);
        }
        finally
        {
            if (version == _loadVersion) IsLoading = false;
        }
    }

    /// <summary>
    /// Shown where the listing would be when there's none yet; with a listing up it stays up (the
    /// balance may be a little old) and the error shows in the status line. After a purchase
    /// (<paramref name="afterPurchase"/>) what the purchase did stays first: a "may have gone
    /// through" must not be replaced by the refresh that followed it.
    /// </summary>
    private void ShowLoadError(string message, bool afterPurchase)
    {
        if (!_hasListing) LoadError = message;
        else if (afterPurchase && HasStatus) SetStatus(_statusMessage + " The shop couldn't be refreshed: " + message, _statusIsError);
        else SetStatus(message, true);
    }

    /// <summary>
    /// BUY on a card: the confirm step, for the character picked right now (a listing that arrives
    /// meanwhile doesn't change what was asked; the game server checks it again anyway).
    /// </summary>
    public void AskToBuy(ShopOfferView offer)
    {
        if (IsBuying || !offer.CanBuy || _selectedCharacter is null) return;
        _pendingCharacter = _selectedCharacter;
        PendingOffer = offer;
        // Asked again for the same offer with another character: the text has to follow.
        OnPropertyChanged(nameof(ConfirmText));
        OnPropertyChanged(nameof(ConfirmDetail));
    }

    public void CancelBuy() => PendingOffer = null;

    private async Task ShowOnStageAsync(ShopOfferView? offer)
    {
        var version = ++_stageVersion;
        // A newer pick drops the load of the last one: clicking through tiles decodes only what stays.
        _stageLoad?.Cancel();
        _stageLoad?.Dispose();
        _stageLoad = null;
        if (offer?.Offer.Preview is not { } preview)
        {
            IsStageLoading = false;
            StagePicture = null;
            return;
        }
        var kept = _stagePictures.FindIndex(p => p.Url == preview.Url);
        if (kept >= 0)
        {
            var entry = _stagePictures[kept];
            _stagePictures.RemoveAt(kept);
            _stagePictures.Add(entry);
            IsStageLoading = false;
            StagePicture = entry.Picture;
            return;
        }

        // The tile's small picture stands in until the full one is here.
        IsStageLoading = true;
        StagePicture = offer.Picture;
        var load = _stageLoad = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token);
        Bitmap? picture = null;
        try
        {
            picture = await _previews.LoadAsync(preview, cancellationToken: load.Token);
        }
        finally
        {
            if (version == _stageVersion && !_closed.IsCancellationRequested) IsStageLoading = false;
        }
        if (version != _stageVersion || _closed.IsCancellationRequested)
        {
            // Picked away (or closed) while it loaded: nobody is going to show it.
            picture?.Dispose();
            return;
        }
        if (picture is null)
        {
            // Not kept: picking it again tries again. The small one stays up meanwhile.
            StagePicture = offer.Picture;
            return;
        }
        _stagePictures.Add((preview.Url, picture));
        while (_stagePictures.Count > KeepStagePictures)
        {
            var old = _stagePictures[0];
            _stagePictures.RemoveAt(0);
            if (ReferenceEquals(old.Picture, _stagePicture)) _retired.Add(old.Picture);
            else old.Picture.Dispose();
        }
        StagePicture = picture;
        DisposeRetired();
    }

    // Stage pictures dropped from the keep list while still on screen: disposed once they're not.
    private readonly List<Bitmap> _retired = [];

    private void DisposeRetired()
    {
        foreach (var picture in _retired.Where(p => !ReferenceEquals(p, _stagePicture)).ToList())
        {
            _retired.Remove(picture);
            picture.Dispose();
        }
    }

    /// <summary>Each tile's small picture, fetched once per URL for as long as the window is open.</summary>
    private async Task LoadTilePictureAsync(ShopOfferView offer)
    {
        if (offer.Offer.Preview is not { } preview) return;
        if (!_tilePictures.TryGetValue(preview.Url, out var load))
            _tilePictures[preview.Url] = load = _previews.LoadAsync(preview, TileFrameWidth, _closed.Token);
        var picture = await load;
        // Closed meanwhile: Dispose only got the loads that had finished.
        if (_closed.IsCancellationRequested)
        {
            picture?.Dispose();
            return;
        }
        offer.Picture = picture;
        if (ReferenceEquals(offer, _selectedOffer) && _isStageLoading && _stagePicture is null) StagePicture = picture;
    }

    public async Task ConfirmBuyAsync()
    {
        var offer = _pendingOffer;
        var character = _pendingCharacter;
        PendingOffer = null;
        if (offer is null || character is null || IsBuying) return;

        IsBuying = true;
        SetStatus($"Buying {offer.Name} for {character.Name}...", false);
        ShopPurchaseResult result;
        try { result = await _service.BuyAsync(_session, offer.Offer.Id, character.Guid); }
        finally { IsBuying = false; }

        switch (result.Outcome)
        {
            case ShopPurchaseOutcome.Bought:
                SetStatus(result.Message, false);
                if (result.Balance is int balance) Balance = balance;
                break;
            case ShopPurchaseOutcome.LoggedOut:
                SetStatus(result.Message, true);
                LoginExpired?.Invoke(result.Message);
                return;
            case ShopPurchaseOutcome.NotTried:
                // Turned away before the game server was asked: nothing changed, nothing to fetch.
                SetStatus(result.Message, true);
                return;
            default:
                SetStatus(result.Message, true);
                break;
        }
        if (_closed.IsCancellationRequested) return;
        await LoadAsync(keepStatus: true);
    }

    private void SetStatus(string message, bool error)
    {
        StatusIsError = error;
        StatusMessage = message;
    }

    private void Apply(ShopListing listing)
    {
        var selectedGuid = _selectedCharacter?.Guid;
        var selectedCategory = _selectedCategory?.Key;

        Characters.Clear();
        foreach (var character in listing.Characters
                     .OrderByDescending(c => c.Level).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
            Characters.Add(new ShopCharacterView(character));

        _allOffers.Clear();
        _allOffers.AddRange(listing.Offers.Select(o => new ShopOfferView(o)));
        foreach (var offer in _allOffers) _ = LoadTilePictureAsync(offer);

        // One tab per category in reading order; the shop opens on mounts. There's no "all" tab: the
        // search and Show filter work within a tab, and the tabs are few.
        Categories.Clear();
        var keys = _allOffers.Select(o => o.Offer.Category).Distinct().OrderBy(ShopCategoryView.Order).ThenBy(k => k, StringComparer.Ordinal).ToList();
        foreach (var key in keys) Categories.Add(new ShopCategoryView(key, _allOffers.Count(o => o.Offer.Category == key)));

        HasListing = true;
        if (_balance != listing.Balance)
        {
            _balance = listing.Balance;
            OnPropertyChanged(nameof(Balance));
            OnPropertyChanged(nameof(BalanceText));
            OnPropertyChanged(nameof(BalanceCaption));
        }

        _selectedCategory = null;
        SelectedCategory = Categories.FirstOrDefault(c => c.Key == selectedCategory)
            ?? Categories.FirstOrDefault(c => c.Key == ShopCategoryView.DefaultKey)
            ?? Categories.FirstOrDefault();
        if (_selectedCategory is null) FilterOffers();

        // Set without the setter's guard: the cards are new and need their states either way.
        _selectedCharacter = Characters.FirstOrDefault(c => c.Guid == selectedGuid)
            ?? Characters.FirstOrDefault(c => c.Online)
            ?? Characters.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedCharacter));
        UpdateOfferStates();
        NotifyContent();
    }

    private void FilterOffers()
    {
        Offers.Clear();
        var key = _selectedCategory?.Key;
        var search = _searchText.Trim();
        var shown = _allOffers.Where(o => key is null || o.Offer.Category == key);
        if (search.Length > 0)
            shown = shown.Where(o => o.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                || o.Description.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                || o.Offer.Section.Contains(search, StringComparison.CurrentCultureIgnoreCase));
        shown = _selectedShow.Apply(shown);
        foreach (var offer in _selectedSort.Apply(shown)) Offers.Add(offer);
        UpdateOfferSections();
        OnPropertyChanged(nameof(HasNoOffers));
        OnPropertyChanged(nameof(NoOffersText));
        // The stage shows something of this tab: the offer it showed if that's here (after a refresh
        // it's a new tile for the same offer, and its picture stays up), else the first tile.
        SelectedOffer = Offers.FirstOrDefault(o => o.Offer.Id == _selectedOffer?.Offer.Id) ?? Offers.FirstOrDefault();
    }

    /// <summary>
    /// The Sort choice orders the tiles inside a heading; the headings themselves stay in the realm's
    /// order (the first offer of each in the listing), so the years don't shuffle with the prices.
    /// </summary>
    private void UpdateOfferSections()
    {
        OfferSections.Clear();
        var order = _allOffers.Select(o => o.Offer.Section).Distinct().ToList();
        foreach (var group in Offers.GroupBy(o => o.Offer.Section).OrderBy(g => order.IndexOf(g.Key)))
            OfferSections.Add(new ShopOfferSection(group.Key, group.ToList()));
    }

    private void UpdateOfferStates()
    {
        foreach (var offer in _allOffers) offer.Update(_selectedCharacter, _balance);
        OnPropertyChanged(nameof(ConfirmText));
        OnPropertyChanged(nameof(ConfirmDetail));
        // Owned and "can buy" follow the character and the balance, so a Show filter on them does too.
        if (!_selectedShow.IsEverything) FilterOffers();
    }

    private void NotifyBusy()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanRefresh));
        OnPropertyChanged(nameof(ShowFirstLoad));
    }

    private void NotifyContent()
    {
        OnPropertyChanged(nameof(HasLoadError));
        OnPropertyChanged(nameof(ShowFirstLoad));
        OnPropertyChanged(nameof(ShowShop));
        OnPropertyChanged(nameof(HasNoCharacters));
        OnPropertyChanged(nameof(HasCharacters));
        OnPropertyChanged(nameof(HasNoOffers));
        OnPropertyChanged(nameof(NoOffersText));
        OnPropertyChanged(nameof(HasBalance));
    }

    public void Dispose()
    {
        if (_closed.IsCancellationRequested) return;
        _closed.Cancel();
        _closed.Dispose();
        // The window is gone with everything that drew them.
        _stageLoad?.Dispose();
        _stageLoad = null;
        foreach (var (_, picture) in _stagePictures) picture.Dispose();
        foreach (var picture in _retired) picture.Dispose();
        foreach (var load in _tilePictures.Values)
            if (load.IsCompletedSuccessfully) load.Result?.Dispose();
        _stagePictures.Clear();
        _retired.Clear();
        _tilePictures.Clear();
    }
}

public sealed class ShopCharacterView(ShopCharacter character)
{
    public ShopCharacter Character { get; } = character;
    public uint Guid => Character.Guid;
    public string Name => Character.Name.Length > 0 ? Character.Name : "Character " + Character.Guid;
    public bool Online => Character.Online;
    public string Detail => $"Level {Character.Level} {ArmoryNames.RaceName(Character.Race)} {ArmoryNames.ClassName(Character.Class)}";
    public IBrush ClassBrush { get; } = ArmoryNames.ClassBrush(character.Class);
}

/// <summary>Tiles under a heading (a BlizzCon's year). Without a title there's no heading, only the tiles.</summary>
public sealed class ShopOfferSection(string title, IReadOnlyList<ShopOfferView> offers)
{
    public string Title { get; } = title;
    public bool HasTitle => Title.Length > 0;
    public IReadOnlyList<ShopOfferView> Offers { get; } = offers;
}

/// <summary>A filter tab: one offer category.</summary>
public sealed class ShopCategoryView(string key, int count) : ViewModelBase
{
    /// <summary>The tab the shop opens on when it has one.</summary>
    public const string DefaultKey = "mount";
    private bool _isSelected;

    public string Key { get; } = key;
    public string Title { get; } = TitleOf(key);
    public int Count { get; } = count;
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }

    /// <summary>The known categories first, in the order the shop is meant to be read.</summary>
    public static int Order(string key) => key switch { "mount" => 0, "pet" => 1, "blizzcon" => 2, "recruit" => 3, "service" => 4, _ => 5 };

    public static string TitleOf(string key) => key switch
    {
        "mount" => "MOUNTS",
        "pet" => "COMPANIONS",
        "service" => "SERVICES",
        "blizzcon" => "BLIZZCON",
        "recruit" => "RECRUIT-A-FRIEND",
        "" => "OTHER",
        _ => key.ToUpperInvariant(),
    };
}

/// <summary>An order for the tiles of a tab.</summary>
public sealed class ShopSortView
{
    public static readonly IReadOnlyList<ShopSortView> Choices =
    [
        new("shop", "Shop order", offers => offers),
        new("name", "Name A to Z", offers => offers.OrderBy(o => o.Name, StringComparer.CurrentCultureIgnoreCase)),
        new("price-asc", "Price: low to high", offers => offers.OrderBy(o => o.Price).ThenBy(o => o.Name, StringComparer.CurrentCultureIgnoreCase)),
        new("price-desc", "Price: high to low", offers => offers.OrderByDescending(o => o.Price).ThenBy(o => o.Name, StringComparer.CurrentCultureIgnoreCase)),
    ];

    private readonly Func<IEnumerable<ShopOfferView>, IEnumerable<ShopOfferView>> _order;

    private ShopSortView(string key, string title, Func<IEnumerable<ShopOfferView>, IEnumerable<ShopOfferView>> order)
    {
        Key = key;
        Title = title;
        _order = order;
    }

    public string Key { get; }
    public string Title { get; }
    public IEnumerable<ShopOfferView> Apply(IEnumerable<ShopOfferView> offers) => _order(offers);
    public override string ToString() => Title;
}

/// <summary>Which of a tab's offers show. Owned and "can buy" are for the picked character.</summary>
public sealed class ShopShowView
{
    public static readonly IReadOnlyList<ShopShowView> Choices =
    [
        new("all", "Everything", _ => true),
        new("buyable", "Can buy now", o => o.CanBuy),
        new("unowned", "Not owned", o => !o.IsOwned),
        new("owned", "Owned", o => o.IsOwned),
    ];

    private readonly Func<ShopOfferView, bool> _keep;

    private ShopShowView(string key, string title, Func<ShopOfferView, bool> keep)
    {
        Key = key;
        Title = title;
        _keep = keep;
    }

    public string Key { get; }
    public string Title { get; }
    public bool IsEverything => Key == "all";
    public IEnumerable<ShopOfferView> Apply(IEnumerable<ShopOfferView> offers) => IsEverything ? offers : offers.Where(_keep);
    public override string ToString() => Title;
}

/// <summary>One offer card. Whether it can be bought depends on the picked character and the balance.</summary>
public sealed class ShopOfferView : ViewModelBase
{
    private string _stateText = "";
    private bool _canBuy;
    private bool _isOwned;
    private bool _isLocked;
    private bool _isSelected;
    private Bitmap? _picture;

    public ShopOfferView(ShopOffer offer)
    {
        Offer = offer;
        AccentBrush = Brush.Parse(offer.Category switch
        {
            "mount" => "#E8B64C", "pet" => "#7CC7A0", "service" => "#9DB7F0", _ => "#C9A8E8",
        });
    }

    public ShopOffer Offer { get; }
    public string Name => Offer.Name.Length > 0 ? Offer.Name : "Offer " + Offer.Id;
    public string Description => Offer.Description;
    public int Price => Offer.Price;
    public string PriceText => Offer.Price.ToString("N0", CultureInfo.CurrentCulture) + (Offer.Price == 1 ? " coin" : " coins");
    public string PriceNumber => IsFree ? "Free" : Offer.Price.ToString("N0", CultureInfo.CurrentCulture);
    /// <summary>A 0-coin offer: "Free" in place of the coin and the number.</summary>
    public bool IsFree => Offer.Price == 0;
    public bool ShowCoin => !IsFree;
    public string CategoryTitle => ShopCategoryView.TitleOf(Offer.Category);
    /// <summary>Bought for the whole account: the realm shares it, so no character is picked and nothing is mailed.</summary>
    public bool IsAccount => Offer.Account;
    public const string AccountText = "Every character on your account gets it.";
    public string AccountCaption => AccountText;
    /// <summary>The tile's turntable picture, small, once it's here; until then (or without one) the drawn icon.</summary>
    public Bitmap? Picture { get => _picture; set { if (SetProperty(ref _picture, value)) OnPropertyChanged(nameof(ShowIcon)); } }
    public int PictureFrames => Offer.Preview?.Frames ?? 1;
    public bool ShowIcon => _picture is null;
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
    // Which drawn icon the tile shows (ShopWindow.axaml) when there's no picture.
    public bool IsMount => Offer.Category == "mount";
    public bool IsPet => Offer.Category == "pet";
    public bool IsService => Offer.Category == "service";
    public bool IsOther => !IsMount && !IsPet && !IsService;
    public IBrush AccentBrush { get; }

    /// <summary>Why it can't be bought for the picked character, or empty.</summary>
    public string StateText { get => _stateText; private set { if (SetProperty(ref _stateText, value)) OnPropertyChanged(nameof(HasState)); } }
    public bool HasState => _stateText.Length > 0;
    public bool CanBuy { get => _canBuy; private set => SetProperty(ref _canBuy, value); }
    public bool IsOwned { get => _isOwned; private set => SetProperty(ref _isOwned, value); }
    /// <summary>Not for sale to the account yet (an owned one shows as owned): none of its characters has come far enough.</summary>
    public bool IsLocked { get => _isLocked; private set => SetProperty(ref _isLocked, value); }
    /// <summary>What opens a locked offer, for the stage: the server's sentence as it is.</summary>
    public string UnlockText => Offer.Unlock.Length > 0
        ? Offer.Unlock
        : "None of your characters has come far enough to buy this yet.";

    public void Update(ShopCharacterView? character, int balance)
    {
        IsOwned = character is not null && Offer.Owned.Contains(character.Guid);
        IsLocked = Offer.Locked && !IsOwned;
        var cannot = character is not null && Offer.Cannot.Contains(character.Guid);
        StateText = character is null ? "No character"
            : IsOwned ? (Offer.Category == "service" ? "Already waiting" : "Owned")
            : IsLocked ? "Locked"
            : cannot ? "Can't use"
            : balance < Offer.Price ? $"Need {(Offer.Price - Math.Max(0, balance)).ToString("N0", CultureInfo.CurrentCulture)} more"
            : "";
        CanBuy = StateText.Length == 0;
    }
}
