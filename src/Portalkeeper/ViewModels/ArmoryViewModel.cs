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
        ApplyFilter();
    }

    public ObservableCollection<ArmoryCharacterSummary> Characters { get; }
    public string Status { get => _status; private set { _status = value; OnPropertyChanged(); } }
    public bool LoadingProfile { get => _loadingProfile; private set { _loadingProfile = value; OnPropertyChanged(); } }
    public bool HasSelectedCharacter => SelectedCharacter is not null;
    public IReadOnlyList<string> Filters { get; } = new[] { "All Characters", "Players", "Playerbots" };

    public string SearchText
    {
        get => _searchText;
        set { if (_searchText == value) return; _searchText = value; OnPropertyChanged(); ApplyFilter(); }
    }

    public int FilterIndex
    {
        get => _filterIndex;
        set { if (_filterIndex == value) return; _filterIndex = value; OnPropertyChanged(); ApplyFilter(); }
    }

    public ArmoryCharacterSummary? SelectedSummary
    {
        get => _selectedSummary;
        set
        {
            if (ReferenceEquals(_selectedSummary, value)) return;
            _selectedSummary = value;
            OnPropertyChanged();
            if (value is not null) _ = LoadProfileAsync(value.Id);
        }
    }

    public ArmoryCharacter? SelectedCharacter
    {
        get => _selectedCharacter;
        private set { _selectedCharacter = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasSelectedCharacter)); }
    }

    private void ApplyFilter()
    {
        var query = _all.AsEnumerable();
        if (_filterIndex == 1) query = query.Where(x => !x.Playerbot);
        else if (_filterIndex == 2) query = query.Where(x => x.Playerbot);
        if (!string.IsNullOrWhiteSpace(_searchText))
            query = query.Where(x => x.Name.Contains(_searchText.Trim(), StringComparison.OrdinalIgnoreCase));

        Characters.Clear();
        foreach (var character in query) Characters.Add(character);
    }

    private async Task LoadProfileAsync(ulong id)
    {
        LoadingProfile = true;
        try
        {
            var result = await _service.LoadProfileAsync(_indexUrl, id);
            SelectedCharacter = result.Profile?.Character;
            Status = result.Status;
        }
        finally { LoadingProfile = false; }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
