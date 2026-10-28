using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

internal static class ClaudeUsageEvents
{
    internal static async Task<JsonElement?> ReadAsync(StreamReader input, AiUsageAttempt usage)
    {
        JsonElement? result = null;
        while (await input.ReadLineAsync() is { } line)
        {
            if (line.Length is < 2 or > 1_000_000) continue;
            try
            {
                using var doc = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 64 }); var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                usage.ObserveClaude(root);
                var type = root.TryGetProperty("type", out var kind) && kind.ValueKind == JsonValueKind.String ? kind.GetString() : "";
                if (type == "result" || string.IsNullOrEmpty(type) && (root.TryGetProperty("structured_output", out _) || root.TryGetProperty("result", out _))) result = root.Clone();
            }
            catch (JsonException) { }
        }
        return result;
    }
    internal static void Codex(string stream, AiUsageAttempt usage)
    {
        foreach (var line in stream.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 1_000_000) continue;
            try { using var doc = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 64 }); usage.ObserveCodex(doc.RootElement); }
            catch (JsonException) { }
        }
    }
}
