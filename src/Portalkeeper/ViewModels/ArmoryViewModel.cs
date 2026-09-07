using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Portalkeeper.Models;
using Portalkeeper.Services;

namespace Portalkeeper.ViewModels;

public sealed class ArmoryViewModel : INotifyPropertyChanged
{
    private readonly RealmArmoryService _service;
    private readonly string _indexUrl;
    private readonly List<ArmoryCharacterSummary> _all;

    private string _searchText = string.Empty;
    private int _filterIndex;
    private ArmoryCharacterSummary? _selectedSummary;
    private ArmoryCharacter? _selectedCharacter;
    private string _status;
    private bool _loadingProfile;

    public ArmoryViewModel(RealmArmoryIndex feed, string indexUrl, string status, RealmArmoryService service)
    {
        _service = service;
        _indexUrl = indexUrl;
        _status = status;
        _all = feed.Characters.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();

        Characters = new ObservableCollection<ArmoryCharacterSummary>();
        LeftEquipmentSlots = new ObservableCollection<ArmoryEquipmentSlotView>();
        RightEquipmentSlots = new ObservableCollection<ArmoryEquipmentSlotView>();
        BottomEquipmentSlots = new ObservableCollection<ArmoryEquipmentSlotView>();

        ApplyFilter();
    }

    public ObservableCollection<ArmoryCharacterSummary> Characters { get; }
    public ObservableCollection<ArmoryEquipmentSlotView> LeftEquipmentSlots { get; }
    public ObservableCollection<ArmoryEquipmentSlotView> RightEquipmentSlots { get; }
    public ObservableCollection<ArmoryEquipmentSlotView> BottomEquipmentSlots { get; }

    public string Status { get => _status; private set { _status = value; OnPropertyChanged(); } }
    public bool LoadingProfile { get => _loadingProfile; private set { _loadingProfile = value; OnPropertyChanged(); } }
    public bool HasSelectedCharacter => SelectedCharacter is not null;
    public bool HasNoSelectedCharacter => SelectedCharacter is null;

    public IReadOnlyList<string> Filters { get; } = new[] { "All Characters", "Players", "Playerbots" };

    public int TotalCharacters => _all.Count;
    public int TotalPlayers => _all.Count(x => !x.Playerbot);
    public int TotalPlayerbots => _all.Count(x => x.Playerbot);
    public string RosterSummary => $"{Characters.Count:N0} shown • {_all.Count:N0} total";

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText == value) return;
            _searchText = value;
            OnPropertyChanged();
            ApplyFilter();
        }
    }

    public int FilterIndex
    {
        get => _filterIndex;
        set
        {
            if (_filterIndex == value) return;
            _filterIndex = value;
            OnPropertyChanged();
            ApplyFilter();
        }
    }

    public ArmoryCharacterSummary? SelectedSummary
    {
        get => _selectedSummary;
        set
        {
            if (ReferenceEquals(_selectedSummary, value)) return;
            _selectedSummary = value;
            OnPropertyChanged();

            if (value is not null)
                _ = LoadProfileAsync(value.Id);
        }
    }

    public ArmoryCharacter? SelectedCharacter
    {
        get => _selectedCharacter;
        private set
        {
            _selectedCharacter = value;
            RebuildEquipmentSlots();
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelectedCharacter));
            OnPropertyChanged(nameof(HasNoSelectedCharacter));
        }
    }

    private void ApplyFilter()
    {
        var query = _all.AsEnumerable();

        if (_filterIndex == 1)
            query = query.Where(x => !x.Playerbot);
        else if (_filterIndex == 2)
            query = query.Where(x => x.Playerbot);

        if (!string.IsNullOrWhiteSpace(_searchText))
            query = query.Where(x => x.Name.Contains(_searchText.Trim(), StringComparison.OrdinalIgnoreCase));

        Characters.Clear();
        foreach (var character in query)
            Characters.Add(character);

        OnPropertyChanged(nameof(RosterSummary));
    }

    private void RebuildEquipmentSlots()
    {
        LeftEquipmentSlots.Clear();
        RightEquipmentSlots.Clear();
        BottomEquipmentSlots.Clear();

        if (_selectedCharacter is null)
            return;

        var bySlot = _selectedCharacter.Equipment.ToDictionary(x => x.Slot);

        // Deliberately mirrors the visual rhythm of the classic character sheet:
        // armor on the sides, jewelry/weapons across the bottom.
        int[] left =  { 0, 1, 2, 14, 4, 3, 18 };
        int[] right = { 8, 9, 5, 6, 7 };
        int[] bottom = { 10, 11, 12, 13, 15, 16, 17 };

        foreach (var slot in left)
            LeftEquipmentSlots.Add(new ArmoryEquipmentSlotView(slot, bySlot.GetValueOrDefault(slot)));
        foreach (var slot in right)
            RightEquipmentSlots.Add(new ArmoryEquipmentSlotView(slot, bySlot.GetValueOrDefault(slot)));
        foreach (var slot in bottom)
            BottomEquipmentSlots.Add(new ArmoryEquipmentSlotView(slot, bySlot.GetValueOrDefault(slot)));
    }

    private async Task LoadEquipmentIconsAsync(ulong characterId)
    {
        var slots = LeftEquipmentSlots
            .Concat(RightEquipmentSlots)
            .Concat(BottomEquipmentSlots)
            .Where(x => x.HasItem)
            .ToArray();

        var tasks = slots.Select(async slot =>
        {
            var bitmap = await _service.LoadItemIconAsync(slot.IconName);

            // Selection may have changed while icons were downloading.
            if (_selectedSummary?.Id == characterId)
                slot.IconImage = bitmap;
            else
                bitmap?.Dispose();

            if (slot.Item?.EnchantmentsValid != false && slot.Item is not null)
            {
                foreach (var gem in slot.Item.Gems)
                {
                    var gemBitmap = await _service.LoadItemIconAsync(gem.Icon);
                    if (_selectedSummary?.Id == characterId)
                        gem.IconImage = gemBitmap;
                    else
                        gemBitmap?.Dispose();
                }
            }
        });

        await Task.WhenAll(tasks);
    }

    private async Task LoadProfileAsync(ulong id)
    {
        LoadingProfile = true;
        try
        {
            var result = await _service.LoadProfileAsync(_indexUrl, id);

            if (_selectedSummary?.Id == id)
            {
                SelectedCharacter = result.Profile?.Character;
                Status = result.Status;

                if (SelectedCharacter is not null)
                    _ = LoadEquipmentIconsAsync(id);
            }
        }
        finally
        {
            LoadingProfile = false;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
