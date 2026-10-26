using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

public sealed record YouTubeVideo(string Id, string Title)
{
    public string WatchUrl => "https://www.youtube.com/watch?v=" + Id + "&autoplay=1";
}

public sealed class YouTubeSearch
{
    IReadOnlyList<YouTubeVideo> results = Array.Empty<YouTubeVideo>();
    DateTimeOffset foundAt;
    public IReadOnlyList<YouTubeVideo> Results => results;
    public static string SearchUrl(string query) => "https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(query);

    public async Task<IReadOnlyList<YouTubeVideo>> SearchAsync(string query, CancellationToken token)
    {
        results = Array.Empty<YouTubeVideo>();
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 16_000_000 };
        using var request = new HttpRequestMessage(HttpMethod.Get, SearchUrl(query));
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36");
        request.Headers.AcceptLanguage.ParseAdd("ko-KR,ko;q=0.9,en;q=0.5");
        using var response = await client.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        var parsed = ParseResults(await response.Content.ReadAsStringAsync(token));
        token.ThrowIfCancellationRequested();
        results = parsed; foundAt = DateTimeOffset.Now;
        return results;
    }

    public YouTubeVideo Select(int number)
    {
        if (results.Count == 0 || DateTimeOffset.Now - foundAt > TimeSpan.FromHours(1)) throw new InvalidOperationException("먼저 유튜브에서 영상을 검색해 주세요.");
        if (number < 1 || number > results.Count) throw new InvalidOperationException($"검색 결과는 {results.Count}개예요. 1~{results.Count}번째 중 골라 주세요.");
        return results[number - 1];
    }

    public static IReadOnlyList<YouTubeVideo> ParseResults(string html)
    {
        var videos = new List<YouTubeVideo>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        // Parse the JSON tree, rather than a regex over escaped video titles.
        var assignment = Regex.Match(html, @"(?:var\s+ytInitialData\s*=|window\[\s*[""']ytInitialData[""']\s*\]\s*=)\s*");
        if (!assignment.Success) return videos;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(html[(assignment.Index + assignment.Length)..]);
            var reader = new Utf8JsonReader(bytes); using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            if (root.TryGetProperty("contents", out var content) && content.TryGetProperty("twoColumnSearchResultsRenderer", out var columns) && columns.TryGetProperty("primaryContents", out var primary)) root = primary;
            Walk(root);
        }
        catch (JsonException) { }
        return videos;

        void Walk(JsonElement element)
        {
            if (videos.Count >= 10) return;
            if (element.ValueKind == JsonValueKind.Array) { foreach (var child in element.EnumerateArray()) Walk(child); return; }
            if (element.ValueKind != JsonValueKind.Object) return;
            if (element.TryGetProperty("videoRenderer", out var video))
            {
                var id = video.TryGetProperty("videoId", out var value) ? value.GetString() : null;
                if (id == null || !Regex.IsMatch(id, @"^[A-Za-z0-9_-]{11}$") || !seen.Add(id)) return;
                var title = "";
                if (video.TryGetProperty("title", out var titleNode))
                {
                    if (titleNode.TryGetProperty("runs", out var runs)) title = string.Concat(runs.EnumerateArray().Select(x => x.TryGetProperty("text", out var t) ? t.GetString() : ""));
                    else if (titleNode.TryGetProperty("simpleText", out var simple)) title = simple.GetString() ?? "";
                }
                if (!string.IsNullOrWhiteSpace(title)) videos.Add(new(id, title));
                return;
            }
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Contains("adSlot", StringComparison.OrdinalIgnoreCase) || property.Name.Contains("promoted", StringComparison.OrdinalIgnoreCase) || property.Name.Contains("advert", StringComparison.OrdinalIgnoreCase)) continue;
                Walk(property.Value);
            }
        }
    }
}
