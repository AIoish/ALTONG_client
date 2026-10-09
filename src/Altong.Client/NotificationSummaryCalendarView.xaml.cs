using System.Windows;
using Altong.Client.Models;

namespace Altong.Client;

public partial class NotificationSummaryCalendarView : System.Windows.Controls.UserControl
{
    public CalendarSummaryItemViewModel[] Items { get; private set; } = [];
    public event EventHandler<CalendarSummaryItemViewModel>? ScheduleCorrected;
    public event EventHandler<CalendarSummaryItemViewModel>? SummaryDismissed;
    public event EventHandler<CalendarSummaryItemViewModel>? ScheduleSaveRequested;

    public NotificationSummaryCalendarView(IReadOnlyList<NotificationSummaryItem> summaries,
        bool isDemo = false)
    {
        InitializeComponent();
        SetItems(summaries.Select(summary => new CalendarSummaryItemViewModel(summary)).ToArray());
        SummarySourceText.Text = isDemo ? "예시 알림 요약 · 일정 내용을 확인해 주세요."
            : "일정 관련 알림을 대시보드 캘린더에 저장할 수 있어요.";
        SummarySourceText.Visibility = isDemo ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SetItems(CalendarSummaryItemViewModel[] items)
    {
        if (Items.SequenceEqual(items)) return;
        Items = items;
        SummaryItemsControl.ItemsSource = Items;
        CalendarLinkHint.Visibility = Items.Any(row => row.IsScheduleRelated) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void DismissSummary_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CalendarSummaryItemViewModel row } && !row.IsBusy)
            SummaryDismissed?.Invoke(this, row);
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
