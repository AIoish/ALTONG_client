using System.IO;
using System.Text.Json;

namespace Altong.Client.Services.NotificationControl;

public interface INativeFocusController : IDisposable
{
    bool IsActive { get; }
    string Status { get; }
    FocusControlBackend Backend { get; }
    IReadOnlyList<NotificationSender> Senders { get; }
    bool IsSelected(string id);
    void SetSelected(string id, bool selected);
    void SetBackend(FocusControlBackend backend);
    void Refresh();
    void Start();
    void Stop();
    void CheckHealth();
}

public sealed class NativeFocusController : INativeFocusController
{
    private readonly INotificationSenderSettings _settings;
    private readonly NotificationSuppressionTransaction _transaction;
    private readonly NotificationRecoveryGuardian _guardian = new();
    private readonly NotificationControlPreferences _preferences;
    private readonly string _preferencesPath;
    private string? _startupError;
    private readonly string? _preferencesError;
    private readonly string? _preferencesNotice;
    public bool IsActive { get; private set; }
    public FocusControlBackend Backend => _preferences.Backend;
    public IReadOnlyList<NotificationSender> Senders { get; private set; } = [];
    public string Status { get; private set; } = "알림 제어 준비 중";

    public NativeFocusController() : this(new WindowsNotificationSenderSettings(),
        NotificationControlPreferences.FilePath, NotificationSuppressionTransaction.DefaultJournalPath)
    {
    }

    public NativeFocusController(INotificationSenderSettings settings, string preferencesPath, string journalPath)
    {
        _settings = settings;
        _preferencesPath = Path.GetFullPath(preferencesPath);
        _transaction = new(_settings, journalPath);
        try { _preferences = NotificationControlPreferences.Load(_preferencesPath); }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            // Lost exclusions must not silently cause every app to be suppressed.
            _preferences = new() { Backend = FocusControlBackend.Windows };
            try
            {
                string backupPath = _preferencesPath + ".corrupt-" + Guid.NewGuid().ToString("N") + ".json";
                File.Copy(_preferencesPath, backupPath, overwrite: false);
                _preferences.Save(_preferencesPath);
                _preferencesNotice = "손상된 앱 선택 설정을 별도 보관하고 Windows 방식으로 초기화했습니다. 자체 제어를 사용하려면 앱을 다시 선택해 주세요.";
            }
            catch (Exception recoveryError)
            {
                _preferencesError = $"앱 선택 설정을 복구하지 못해 이 실행에서는 Windows 방식을 사용합니다. 원본 파일은 유지합니다. ({recoveryError.Message})";
            }
        }
        catch (Exception ex)
        {
            _preferences = new() { Backend = FocusControlBackend.Windows };
            _preferencesError = $"앱 선택 설정을 읽지 못해 이 실행에서는 Windows 방식을 사용합니다. ({ex.Message})";
        }
        try { _transaction.Restore(abandonedOnly: true); }
        catch (Exception ex) { _startupError = $"이전 알림 설정 복구 대기: {ex.Message}"; }
        Refresh();
    }

    public bool IsSelected(string id) => !_preferences.ExcludedSenders.Contains(id);
    public void SetSelected(string id, bool selected)
    {
        if (_preferencesError is { } error) throw new InvalidOperationException(error);
        if (IsActive || _transaction.HasPendingRecovery) throw new InvalidOperationException("집중을 종료하고 복구한 뒤 선택을 변경해 주세요.");
        if (!Senders.Any(s => s.Id == id && s.IsEligible)) return;
        var previous = new HashSet<string>(_preferences.ExcludedSenders, StringComparer.OrdinalIgnoreCase);
        if (selected) _preferences.ExcludedSenders.Remove(id); else _preferences.ExcludedSenders.Add(id);
        try { _preferences.Save(_preferencesPath); }
        catch { _preferences.ExcludedSenders = previous; throw; }
    }
    public void SetBackend(FocusControlBackend backend)
    {
        if (_preferences.Backend == backend) return;
        if (_preferencesError is { } error) throw new InvalidOperationException(error);
        if (IsActive || _transaction.HasPendingRecovery) Stop();
        var previous = _preferences.Backend;
        _preferences.Backend = backend;
        try { _preferences.Save(_preferencesPath); } catch { _preferences.Backend = previous; throw; }
        Refresh();
    }
    public void Refresh()
    {
        string summary;
        try
        {
            if (_settings.CompatibilityError is { } error)
            {
                Senders = [];
                summary = error;
            }
            else
            {
                Senders = _settings.Enumerate();
                summary = IsActive ? $"알통 자체 제어 중 · {AppliedCount}개 앱 · 알림센터 보관 유지"
                    : $"제어 가능한 앱 {Senders.Count(s => s.IsEligible)}개 · 알림센터 보관 유지";
            }
        }
        catch (Exception ex) { Senders = []; summary = $"앱 조회 실패: {ex.Message}"; }
        Status = string.Join(Environment.NewLine,
            new[] { _preferencesError, _startupError, _preferencesNotice, summary }.Where(s => !string.IsNullOrEmpty(s)));
    }
    public int AppliedCount { get; private set; }
    public void Start()
    {
        if (IsActive) return;
        if (_preferencesError is { } preferencesError) throw new InvalidOperationException(preferencesError);
        if (_startupError is { } error) throw new InvalidOperationException(error);
        Refresh();
        AppliedCount = _transaction.Begin(Senders.Where(s => s.IsEligible && IsSelected(s.Id)).Select(s => s.Id), _guardian.Start);
        IsActive = true;
        Refresh();
    }
    public void Stop()
    {
        _transaction.Restore();
        IsActive = false;
        AppliedCount = 0;
        _startupError = null;
        Refresh();
    }
    public void CheckHealth()
    {
        if (IsActive && !_guardian.IsAlive)
        {
            Stop();
            throw new InvalidOperationException("복구 감시가 중단되어 집중을 종료하고 원래 알림 설정으로 복구했습니다.");
        }
    }
    public void Dispose()
    {
        try { Stop(); }
        catch (Exception ex) { AppLogger.Warn($"[NotificationControl] 종료 복구 대기: {ex.GetType().Name}"); }
        _guardian.Dispose();
    }
}
