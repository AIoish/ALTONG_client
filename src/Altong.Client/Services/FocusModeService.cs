namespace Altong.Client.Services;

/// <summary>
/// 앱에서 공유하는 집중 모드의 활성 상태를 관리한다.
/// </summary>
public sealed class FocusModeService
{
    public bool IsEnabled { get; private set; }

    public event EventHandler? StateChanged;

    public void Start()
    {
        SetEnabled(true);
    }

    public void Stop()
    {
        SetEnabled(false);
    }

    public void Toggle()
    {
        SetEnabled(!IsEnabled);
    }

    private void SetEnabled(bool isEnabled)
    {
        if (IsEnabled == isEnabled)
        {
            return;
        }

        IsEnabled = isEnabled;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
