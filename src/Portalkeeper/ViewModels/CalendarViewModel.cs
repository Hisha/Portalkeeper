using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using Portalkeeper.Models;

namespace Portalkeeper.ViewModels;

public sealed class CalendarViewModel : INotifyPropertyChanged
{
    private readonly RealmCalendarFeed _feed;
    private DateOnly _month;
    public ObservableCollection<CalendarEventRow> Events { get; } = new();
    public string LoadStatus { get; }

    public CalendarViewModel(RealmCalendarFeed feed, string loadStatus)
    {
        _feed = feed;
        LoadStatus = loadStatus;
        var today = DateOnly.FromDateTime(DateTime.Now);
        var initial = new DateOnly(today.Year, today.Month, 1);
        var first = new DateOnly(feed.Range.Start.Year, feed.Range.Start.Month, 1);
        var last = new DateOnly(feed.Range.End.Year, feed.Range.End.Month, 1);
        _month = initial < first ? first : initial > last ? last : initial;
        Refresh();
    }

    public string MonthTitle => _month.ToString("MMMM yyyy", CultureInfo.CurrentCulture).ToUpperInvariant();
    public string FeedRange => $"Published { _feed.Range.Start:MMM d, yyyy} – {_feed.Range.End:MMM d, yyyy} • generated {_feed.GeneratedAt.ToLocalTime():g}";
    public bool CanPrevious => _month > new DateOnly(_feed.Range.Start.Year, _feed.Range.Start.Month, 1);
    public bool CanNext => _month < new DateOnly(_feed.Range.End.Year, _feed.Range.End.Month, 1);

    public void PreviousMonth() { if (!CanPrevious) return; _month = _month.AddMonths(-1); Refresh(); }
    public void NextMonth() { if (!CanNext) return; _month = _month.AddMonths(1); Refresh(); }

    private void Refresh()
    {
        Events.Clear();
        var monthStart = _month;
        var monthEnd = _month.AddMonths(1).AddDays(-1);

        foreach (var e in _feed.Events.Where(e => Intersects(e, monthStart, monthEnd)).OrderBy(EventSortDate).ThenBy(e => e.Name))
            Events.Add(ToRow(e));

        OnPropertyChanged(nameof(MonthTitle));
        OnPropertyChanged(nameof(CanPrevious));
        OnPropertyChanged(nameof(CanNext));
        OnPropertyChanged(nameof(HasEvents));
    }

    public bool HasEvents => Events.Count > 0;

    private static bool Intersects(RealmCalendarEvent e, DateOnly first, DateOnly last)
    {
        if (e.AllDay && e.StartDate is { } sd)
            return sd <= last && (e.EndDate ?? sd) >= first;
        if (e.Start is { } st)
        {
            var local = st.ToLocalTime();
            var d = DateOnly.FromDateTime(local.DateTime);
            return d >= first && d <= last;
        }
        return false;
    }

    private static DateTime EventSortDate(RealmCalendarEvent e) => e.AllDay && e.StartDate is { } d
        ? d.ToDateTime(TimeOnly.MinValue)
        : e.Start?.ToLocalTime().DateTime ?? DateTime.MaxValue;

    private static CalendarEventRow ToRow(RealmCalendarEvent e)
    {
        if (e.AllDay && e.StartDate is { } start)
        {
            var end = e.EndDate ?? start;
            var when = start == end ? start.ToString("ddd, MMM d") : $"{start:MMM d} – {end:MMM d}";
            return new(when, e.Name, "All day", e.Category);
        }

        if (e.Start is { } timedStart)
        {
            var s = timedStart.ToLocalTime();
            var end = e.End?.ToLocalTime();
            var time = end is null ? s.ToString("t") : $"{s:t} – {end.Value:t}";
            return new(s.ToString("ddd, MMM d"), e.Name, time, e.Category);
        }

        return new("", e.Name, "", e.Category);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record CalendarEventRow(string DateText, string Name, string TimeText, string Category);
