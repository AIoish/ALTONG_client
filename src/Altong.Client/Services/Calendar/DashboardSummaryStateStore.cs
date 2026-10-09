using System.IO;
using System.Text.Json;
using Altong.Client.Models;

namespace Altong.Client.Services.Calendar;

// Dashboard decisions only. AI input and historical report snapshots remain untouched.
public sealed record DashboardSummaryState(string SessionId, string SummaryId,
    CalendarSchedule? Correction = null, bool Dismissed = false);

public sealed class DashboardSummaryStateStore(string path)
{
    private Dictionary<(string Session, string Summary), DashboardSummaryState> _states = [];
    public string? LoadWarning { get; private set; }

    public void Load()
    {
        try
        {
            if (!File.Exists(path)) return;
            var loaded = JsonSerializer.Deserialize<DashboardSummaryState[]>(File.ReadAllText(path))
                ?? throw new JsonException();
            var updated = new Dictionary<(string, string), DashboardSummaryState>();
            foreach (var state in loaded)
            {
                if (state is null || string.IsNullOrWhiteSpace(state.SessionId) || string.IsNullOrWhiteSpace(state.SummaryId))
                    throw new JsonException();
                updated[(state.SessionId, state.SummaryId)] = state;
            }
            _states = updated;
            LoadWarning = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LoadWarning = "이전에 보완하거나 제거한 요약의 상태를 불러오지 못했습니다.";
        }
    }

    public DashboardSummaryState? Get(string sessionId, string summaryId) =>
        _states.GetValueOrDefault((sessionId, summaryId));

    public void Save(DashboardSummaryState state)
    {
        // Do not overwrite an unreadable state file or report a removal before it is saved.
        if (LoadWarning is not null) throw new IOException(LoadWarning);
        var updated = new Dictionary<(string, string), DashboardSummaryState>(_states)
        {
            [(state.SessionId, state.SummaryId)] = state
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(updated.Values.ToArray()));
        File.Move(temporary, path, overwrite: true);
        _states = updated;
    }
}
