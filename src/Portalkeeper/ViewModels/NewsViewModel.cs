using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using Portalkeeper.Models;

namespace Portalkeeper.ViewModels;

public sealed class NewsViewModel : INotifyPropertyChanged
{
    private RealmNewsArticle? _selectedArticle;

    public ObservableCollection<RealmNewsArticle> Articles { get; }
    public string LoadStatus { get; }
    public string FeedGeneratedDisplay { get; }
    public string ArticleCountText => Articles.Count == 1 ? "1 published article" : $"{Articles.Count} published articles";
    public bool HasArticles => Articles.Count > 0;

    public NewsViewModel(RealmNewsFeed feed, string loadStatus)
    {
        LoadStatus = loadStatus;
        Articles = new ObservableCollection<RealmNewsArticle>(
            feed.Articles
                .OrderByDescending(article => article.Pinned)
                .ThenByDescending(article => article.PublishedAt)
                .ThenByDescending(article => article.Id));

        FeedGeneratedDisplay = $"Feed generated {feed.GeneratedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}";
        _selectedArticle = Articles.FirstOrDefault();
    }

    public RealmNewsArticle? SelectedArticle
    {
        get => _selectedArticle;
        set
        {
            if (ReferenceEquals(_selectedArticle, value))
                return;

            _selectedArticle = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelection));
        }
    }

    public bool HasSelection => SelectedArticle is not null;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
