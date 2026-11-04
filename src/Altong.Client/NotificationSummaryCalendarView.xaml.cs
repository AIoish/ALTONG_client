using System.Windows;
using Altong.Client.Models;

namespace Altong.Client;

public partial class NotificationSummaryCalendarView : System.Windows.Controls.UserControl
{
    public CalendarSummaryItemViewModel[] Items { get; private set; } = [];
    private int _displayLimit = 25;
    public event EventHandler<CalendarSummaryItemViewModel>? ScheduleCorrected;
    public event EventHandler<CalendarSummaryItemViewModel>? ScheduleSaveRequested;

    public NotificationSummaryCalendarView(IReadOnlyList<NotificationSummaryItem> summaries,
        bool isDemo = false)
    {
        InitializeComponent();
        SetItems(summaries.Select(summary => new CalendarSummaryItemViewModel(summary)).ToArray());
        SummarySourceText.Text = "예시 요약";
        SummarySourceText.Visibility = isDemo ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SetItems(CalendarSummaryItemViewModel[] items)
    {
        if (Items.SequenceEqual(items)) return;
        Items = items;
        UpdateVisibleItems();
        CalendarLinkHint.Visibility = Items.Any(row => row.IsScheduleRelated) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateVisibleItems()
    {
        SummaryItemsControl.ItemsSource = Items.Take(_displayLimit).ToArray();
        MoreSummariesButton.Visibility = Items.Length > _displayLimit ? Visibility.Visible : Visibility.Collapsed;
        MoreSummariesButton.Content = $"요약 더 보기 · {Math.Min(_displayLimit, Items.Length)}/{Items.Length}";
    }

    private void MoreSummaries_Click(object sender, RoutedEventArgs e)
    {
        _displayLimit += 25;
        UpdateVisibleItems();
    }

    private void SaveCalendar_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CalendarSummaryItemViewModel row } ||
            !row.CanAct || !row.IsScheduleRelated) return;
        if (row.IsEditing)
        {
            if (!row.ApplyEdits()) return;
            ScheduleCorrected?.Invoke(this, row);
        }
        if (row.Draft.Validate() is { } error) { row.Status = error; row.IsEditing = true; return; }
        ScheduleSaveRequested?.Invoke(this, row);
    }

    private void EditSchedule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CalendarSummaryItemViewModel row } &&
            row.IsScheduleRelated && !row.IsBusy) row.IsEditing = !row.IsEditing;
    }

    private void ApplySchedule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CalendarSummaryItemViewModel row } &&
            row.IsScheduleRelated && !row.IsBusy && row.ApplyEdits())
        {
            ScheduleCorrected?.Invoke(this, row);
        }
    }
}
