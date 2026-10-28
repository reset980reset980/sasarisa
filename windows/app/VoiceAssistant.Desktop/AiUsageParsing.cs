using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

internal static class AiUsageParsing
{
    static bool Object(JsonElement value, string key, out JsonElement child) { child = default; return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out child) && child.ValueKind == JsonValueKind.Object; }
    static bool Count(JsonElement value, string key, out long count)
    { count = 0; if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out var number) && number is >= 0 and <= 1_000_000_000_000) { count = number; return true; } return false; }
    static string Text(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "";
    static bool OptionalCount(JsonElement value, string key, out long count) { count = 0; return !value.TryGetProperty(key, out _) || Count(value, key, out count); }
    static AiTokenUsage Inclusive(JsonElement usage, string inputKey, string outputKey, string detailsKey, string cacheKey)
    {
        if (!Count(usage, inputKey, out var input) || !Count(usage, outputKey, out var output)) return new();
        long cache = 0;
        if (usage.TryGetProperty(detailsKey, out var details) && (details.ValueKind != JsonValueKind.Object || !OptionalCount(details, cacheKey, out cache))) return new();
        if (cache > input) return new(); return new(input - cache, output, cache, 0, true);
    }
    internal static AiTokenUsage ClaudeTokens(JsonElement root)
    {
        if (!Object(root, "usage", out var usage) || !Count(usage, "input_tokens", out var input) || !Count(usage, "output_tokens", out var output)) return new();
        if (!OptionalCount(usage, "cache_read_input_tokens", out var read) || !OptionalCount(usage, "cache_creation_input_tokens", out var write)) return new(); return new(input, output, read, write, true);
    }
    internal static AiTokenUsage CodexTokens(JsonElement root)
    {
        if (!Object(root, "usage", out var usage) || !Count(usage, "input_tokens", out var input) || !Count(usage, "output_tokens", out var output)) return new();
        if (!OptionalCount(usage, "cached_input_tokens", out var cache)) return new(); return cache > input ? new() : new(input - cache, output, cache, 0, true);
    }
    internal static AiTokenUsage ApiTokens(JsonElement root, string provider)
    {
        if (provider == "anthropic") return ClaudeTokens(root);
        if (Object(root, "usage", out var usage))
        {
            var responses = Inclusive(usage, "input_tokens", "output_tokens", "input_tokens_details", "cached_tokens");
            if (responses.Reported) return responses;
            return Inclusive(usage, "prompt_tokens", "completion_tokens", "prompt_tokens_details", "cached_tokens");
        }
        if (provider == "ollama" && Count(root, "prompt_eval_count", out var prompt) && Count(root, "eval_count", out var output)) return new(prompt, output, 0, 0, true);
        return new();
    }
    static DateTimeOffset? Date(JsonElement value, string key)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(key, out var item)) return null;
        if (item.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(item.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)) return parsed;
        if (item.ValueKind == JsonValueKind.Number && item.TryGetDouble(out var seconds) && double.IsFinite(seconds))
        { try { return DateTimeOffset.UnixEpoch.AddSeconds(seconds); } catch (ArgumentOutOfRangeException) { } }
        return null;
    }
    static bool Fraction(JsonElement value, string key, double scale, out double fraction)
    {
        fraction = 0;
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(key, out var item) || item.ValueKind != JsonValueKind.Number || !item.TryGetDouble(out var number) || !double.IsFinite(number)) return false;
        fraction = number / scale; return fraction is >= 0 and <= 1;
    }
    internal static AiQuotaWindow[] ClaudeQuota(JsonElement root, DateTimeOffset seen)
    {
        if (Text(root, "type") != "rate_limit_event" || !Object(root, "rate_limit_info", out var info)) return Array.Empty<AiQuotaWindow>();
        var output = new List<AiQuotaWindow>(); var names = new Dictionary<string, string> { ["five_hour"] = "5시간", ["seven_day"] = "7일", ["seven_day_opus"] = "7일 · Opus" };
        if (Object(info, "unifiedWindows", out var windows))
            foreach (var pair in names) if (Object(windows, pair.Key, out var window) && Fraction(window, "utilization", 1, out var used)) output.Add(new("claude", pair.Key, pair.Value, used, Date(window, "resetsAt"), seen));
        var kind = Text(info, "rateLimitType");
        if (names.TryGetValue(kind, out var label) && Fraction(info, "utilization", 1, out var utilization)) output.Add(new("claude", kind, label, utilization, Date(info, "resetsAt"), seen));
        return output.GroupBy(x => x.Key).Select(x => x.Last()).ToArray();
    }
    internal static AiQuotaWindow[] CodexQuota(JsonElement root, DateTimeOffset fallbackSeen)
    {
        var type = Text(root, "type"); var payload = root;
        if (type == "event_msg") { if (!Object(root, "payload", out payload) || Text(payload, "type") != "token_count") return Array.Empty<AiQuotaWindow>(); }
        else if (type is not ("token_count" or "turn.completed" or "rate_limits.updated")) return Array.Empty<AiQuotaWindow>();
        if (!Object(payload, "rate_limits", out var limits)) return Array.Empty<AiQuotaWindow>();
        var seen = Date(root, "timestamp") ?? fallbackSeen; var result = new List<AiQuotaWindow>();
        foreach (var key in new[] { "primary", "secondary" })
        {
            if (!Object(limits, key, out var window) || !Fraction(window, "used_percent", 100, out var used)) continue;
            Count(window, "window_minutes", out var minutes);
            var label = minutes == 300 ? "5시간" : minutes == 10080 ? "7일" : minutes > 0 && minutes % 1440 == 0 ? minutes / 1440 + "일" : minutes > 0 && minutes % 60 == 0 ? minutes / 60 + "시간" : minutes > 0 ? minutes + "분" : key == "primary" ? "기본 한도" : "추가 한도";
            result.Add(new("codex", key, label, used, Date(window, "resets_at"), seen));
        }
        return result.ToArray();
    }
}

internal sealed record CodexUsageScan(int Files, int Bytes, int Windows, string? Error);
internal static class CodexUsageReader
{
    internal static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");
    internal static Task<CodexUsageScan> RefreshAsync(AiUsageStore store, string root, DateTimeOffset now, CancellationToken token) => Task.Run(() =>
    {
        int files = 0, bytes = 0, failed = 0; var windows = new Dictionary<string, AiQuotaWindow>(); var candidates = new List<FileInfo>();
        try
        {
            for (var day = 0; day < 3; day++)
            {
                token.ThrowIfCancellationRequested(); var date = now.LocalDateTime.Date.AddDays(-day);
                var directory = Path.Combine(root, date.ToString("yyyy", CultureInfo.InvariantCulture), date.ToString("MM", CultureInfo.InvariantCulture), date.ToString("dd", CultureInfo.InvariantCulture));
                if (Directory.Exists(directory)) candidates.AddRange(new DirectoryInfo(directory).EnumerateFiles("*.jsonl", SearchOption.TopDirectoryOnly));
            }
            foreach (var file in candidates.OrderByDescending(x => x.LastWriteTimeUtc).Take(5))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    using var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    var length = (int)Math.Min(400_000, input.Length); var tail = new byte[length]; input.Seek(-length, SeekOrigin.End); var total = 0;
                    while (total < length) { token.ThrowIfCancellationRequested(); var read = input.Read(tail, total, length - total); if (read == 0) break; total += read; }
                    files++; bytes += total;
                    foreach (var line in Encoding.UTF8.GetString(tail, 0, total).Split('\n').Reverse())
                    {
                        token.ThrowIfCancellationRequested(); if (line.Length > 32_000 || !line.Contains("\"rate_limits\"", StringComparison.Ordinal)) continue;
                        try
                        {
                            using var doc = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 12 });
                            foreach (var window in AiUsageParsing.CodexQuota(doc.RootElement, file.LastWriteTimeUtc))
                                if (window.Seen <= now.AddMinutes(5) && window.Seen >= now.AddDays(-3) && (!windows.TryGetValue(window.Key, out var previous) || window.Seen > previous.Seen)) windows[window.Key] = window;
                        }
                        catch (JsonException) { }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed++; }
            }
            token.ThrowIfCancellationRequested(); if (windows.Count > 0) store.Record(null, windows.Values);
            return new CodexUsageScan(files, bytes, windows.Count, failed == 0 ? null : "일부 Codex 로컬 사용 기록을 읽지 못했어요.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new CodexUsageScan(files, bytes, 0, "Codex 로컬 사용 기록을 읽지 못했어요."); }
    }, token);
}
