using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Altong.Client.Data.Models;

namespace Altong.Client.Services.AI.Briefing;

internal sealed class GemmaBriefingClient(int port) : IDisposable
{
    private readonly HttpClient _http = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
    {
        BaseAddress = new Uri($"http://127.0.0.1:{port}"),
        Timeout = TimeSpan.FromMinutes(2)
    };

    public async Task<BriefingRawOutput?> SummarizeAsync(string appName, string sender,
        IReadOnlyList<NotificationRecord> notifications, CancellationToken ct)
    {
        var items = notifications.Select(n => new NotificationPromptItem(
            n.ReceivedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"), n.Title ?? "", n.Body ?? "")).ToArray();
        var schema = new
        {
            type = "object",
            properties = new
            {
                primary_category = new { type = "string", @enum = BriefingPromptBuilder.AllowedCategories },
                summary_lines = new
                {
                    type = "array", minItems = 1, maxItems = BriefingPromptBuilder.MaxSummaryLines,
                    items = new { type = "string", minLength = 1, maxLength = BriefingPromptBuilder.MaxLineLength }
                }
            },
            required = new[] { "primary_category", "summary_lines" },
            additionalProperties = false
        };
        try
        {
            using var response = await _http.PostAsJsonAsync("/v1/chat/completions", new
            {
                messages = new[]
                {
                    new { role = "system", content = BriefingPromptBuilder.BuildSystemPrompt() },
                    new { role = "user", content = BriefingPromptBuilder.BuildUserPrompt(appName, sender, items) }
                },
                temperature = 0,
                max_tokens = LlamaBriefingEngine.MaxNewTokens,
                response_format = new { type = "json_schema", json_schema = new { name = "briefing", schema } }
            }, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var document = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: ct).ConfigureAwait(false);
            var raw = document?.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
            var parsed = LlamaBriefingEngine.ParseResponse(raw);
            if (parsed is null) AppLogger.Info("[Briefing] Gemma response did not match the briefing contract.");
            return parsed;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested &&
            ex is HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException
                or IndexOutOfRangeException or OperationCanceledException)
        {
            AppLogger.Error("[Briefing] Gemma request failed", ex);
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
