using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Altong.Client.Services;
using TextBox = System.Windows.Controls.TextBox;

namespace Altong.Client;

public partial class QuickMemoView : System.Windows.Controls.UserControl
{
    private readonly DispatcherTimer _draftTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private QuickMemoStore _store = new();
    private bool _updating;
    private bool _composing;
    private bool _saving;
    private Task? _draftWrite;
    private int _draftRevision;
    private int _visibleCount = 20;
    private string? _draftSessionId;
    private readonly Dictionary<string, string> _pendingDrafts = new(StringComparer.Ordinal);
    public event EventHandler? CloseRequested;
    internal TextBox Editor => MemoEditor;

    public QuickMemoView()
    {
        InitializeComponent();
        _draftTimer.Tick += DraftTimer_Tick;
        TextCompositionManager.AddPreviewTextInputStartHandler(MemoEditor, (_, _) => _composing = true);
        TextCompositionManager.AddPreviewTextInputUpdateHandler(MemoEditor, (_, _) => _composing = true);
        TextCompositionManager.AddPreviewTextInputHandler(MemoEditor, (_, _) => _composing = false);
        MemoEditor.LostKeyboardFocus += (_, _) => _composing = false;
        Unloaded += (_, _) => TryFlushDraft();
    }

    internal void SetStore(QuickMemoStore store)
    {
        _store = store;
        LoadSession();
    }

    internal void SetSession(string? sessionId)
    {
        if (_draftSessionId == sessionId) return;
        TryFlushDraft();
        _draftSessionId = sessionId;
        _composing = false;
        _visibleCount = 20;
        LoadSession();
        MemoScroll.ScrollToTop();
    }

    private void LoadSession()
    {
        _draftRevision++;
        _updating = true;
        MemoEditor.Text = _draftSessionId is not null && _pendingDrafts.TryGetValue(_draftSessionId, out var pending)
            ? pending : _store.GetDraftForSession(_draftSessionId);
        _updating = false;
        MemoStatusText.Text = "Ctrl+Enter로 저장";
        RefreshEntries();
        RefreshAvailability();
    }

    private void RefreshAvailability()
    {
        bool available = _store.LoadError is null;
        MemoEditor.IsEnabled = available && _draftSessionId is not null;
        ReloadMemoButton.Visibility = available ? Visibility.Collapsed : Visibility.Visible;
        SaveMemoButton.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        SaveMemoButton.IsEnabled = available && _draftSessionId is not null && !_saving && !string.IsNullOrWhiteSpace(MemoEditor.Text);
        if (!available) MemoStatusText.Text = _store.LoadError;
    }

    private void MemoEditor_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_updating || SaveMemoButton is null) return;
        if (_draftSessionId is null) return;
        _draftRevision++;
        _pendingDrafts[_draftSessionId] = MemoEditor.Text;
        RefreshAvailability();
        MemoStatusText.Text = "메모 저장 중…";
        _draftTimer.Stop();
        _draftTimer.Start();
    }

    private async void DraftTimer_Tick(object? sender, EventArgs e)
    {
        _draftTimer.Stop();
        if (_store.LoadError is not null || _draftSessionId is null) return;
        if (_draftWrite is { IsCompleted: false }) { _draftTimer.Start(); return; }
        int revision = _draftRevision;
        string text = MemoEditor.Text;
        string? sessionId = _draftSessionId;
        try
        {
            _draftWrite = Task.Run(() => _store.SaveDraft(text, sessionId));
            await _draftWrite;
            if (_pendingDrafts.TryGetValue(sessionId!, out var pending) && pending == text) _pendingDrafts.Remove(sessionId!);
            if (revision == _draftRevision)
                MemoStatusText.Text = string.IsNullOrWhiteSpace(text) ? "Ctrl+Enter로 저장" : "메모 보관됨 · Ctrl+Enter로 추가";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (revision == _draftRevision)
                MemoStatusText.Text = "메모를 저장하지 못했어요. 내용을 복사하거나 다시 저장해 주세요.";
        }
    }

    private void FinishPendingDraft()
    {
        // A pending older snapshot must finish before saving/clearing the current draft.
        _draftRevision++;
        try { _draftWrite?.GetAwaiter().GetResult(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Retry the current contents below. */ }
    }

    public bool TryFlushDraft()
    {
        _draftTimer.Stop();
        if (_store.LoadError is not null) return true; // Editing is disabled; preserve the unreadable file.
        try
        {
            FinishPendingDraft();
            foreach (var draft in _pendingDrafts.ToArray())
            {
                _store.SaveDraft(draft.Value, draft.Key);
                _pendingDrafts.Remove(draft.Key);
            }
            if (!string.IsNullOrWhiteSpace(MemoEditor.Text)) MemoStatusText.Text = "메모 보관됨 · Ctrl+Enter로 추가";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MemoStatusText.Text = "메모를 저장하지 못했어요. 내용을 복사하거나 다시 저장해 주세요.";
            return false;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e) => SaveMemo();

    internal void SaveMemo()
    {
        if (_saving || _composing || _draftSessionId is null || string.IsNullOrWhiteSpace(MemoEditor.Text)) return;
        _draftTimer.Stop();
        _saving = true;
        RefreshAvailability();
        try
        {
            FinishPendingDraft();
            if (_store.Save(MemoEditor.Text, _draftSessionId) is null) return;
            _pendingDrafts.Remove(_draftSessionId);
            _updating = true;
            MemoEditor.Clear();
            _updating = false;
            _visibleCount = 20;
            RefreshEntries();
            MemoScroll.ScrollToTop();
            MemoStatusText.Text = "메모를 저장했어요";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MemoStatusText.Text = "저장하지 못했어요. 내용을 유지했으니 다시 저장해 주세요.";
        }
        finally
        {
            _saving = false;
            RefreshAvailability();
        }
    }

    private void Memo_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // IME owns Enter/Escape during composition; do not save or close a partially composed syllable.
        if (_composing || e.Key == Key.ImeProcessed) return;
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SaveMemo();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        _visibleCount += 20;
        RefreshEntries();
    }

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        _store.Reload();
        if (_store.LoadError is null) SetStore(_store);
        else RefreshAvailability();
    }

    private void RefreshEntries()
    {
        var entries = _store.GetSavedForSession(_draftSessionId);
        MemoItems.ItemsSource = entries.Take(_visibleCount).ToArray();
        SavedMemoHeading.Text = $"이번 집중 메모 · {entries.Count}개";
        MemoEmptyText.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        MemoScroll.Visibility = entries.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        MoreMemosButton.Visibility = entries.Count > _visibleCount ? Visibility.Visible : Visibility.Collapsed;
    }
}
