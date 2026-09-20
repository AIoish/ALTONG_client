using System.Globalization;
using Altong.Client.Data.Models;
using Microsoft.Data.Sqlite;

namespace Altong.Client.Data.Repositories;

/// <summary>
/// Microsoft.Data.Sqlite 기반의 알림 저장소 구현체.
/// </summary>
public sealed class SqliteNotificationRepository : INotificationRepository
{
    private readonly IAltongDatabase _database;

    public SqliteNotificationRepository(IAltongDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task InsertAsync(NotificationRecord notification)
    {
        using var connection = _database.CreateOpenConnection();
        using var command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO notifications (
                id, app_name, sender, title, body, received_at,
                is_passed, urgency_score, relevance_score, category, ai_summary_reason, session_id
            ) VALUES (
                $id, $app_name, $sender, $title, $body, $received_at,
                $is_passed, $urgency_score, $relevance_score, $category, $ai_summary_reason, $session_id
            );
            """;

        command.Parameters.AddWithValue("$id", notification.Id);
        command.Parameters.AddWithValue("$app_name", notification.AppName);
        command.Parameters.AddWithValue("$sender", (object?)notification.Sender ?? DBNull.Value);
        command.Parameters.AddWithValue("$title", notification.Title);
        command.Parameters.AddWithValue("$body", notification.Body);
        command.Parameters.AddWithValue("$received_at", notification.ReceivedAt.ToString("o", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$is_passed", notification.IsPassed.HasValue ? (notification.IsPassed.Value ? 1 : 0) : DBNull.Value);
        command.Parameters.AddWithValue("$urgency_score", (object?)notification.UrgencyScore ?? DBNull.Value);
        command.Parameters.AddWithValue("$relevance_score", (object?)notification.RelevanceScore ?? DBNull.Value);
        command.Parameters.AddWithValue("$category", (object?)notification.Category ?? DBNull.Value);
        command.Parameters.AddWithValue("$ai_summary_reason", (object?)notification.AiSummaryReason ?? DBNull.Value);
        command.Parameters.AddWithValue("$session_id", (object?)notification.SessionId ?? DBNull.Value);

        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public async Task UpdateFilterResultAsync(
        string id, bool isPassed, int urgencyScore, int relevanceScore, string category, string? reason)
    {
        using var connection = _database.CreateOpenConnection();
        using var command = connection.CreateCommand();

        command.CommandText = """
            UPDATE notifications
            SET is_passed = $is_passed,
                urgency_score = $urgency_score,
                relevance_score = $relevance_score,
                category = $category,
                ai_summary_reason = $ai_summary_reason
            WHERE id = $id;
            """;

        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$is_passed", isPassed ? 1 : 0);
        command.Parameters.AddWithValue("$urgency_score", urgencyScore);
        command.Parameters.AddWithValue("$relevance_score", relevanceScore);
        command.Parameters.AddWithValue("$category", category);
        command.Parameters.AddWithValue("$ai_summary_reason", (object?)reason ?? DBNull.Value);

        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public async Task<NotificationRecord?> GetByIdAsync(string id)
    {
        using var connection = _database.CreateOpenConnection();
        using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT id, app_name, sender, title, body, received_at,
                   is_passed, urgency_score, relevance_score, category, ai_summary_reason, session_id, created_at
            FROM notifications
            WHERE id = $id
            LIMIT 1;
            """;

        command.Parameters.AddWithValue("$id", id);

        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        if (await reader.ReadAsync().ConfigureAwait(false))
        {
            return MapNotification(reader);
        }

        return null;
    }

    public async Task<IReadOnlyList<NotificationRecord>> GetBlockedNotificationsAsync(string? sessionId = null, int limit = 50)
    {
        using var connection = _database.CreateOpenConnection();
        using var command = connection.CreateCommand();

        if (string.IsNullOrEmpty(sessionId))
        {
            command.CommandText = """
                SELECT id, app_name, sender, title, body, received_at,
                       is_passed, urgency_score, relevance_score, category, ai_summary_reason, session_id, created_at
                FROM notifications
                WHERE is_passed = 0
                ORDER BY received_at DESC
                LIMIT $limit;
                """;
        }
        else
        {
            command.CommandText = """
                SELECT id, app_name, sender, title, body, received_at,
                       is_passed, urgency_score, relevance_score, category, ai_summary_reason, session_id, created_at
                FROM notifications
                WHERE is_passed = 0 AND session_id = $session_id
                ORDER BY received_at DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$session_id", sessionId);
        }

        command.Parameters.AddWithValue("$limit", limit);

        var list = new List<NotificationRecord>();
        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            list.Add(MapNotification(reader));
        }

        return list;
    }

    public async Task<IReadOnlyList<NotificationRecord>> GetRecentNotificationsAsync(int limit = 50)
    {
        using var connection = _database.CreateOpenConnection();
        using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT id, app_name, sender, title, body, received_at,
                   is_passed, urgency_score, relevance_score, category, ai_summary_reason, session_id, created_at
            FROM notifications
            ORDER BY received_at DESC
            LIMIT $limit;
            """;

        command.Parameters.AddWithValue("$limit", limit);

        var list = new List<NotificationRecord>();
        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            list.Add(MapNotification(reader));
        }

        return list;
    }

    private static NotificationRecord MapNotification(SqliteDataReader reader)
    {
        string id = reader.GetString(0);
        string appName = reader.GetString(1);
        string? sender = reader.IsDBNull(2) ? null : reader.GetString(2);
        string title = reader.GetString(3);
        string body = reader.GetString(4);
        DateTime receivedAt = DateTime.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

        bool? isPassed = reader.IsDBNull(6) ? null : reader.GetInt32(6) == 1;
        int? urgencyScore = reader.IsDBNull(7) ? null : reader.GetInt32(7);
        int? relevanceScore = reader.IsDBNull(8) ? null : reader.GetInt32(8);
        string? category = reader.IsDBNull(9) ? null : reader.GetString(9);
        string? reason = reader.IsDBNull(10) ? null : reader.GetString(10);
        string? sessionId = reader.IsDBNull(11) ? null : reader.GetString(11);
        DateTime? createdAt = reader.IsDBNull(12) ? null : DateTime.Parse(reader.GetString(12), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

        return new NotificationRecord(
            id, appName, sender, title, body, receivedAt,
            isPassed, urgencyScore, relevanceScore, category, reason, sessionId, createdAt);
    }
}
