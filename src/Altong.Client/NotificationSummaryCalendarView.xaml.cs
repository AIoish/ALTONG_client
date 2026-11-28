using System.Windows;
using Altong.Client.Models;
using Altong.Client.Services.AI.Briefing;

namespace Altong.Client;

public partial class NotificationSummaryCalendarView : System.Windows.Controls.UserControl
{
    public CalendarSummaryItemViewModel[] Items { get; private set; } = [];
    private ReportSummaryGroupViewModel[] _groups = [];
    private int _displayLimit = 25;
    internal ScheduleRegistrationWindow? ActiveDialog { get; private set; }
    internal bool UseModelessDialogForTests { get; set; }
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
        _groups = items.GroupBy(row => (row.SessionId, App: BriefingGrouping.AppIdentity(row.Summary.AppName),
                Group: string.IsNullOrWhiteSpace(row.Summary.GroupId) ? "summary:" + row.Summary.SummaryId : row.Summary.GroupId))
            .Select(group => new ReportSummaryGroupViewModel(group)).ToArray();
        UpdateVisibleItems();
        CalendarLinkHint.Visibility = Items.Any(row => row.IsScheduleRelated) ? Visibility.Visible : Visibility.Collapsed;
    }

    public void CloseActiveDialog()
    {
        var dialog = ActiveDialog;
        if (dialog is null) return;
        ActiveDialog = null;
        dialog.Close();
    }

    private void UpdateVisibleItems()
    {
        SummaryItemsControl.ItemsSource = _groups.Take(_displayLimit).ToArray();
        MoreSummariesButton.Visibility = _groups.Length > _displayLimit ? Visibility.Visible : Visibility.Collapsed;
        MoreSummariesButton.Content = $"요약 더 보기 · {Math.Min(_displayLimit, _groups.Length)}/{_groups.Length}그룹";
    }

    private void MoreSummaries_Click(object sender, RoutedEventArgs e)
    {
        _displayLimit += 25;
        UpdateVisibleItems();
    }

    private void OpenSchedulePopup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CalendarSummaryItemViewModel row } ||
            !row.CanAct || !row.IsScheduleRelated) return;

        if (ActiveDialog is not null)
        {
            ActiveDialog.Activate();
            return;
        }

        if (!row.IsSaved && !string.IsNullOrWhiteSpace(row.Status))
            row.Status = "";

        row.IsEditing = true;
        var dialog = new ScheduleRegistrationWindow(row, ConfirmScheduleRegistration);
        if (Window.GetWindow(this) is { IsVisible: true } owner)
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        ActiveDialog = dialog;
        dialog.Closed += (_, _) =>
        {
            if (ReferenceEquals(ActiveDialog, dialog))
                ActiveDialog = null;
        };

        if (UseModelessDialogForTests)
            dialog.Show();
        else
            dialog.ShowDialog();
    }

    private bool ConfirmScheduleRegistration(CalendarSummaryItemViewModel row)
    {
        if (!row.CanAct || !row.IsScheduleRelated)
            return false;

        if (!row.ApplyEdits())
        {
            row.IsEditing = true;
            return false;
        }

        ScheduleCorrected?.Invoke(this, row);
        ScheduleSaveRequested?.Invoke(this, row);

        if (ScheduleSaveRequested is not null && !row.IsSaved)
        {
            row.IsEditing = true;
            return false;
        }

        return true;
    }
}
