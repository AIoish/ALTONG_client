// Purpose: Preserve the Python JSON input contract without WPF dependencies.
// Created: 2026-10-10
using System.Globalization;
using System.Text;

namespace Altong.Filtering;

public sealed record NotificationInput(string AppName, string? Sender, string Title, string Body, string Timestamp);
public sealed record RecentWindow(string AppName, string WindowTitle);
public sealed record ContextInput(string ActiveProcess, string WindowTitle,
    IReadOnlyList<string>? RecentProcesses = null, IReadOnlyList<RecentWindow>? RecentWindows = null);

public static class ModelInputBuilder
{
    // Python ensure_ascii=False, separators=(',', ':'). Do not use default .NET
    // JSON escaping: escaping Korean changes the text presented to the tokenizer.
    public static string Quote(string value)
    {
        var b = new StringBuilder("\"");
        foreach (char c in value)
            b.Append(c switch { '"' => "\\\"", '\\' => "\\\\", '\b' => "\\b", '\f' => "\\f",
                '\n' => "\\n", '\r' => "\\r", '\t' => "\\t",
                < ' ' => "\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture), _ => c.ToString() });
        return b.Append('"').ToString();
    }

    public static string Build(NotificationInput n, ContextInput c, bool relevance)
    {
        var s = "{\"notification\":{\"app_name\":" + Quote(n.AppName) + ",\"sender\":" + Quote(n.Sender ?? "") +
            ",\"title\":" + Quote(n.Title) + ",\"body\":" + Quote(n.Body) + ",\"timestamp\":" + Quote(n.Timestamp) + "}";
        if (relevance)
            s += ",\"current_window\":{\"active_process\":" + Quote(c.ActiveProcess) + ",\"window_title\":" + Quote(c.WindowTitle) +
                "},\"recent_windows\":[" + string.Join(",", (c.RecentWindows ?? []).Select(w =>
                    "{\"app_name\":" + Quote(w.AppName) + ",\"window_title\":" + Quote(w.WindowTitle) + "}")) +
                "],\"recent_processes\":[" + string.Join(",", (c.RecentProcesses ?? []).Select(Quote)) + "]";
        return s + "}";
    }
}
