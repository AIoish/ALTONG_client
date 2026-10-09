using System.Text.Json.Serialization;
using System.Text.Json;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Altong.Client.Models;

// Additive proposal for team 3; existing notification contracts are unchanged.
public sealed record NotificationSummaryBatch(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("items")] IReadOnlyList<NotificationSummaryItem> Items);

public sealed record NotificationSummaryItem(
    [property: JsonPropertyName("summary_id")] string SummaryId,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("is_schedule_related")] bool IsScheduleRelated,
    [property: JsonPropertyName("schedule")] CalendarSchedule? Schedule)
{
    [JsonPropertyName("app_name")]
    public string? AppName { get; init; }
}

public sealed record CalendarSchedule(
    [property: JsonPropertyName("event_id")] string EventId,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("start"), JsonConverter(typeof(ExplicitCalendarTimeConverter))] DateTimeOffset? Start,
    [property: JsonPropertyName("end"), JsonConverter(typeof(ExplicitCalendarTimeConverter))] DateTimeOffset? End,
    [property: JsonPropertyName("time_zone")] string? TimeZone,
    [property: JsonPropertyName("location")] string? Location,
    [property: JsonPropertyName("details")] string? Details)
{
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(EventId)) return "일정 ID가 없습니다. 요약 제공자에게 확인해 주세요.";
        if (string.IsNullOrWhiteSpace(Title)) return "일정 제목을 입력해 주세요.";
        if (Start is null || End is null) return "시작·종료 날짜와 시각을 입력해 주세요.";
        if (End <= Start) return "종료 시각은 시작 시각보다 늦어야 합니다.";
        if (string.IsNullOrWhiteSpace(TimeZone)) return "일정의 시간대를 선택해 주세요.";
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(TimeZone);
            if (!TimeZoneInfo.TryConvertIanaIdToWindowsId(TimeZone, out _))
                return "시간대는 Asia/Seoul처럼 IANA 이름으로 입력해 주세요.";
            if (zone.GetUtcOffset(Start.Value.UtcDateTime) != Start.Value.Offset ||
                zone.GetUtcOffset(End.Value.UtcDateTime) != End.Value.Offset)
                return "날짜·시각의 UTC 오프셋이 시간대와 맞지 않습니다. 일정 정보를 보완해 주세요.";
        }
        catch (TimeZoneNotFoundException) { return "알 수 없는 시간대입니다."; }
        catch (InvalidTimeZoneException) { return "사용할 수 없는 시간대입니다."; }
        return null;
    }
}

// Missing offsets must remain missing input, rather than silently using the PC's time zone.
public sealed class ExplicitCalendarTimeConverter : JsonConverter<DateTimeOffset?>
{
    public override DateTimeOffset? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        var text = reader.GetString();
        return text is not null && Regex.IsMatch(text,
            "\\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}(?::[0-9]{2}(?:\\.[0-9]{1,7})?)?(?:Z|[+-][0-9]{2}:[0-9]{2})\\z") &&
            DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value) ? value : null;
    }
    public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options)
    {
        if (value is { } time) writer.WriteStringValue(time.ToString("o", CultureInfo.InvariantCulture));
        else writer.WriteNullValue();
    }
}
