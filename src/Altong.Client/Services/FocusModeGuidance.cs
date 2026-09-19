namespace Altong.Client.Services;

public enum FocusModeGuidanceKind
{
    EnableWindowsDnd,
    DisableWindowsDnd,
    Unavailable,
}

public sealed record FocusModeGuidance(
    FocusModeGuidanceKind Kind,
    string Message,
    bool CanOpenSettings);
