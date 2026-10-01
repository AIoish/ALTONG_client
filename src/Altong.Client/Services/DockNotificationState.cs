using System.Collections.ObjectModel;
using Altong.Client.Data.Models;
using Altong.Client.Models;

namespace Altong.Client.Services;

/// <summary>
/// UI 스레드에서 사용하는 세션별 알림 상태. 표시·읽음 처리는 DB와 독립적이다.
/// 지연된 판정은 수집 당시 SessionId가 현재 세션과 일치할 때만 받는다.
/// </summary>
public sealed class DockNotificationState
{
    private readonly ObservableCollection<DockNotificationItem> _items = [];
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
    private bool _restorePinnedAfterDrag;

    public DockNotificationState() => Items = new(_items);
    public ReadOnlyObservableCollection<DockNotificationItem> Items { get; }
    public string? SessionId { get; private set; }
    public bool IsActive => SessionId is not null;
    public bool IsPanelOpen { get; private set; }
    public bool IsPinned { get; private set; }
    public bool IsDragging { get; private set; }
    public DockNotificationItem? SelectedItem { get; private set; }
    public int UnreadCount => _items.Count(item => !item.IsRead);
    public int HighestUrgency => _items.Where(item => !item.IsRead).Select(item => item.Urgency).DefaultIfEmpty(0).Max();
    public string BadgeText => UnreadCount > 9 ? "9+" : UnreadCount.ToString();
    public event EventHandler? Changed;

    public void BeginSession(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (SessionId == sessionId) return;
        Reset();
        SessionId = sessionId;
        Notify();
    }

    public void EndSession()
    {
        Reset();
        Notify();
    }

    private void Reset()
    {
        SessionId = null;
        IsPanelOpen = IsPinned = IsDragging = _restorePinnedAfterDrag = false;
        SelectedItem = null;
        _items.Clear();
        _ids.Clear();
    }

    public bool Receive(NotificationRecord record)
    {
        if (!IsActive || record.SessionId != SessionId || record.IsPassed != true ||
            string.IsNullOrWhiteSpace(record.Id) || !_ids.Add(record.Id))
            return false;

        var item = new DockNotificationItem(record);
        int index = 0;
        while (index < _items.Count && Compare(_items[index], item) <= 0) index++;
        _items.Insert(index, item);
        Notify();
        return true;
    }

    public void OpenPanel()
    {
        if (!IsActive || IsDragging || IsPanelOpen) return;
        IsPanelOpen = true;
        Notify();
    }

    public void ClosePanel()
    {
        FinalizeSelectedRead();
        IsPanelOpen = IsPinned = _restorePinnedAfterDrag = false;
        Notify();
    }

    public void OpenDetail(string id)
    {
        if (!IsPanelOpen || IsDragging) return;
        var next = _items.FirstOrDefault(item => item.Id == id);
        if (next is null || next == SelectedItem) return;
        FinalizeSelectedRead();
        SelectedItem = next;
        next.MarkRead();
        Notify();
    }

    public void CloseDetail()
    {
        FinalizeSelectedRead();
        Notify();
    }

    private void FinalizeSelectedRead()
    {
        if (SelectedItem is not { } selected) return;
        selected.SortAsUnread = false;
        SelectedItem = null;
        var ordered = _items.OrderBy(item => item, Comparer<DockNotificationItem>.Create(Compare)).ToArray();
        for (int i = 0; i < ordered.Length; i++)
        {
            int oldIndex = _items.IndexOf(ordered[i]);
            if (oldIndex != i) _items.Move(oldIndex, i);
        }
    }

    private static int Compare(DockNotificationItem left, DockNotificationItem right)
    {
        int group = right.SortAsUnread.CompareTo(left.SortAsUnread);
        if (group != 0) return group;
        int received = right.Notification.ReceivedAt.CompareTo(left.Notification.ReceivedAt);
        return received != 0 ? received : StringComparer.Ordinal.Compare(left.Id, right.Id);
    }

    public void TogglePin()
    {
        if (!IsPanelOpen || IsDragging) return;
        IsPinned = !IsPinned;
        Notify();
    }

    public void BeginDrag()
    {
        if (IsDragging) return;
        _restorePinnedAfterDrag = IsPanelOpen && IsPinned;
        IsDragging = true;
        IsPanelOpen = false;
        if (!_restorePinnedAfterDrag) FinalizeSelectedRead();
        Notify();
    }

    public void EndDrag()
    {
        if (!IsDragging) return;
        IsDragging = false;
        IsPanelOpen = IsActive && IsPinned && _restorePinnedAfterDrag;
        _restorePinnedAfterDrag = false;
        Notify();
    }

    private void Notify() => Changed?.Invoke(this, EventArgs.Empty);
}
