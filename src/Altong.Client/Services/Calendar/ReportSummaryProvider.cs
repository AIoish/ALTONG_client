using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Altong.Client.Models;

namespace Altong.Client.Services.Calendar;

public sealed record ReportSummarySupply(IReadOnlyList<NotificationSummaryItem> Items, bool IsDemo = false);

public interface IReportSummaryProvider
{
    Task<ReportSummarySupply> ReadAsync(string sessionId, CancellationToken cancellationToken);
}

public sealed class JsonReportSummaryProvider(string folder) : IReportSummaryProvider
{
    public async Task<ReportSummarySupply> ReadAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (!Regex.IsMatch(sessionId, "\\A[a-zA-Z0-9_-]{1,128}\\z"))
            throw new InvalidDataException("잘못된 집중 세션 ID입니다.");
        var file = Path.Combine(folder, sessionId + ".json");
        if (!File.Exists(file)) return new([]);
        if (new FileInfo(file).Length > 1_048_576) throw new InvalidDataException("요약 파일이 너무 큽니다.");
        var batch = JsonSerializer.Deserialize<NotificationSummaryBatch>(
            await File.ReadAllTextAsync(file, cancellationToken));
        if (batch is null || batch.SchemaVersion != 1 || batch.SessionId != sessionId || batch.Items is null)
            throw new InvalidDataException("요약 버전 또는 집중 세션이 맞지 않습니다.");
        if (batch.Items.Any(item => item is null || string.IsNullOrWhiteSpace(item.SummaryId) ||
            string.IsNullOrWhiteSpace(item.Text)) ||
            batch.Items.Select(item => item.SummaryId).Distinct(StringComparer.Ordinal).Count() != batch.Items.Count)
            throw new InvalidDataException("요약 ID와 내용은 비어 있거나 중복될 수 없습니다.");
        return new(batch.Items);
    }
}

public sealed class FakeReportSummaryProvider : IReportSummaryProvider
{
    public Task<ReportSummarySupply> ReadAsync(string sessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = new DateTimeOffset(2026, 10, 5, 14, 0, 0, TimeSpan.FromHours(9));
        return Task.FromResult(new ReportSummarySupply([
            new("demo-meeting", "내일 오후 2시에 회의실 A에서 프로젝트 진행 상황을 공유해 주세요.", true,
                new("demo-project-meeting", "프로젝트 회의", start, start.AddHours(1), "Asia/Seoul", "회의실 A", "진행 상황 공유와 다음 작업 논의")) { AppName = "KakaoTalk" },
            new("demo-message", "공유한 자료를 확인해 달라는 메시지가 왔습니다.", false, null) { AppName = "KakaoTalk" },
            new("demo-uncertain", "다음 주에 식사 약속을 잡자는 메시지가 왔습니다. 날짜와 시간은 미정입니다.", true,
                new("demo-lunch", "식사 약속", null, null, null, null, "날짜와 시각을 정한 뒤 등록해 주세요.")) { AppName = "KakaoTalk" }
        ], IsDemo: true));
    }
}
