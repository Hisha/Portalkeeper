using System;
using System.Collections.Generic;
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
    private DateOnly _selectedDate;

    public ObservableCollection<CalendarDayCell> Days { get; } = new();
    public ObservableCollection<CalendarEventDetailRow> SelectedEvents { get; } = new();
    public string LoadStatus { get; }

    public CalendarViewModel(RealmCalendarFeed feed, string loadStatus)
    {
        _feed = feed;
        LoadStatus = loadStatus;

        var today = DateOnly.FromDateTime(DateTime.Now);
        var initial = new DateOnly(today.Year, today.Month, 1);
        var first = FirstPublishedMonth;
        var last = LastPublishedMonth;

        _month = initial < first ? first : initial > last ? last : initial;
        _selectedDate = ClampToMonth(today, _month);
        Refresh();
    }

    public string MonthTitle => _month.ToString("MMMM yyyy", CultureInfo.CurrentCulture).ToUpperInvariant();
    public string SelectedDayTitle => _selectedDate.ToString("dddd, MMMM d, yyyy", CultureInfo.CurrentCulture);
    public string FeedRange => $"Published {_feed.Range.Start:MMM d, yyyy} – {_feed.Range.End:MMM d, yyyy} • generated {_feed.GeneratedAt.ToLocalTime():g}";
    public bool CanPrevious => _month > FirstPublishedMonth;
    public bool CanNext => _month < LastPublishedMonth;
    public bool HasSelectedEvents => SelectedEvents.Count > 0;
    public string SelectedDayEmptyText => "No public realm events on this day.";

    private DateOnly FirstPublishedMonth => new(_feed.Range.Start.Year, _feed.Range.Start.Month, 1);
    private DateOnly LastPublishedMonth => new(_feed.Range.End.Year, _feed.Range.End.Month, 1);

    public void PreviousMonth()
    {
        if (!CanPrevious)
            return;

        _month = _month.AddMonths(-1);
        _selectedDate = FirstSelectableDateInMonth(_month);
        Refresh();
    }

    public void NextMonth()
    {
        if (!CanNext)
            return;

        _month = _month.AddMonths(1);
        _selectedDate = FirstSelectableDateInMonth(_month);
        Refresh();
    }

    public void SelectDay(DateOnly date)
    {
        if (date < _feed.Range.Start || date > _feed.Range.End)
            return;

        if (date.Year != _month.Year || date.Month != _month.Month)
        {
            var requestedMonth = new DateOnly(date.Year, date.Month, 1);
            if (requestedMonth < FirstPublishedMonth || requestedMonth > LastPublishedMonth)
                return;

            _month = requestedMonth;
        }

        _selectedDate = date;
        Refresh();
    }

    private void Refresh()
    {
        BuildDays();
        BuildSelectedEvents();

        OnPropertyChanged(nameof(MonthTitle));
        OnPropertyChanged(nameof(SelectedDayTitle));
        OnPropertyChanged(nameof(CanPrevious));
        OnPropertyChanged(nameof(CanNext));
        OnPropertyChanged(nameof(HasSelectedEvents));
    }

    private void BuildDays()
    {
        Days.Clear();

        var firstOfMonth = _month;
        var sundayOffset = (int)firstOfMonth.DayOfWeek;
        var gridStart = firstOfMonth.AddDays(-sundayOffset);
        var today = DateOnly.FromDateTime(DateTime.Now);

        for (var i = 0; i < 42; i++)
        {
            var date = gridStart.AddDays(i);
            var isCurrentMonth = date.Month == _month.Month && date.Year == _month.Year;
            var isPublished = date >= _feed.Range.Start && date <= _feed.Range.End;
            var eventMarkers = EventsForDay(date)
                .Take(3)
                .Select(ToMarker)
                .ToList();
            var totalEvents = EventsForDay(date).Count;

            Days.Add(new CalendarDayCell(
                date,
                date.Day.ToString(CultureInfo.CurrentCulture),
                isCurrentMonth,
                isPublished,
                date == today,
                date == _selectedDate,
                eventMarkers,
                Math.Max(0, totalEvents - eventMarkers.Count)));
        }
    }

    private void BuildSelectedEvents()
    {
        SelectedEvents.Clear();

        foreach (var calendarEvent in EventsForDay(_selectedDate)
                     .OrderBy(EventSortDate)
                     .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            SelectedEvents.Add(ToDetailRow(calendarEvent));
        }
    }

    private List<RealmCalendarEvent> EventsForDay(DateOnly day) => _feed.Events
        .Where(e => OccursOn(e, day))
        .ToList();

    private static bool OccursOn(RealmCalendarEvent e, DateOnly day)
    {
        if (e.AllDay && e.StartDate is { } startDate)
        {
            var endDate = e.EndDate ?? startDate;
            return day >= startDate && day <= endDate;
        }

        if (e.Start is { } start)
        {
            var localDate = DateOnly.FromDateTime(start.ToLocalTime().DateTime);
            return localDate == day;
        }

        return false;
    }

    private static DateTime EventSortDate(RealmCalendarEvent e) => e.AllDay && e.StartDate is { } day
        ? day.ToDateTime(TimeOnly.MinValue)
        : e.Start?.ToLocalTime().DateTime ?? DateTime.MaxValue;

    private static CalendarEventMarker ToMarker(RealmCalendarEvent e)
    {
        var displayName = e.Name switch
        {
            "Kalu'ak Fishing Derby" => "Kalu'ak Derby",
            "Stranglethorn Fishing Extravaganza" => "Stranglethorn",
            _ => e.Name
        };

        var (background, foreground) = EventColors(e);
        return new CalendarEventMarker(displayName, background, foreground);
    }

    private static CalendarEventDetailRow ToDetailRow(RealmCalendarEvent e)
    {
        if (e.AllDay && e.StartDate is { } start)
        {
            var end = e.EndDate ?? start;
            var duration = start == end
                ? "All day"
                : $"All day • {start:MMM d} – {end:MMM d}";
            var (background, foreground) = EventColors(e);
            return new CalendarEventDetailRow(e.Name, duration, CategoryLabel(e.Category), background, foreground);
        }

        if (e.Start is { } timedStart)
        {
            var localStart = timedStart.ToLocalTime();
            var localEnd = e.End?.ToLocalTime();
            var time = localEnd is null
                ? localStart.ToString("t")
                : $"{localStart:t} – {localEnd.Value:t}";
            var (background, foreground) = EventColors(e);
            return new CalendarEventDetailRow(e.Name, time, CategoryLabel(e.Category), background, foreground);
        }

        var colors = EventColors(e);
        return new CalendarEventDetailRow(e.Name, string.Empty, CategoryLabel(e.Category), colors.Background, colors.Foreground);
    }

    private static string CategoryLabel(string category) => category.Equals("fishing", StringComparison.OrdinalIgnoreCase)
        ? "Fishing contest"
        : "Realm holiday";

    private static (string Background, string Foreground) EventColors(RealmCalendarEvent e)
    {
        var name = e.Name;

        if (name.Contains("Darkmoon", StringComparison.OrdinalIgnoreCase))
            return ("#5C3B76", "#FFF7FF");
        if (e.Category.Equals("fishing", StringComparison.OrdinalIgnoreCase))
            return ("#315D69", "#F2FFFF");
        if (name.Contains("Brewfest", StringComparison.OrdinalIgnoreCase))
            return ("#8B5A20", "#FFF8E8");
        if (name.Contains("Harvest", StringComparison.OrdinalIgnoreCase))
            return ("#786126", "#FFF9E7");
        if (name.Contains("Hallow", StringComparison.OrdinalIgnoreCase))
            return ("#704226", "#FFF4E8");
        if (name.Contains("Winter", StringComparison.OrdinalIgnoreCase))
            return ("#3E6170", "#F2FCFF");
        if (name.Contains("Love", StringComparison.OrdinalIgnoreCase))
            return ("#7A405E", "#FFF4FA");
        if (name.Contains("Lunar", StringComparison.OrdinalIgnoreCase))
            return ("#4E5781", "#F6F5FF");

        return ("#65533D", "#FFF9ED");
    }

    private DateOnly FirstSelectableDateInMonth(DateOnly month)
    {
        var monthEnd = month.AddMonths(1).AddDays(-1);
        if (_feed.Range.Start > month)
            return _feed.Range.Start;
        if (_feed.Range.End < monthEnd)
            return _feed.Range.End;
        return month;
    }

    private static DateOnly ClampToMonth(DateOnly date, DateOnly month)
    {
        if (date.Year == month.Year && date.Month == month.Month)
            return date;
        return month;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class CalendarDayCell
{
    public CalendarDayCell(
        DateOnly date,
        string dayNumber,
        bool isCurrentMonth,
        bool isPublished,
        bool isToday,
        bool isSelected,
        IReadOnlyList<CalendarEventMarker> markers,
        int overflowCount)
    {
        Date = date;
        DayNumber = dayNumber;
        IsCurrentMonth = isCurrentMonth;
        IsPublished = isPublished;
        IsToday = isToday;
        IsSelected = isSelected;
        Markers = markers;
        OverflowCount = overflowCount;
    }

    public DateOnly Date { get; }
    public string DayNumber { get; }
    public bool IsCurrentMonth { get; }
    public bool IsPublished { get; }
    public bool IsToday { get; }
    public bool IsSelected { get; }
    public IReadOnlyList<CalendarEventMarker> Markers { get; }
    public int OverflowCount { get; }
    public bool HasOverflow => OverflowCount > 0;

    public string Background => IsSelected
        ? "#FFF0C8"
        : IsCurrentMonth ? "#E8DDBF" : "#C9BC9E";

    public string BorderBrush => IsSelected
        ? "#9B6B24"
        : IsToday ? "#7963A0" : "#9C8D70";

    public string DayForeground => IsCurrentMonth ? "#2A2118" : "#6D6250";
    public string TodayBadge => IsToday ? "TODAY" : string.Empty;
    public double CellOpacity => IsPublished ? 1.0 : 0.45;
}

public sealed record CalendarEventMarker(string Name, string Background, string Foreground);

public sealed record CalendarEventDetailRow(
    string Name,
    string TimeText,
    string CategoryText,
    string AccentBackground,
    string AccentForeground);
