using System.Globalization;
using Altong.Client.Data.Models;

namespace Altong.Client.Data.Repositories;

public sealed record DashboardNotificationPage(IReadOnlyList<NotificationRecord> Records,
    bool HasMoreBlocked, bool HasMorePassed)
{
    public bool HasMore => HasMoreBlocked || HasMorePassed;
    public static DashboardNotificationPage Empty { get; } = new([], false, false);
}

/// <summary>대시보드 목록의 조회와 제거 상태만 관리한다. 리포트 원본 알림은 수정하지 않는다.</summary>
public sealed class SqliteDashboardNotificationRepository(IAltongDatabase database)
{
    private const string Columns = """
        n.id, n.app_name, n.sender, n.title, n.body, n.received_at,
        n.is_passed, n.urgency_score, n.relevance_score, n.category,
        n.ai_summary_reason, n.session_id, n.created_at
        """;
    private const string Visible = """
        n.is_passed IN (0, 1) AND NOT EXISTS (
            SELECT 1 FROM dashboard_notification_dismissals d WHERE d.notification_id = n.id
        )
        """;

    public async Task<DashboardNotificationPage> ReadAsync(int limitPerSection = 50,
        IEnumerable<string>? selectedIds = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limitPerSection);
        var selection = (selectedIds ?? []).Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal).ToArray();
        using var connection = database.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {Columns}
            FROM notifications n
            WHERE {Visible} AND n.is_passed = $passed
            ORDER BY julianday(n.received_at) DESC, n.received_at DESC, n.id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", checked(limitPerSection + selection.Length + 1));
        var passedParameter = command.Parameters.Add("$passed", Microsoft.Data.Sqlite.SqliteType.Integer);
        passedParameter.Value = 0;
        var blocked = await ReadRecordsAsync(command).ConfigureAwait(false);
        passedParameter.Value = 1;
        var passed = await ReadRecordsAsync(command).ConfigureAwait(false);
        var records = blocked.Take(limitPerSection).Concat(passed.Take(limitPerSection)).ToList();

        // New arrivals can push a selected item past the visible limit. Keep those
        // selected rows visible so a refresh never silently changes the selection.
        var visibleIds = records.Select(record => record.Id).ToHashSet(StringComparer.Ordinal);
        var retainedIds = selection.Where(id => !visibleIds.Contains(id));
        foreach (var chunk in retainedIds.Chunk(100))
        {
            command.Parameters.Clear();
            var names = chunk.Select((_, i) => "$id" + i).ToArray();
            command.CommandText = $"SELECT {Columns} FROM notifications n WHERE {Visible} AND n.id IN ({string.Join(",", names)});";
            for (int i = 0; i < chunk.Length; i++) command.Parameters.AddWithValue(names[i], chunk[i]);
            var retained = await ReadRecordsAsync(command).ConfigureAwait(false);
            records.AddRange(retained);
            visibleIds.UnionWith(retained.Select(record => record.Id));
        }
        return new DashboardNotificationPage(records.OrderByDescending(record => AsUtc(record.ReceivedAt))
            .ThenByDescending(record => record.Id, StringComparer.Ordinal).ToArray(),
            blocked.Any(record => !visibleIds.Contains(record.Id)), passed.Any(record => !visibleIds.Contains(record.Id)));
    }

    private static async Task<List<NotificationRecord>> ReadRecordsAsync(Microsoft.Data.Sqlite.SqliteCommand command)
    {
        var records = new List<NotificationRecord>();
        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
            records.Add(new NotificationRecord(reader.GetString(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3), reader.GetString(4),
                DateTime.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.GetInt32(6) == 1,
                reader.IsDBNull(7) ? null : reader.GetInt32(7),
                reader.IsDBNull(8) ? null : reader.GetInt32(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(12) ? null : DateTime.Parse(reader.GetString(12), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
        return records;
    }

    // Offset-free legacy timestamps have the same UTC interpretation as SQLite and TimeText.
    private static DateTime AsUtc(DateTime at) => at.Kind == DateTimeKind.Unspecified
        ? DateTime.SpecifyKind(at, DateTimeKind.Utc) : at.ToUniversalTime();

    public async Task DismissAsync(IEnumerable<string> notificationIds)
    {
        ArgumentNullException.ThrowIfNull(notificationIds);
        var ids = notificationIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0) return;
        using var connection = database.CreateOpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO dashboard_notification_dismissals(notification_id, dismissed_at)
            SELECT id, $at FROM notifications WHERE id = $id AND is_passed IN (0, 1);
            """;
        command.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        var idParameter = command.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Text);
        foreach (var id in ids)
        {
            idParameter.Value = id;
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        transaction.Commit();
    }
}
