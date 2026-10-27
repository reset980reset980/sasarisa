using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

// The service accepts only a spoken phrase and a canonical quick command, never an AI reply or action JSON.
internal static class SharedCommands
{
    public static string Key(string text) => Regex.Replace(text.Normalize(NormalizationForm.FormC).Trim(), @"\s+", "").ToLowerInvariant();
    public static bool Clean(string? text)
    {
        try { return text != null && text.Length <= 200 && StringInfo.ParseCombiningCharacters(Key(text)).Length is >= 2 and <= 24 && !Regex.IsMatch(text, @"\d{3,}|[@:/\\\p{Cc}\p{Cf}]|http", RegexOptions.IgnoreCase); }
        catch (ArgumentException) { return false; }
    }
    // Conservative vocabulary: an unrecognised word remains local even when sharing is enabled.
    static readonly Regex Vocabulary = new(@"^(?:소리|볼륨|음량|음소거|음악|영상|미디어|재생|일시정지|정지|다음|곡|페이지|뒤로|앞으로|새로고침|메모장|계산기|크롬|엣지|탐색기|카카오톡|카톡|클로드|브이에스코드|좀|조금|더|많이|다시|지금|그냥|해제|열어|열기|실행해|켜|꺼|키워|올려|줄여|낮춰|내려|멈춰|넘겨|가|봐|해|줘|주세요|주라|줄래|줄까요|줄수있어|주겠어|부탁해|을|를|이|로|으로|퍼센트|%|[0-9])+$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    public static bool CanContribute(string said, UiSettings settings) => Clean(said) && Vocabulary.IsMatch(Key(said)) &&
        !new[] { settings.Name, settings.Desktop.UserName }.Where(x => !string.IsNullOrWhiteSpace(x)).Any(x => Key(said).Contains(Key(x)));
    public static string? Canonical(CommandPlan plan)
    {
        if (plan.Actions.Count != 1) return null;
        var a = plan.Actions[0];
        if (a.Target.Length != 0 || a.X != 0 || a.Y != 0) return null;
        if (a.Kind == "volume" && a.Value.Length == 0 && a.Number is >= 0 and <= 100) return $"볼륨 {a.Number}";
        if (a.Kind == "volume_delta" && a.Value.Length == 0 && a.Number is 10 or -10) return a.Number > 0 ? "소리 키워" : "소리 줄여";
        if (a.Number != 0) return null;
        if (a.Kind == "open_app") return CommandParser.Apps.FirstOrDefault(x => x.Value == a.Value).Key is { } name ? name + " 열어" : null;
        if (a.Kind == "media") return a.Value switch { "mute" => "음소거", "pause" => "일시정지", "play" => "음악 재생", "next" => "다음 곡", _ => null };
        if (a.Kind == "browser") return a.Value switch { "back" => "뒤로 가", "forward" => "앞으로 가", "refresh" => "새로고침", _ => null };
        if (a.Kind == "key") return a.Value == "enter" ? "엔터" : null;
        if (a.Value.Length != 0) return null;
        return a.Kind switch { "display_off" => "화면 꺼", "cancel_timers" => "타이머 취소", "close_owned" => "네가 연 창 닫아", "undo" => "되돌려", "history" => "방금 뭐 했어", "stop" => "그만", "youtube_close" => "유튜브 꺼", _ => null };
    }
    public static CommandPlan? Parse(string? canonical)
    {
        if (canonical == null || canonical.Length > 40 || canonical.Any(char.IsControl)) return null;
        var plan = CommandParser.Parse(canonical);
        if (plan == null) return null;
        var key = Key(canonical);
        if (key is "몇시야" or "지금몇시야" or "시간알려줘" or "현재시간" or "오늘며칠이야" or "오늘날짜" or "날짜알려줘") return plan;
        if (Canonical(plan) == null) return null;
        try { foreach (var action in plan.Actions) ActionPolicy.Validate(action); return plan; }
        catch (InvalidOperationException) { return null; }
    }
}

internal sealed record SharedPair(string S, string C);
internal sealed record GrowState
{
    public string Install { get; init; } = Guid.NewGuid().ToString();
    public DateTimeOffset LastPack { get; set; }
    public DateTimeOffset PackAttempt { get; set; }
    public DateTimeOffset LastUpload { get; set; }
    public DateTimeOffset UploadAttempt { get; set; }
    public Dictionary<string, string> Pack { get; set; } = new();
    public Dictionary<string, string> Vetoes { get; set; } = new();
    public Dictionary<string, string> Pending { get; set; } = new();
    public List<string> Sent { get; set; } = new();
}

internal sealed class GrowTogether
{
    public const string Server = "https://maker.buffm.com/api/butler";
    const int MaxBytes = 2 * 1024 * 1024;
    readonly object gate = new();
    readonly SemaphoreSlim sync = new(1, 1);
    readonly string file;
    readonly HttpClient http;
    readonly Func<DateTimeOffset> now;
    GrowState state = new();
    public string? Error { get; }
    public GrowTogether(string file, HttpClient http, Func<DateTimeOffset>? now = null)
    {
        this.file = file; this.http = http; this.now = now ?? (() => DateTimeOffset.UtcNow);
        if (!File.Exists(file)) return;
        try
        {
            using var stream = File.OpenRead(file);
            if (stream.Length > MaxBytes) throw new JsonException();
            var loaded = JsonSerializer.Deserialize<GrowState>(stream, SettingsStore.Json) ?? throw new JsonException();
            if (!Guid.TryParse(loaded.Install, out _) || loaded.Pack == null || loaded.Vetoes == null || loaded.Pending == null || loaded.Sent == null ||
                loaded.Pack.Count > 5000 || loaded.Vetoes.Count > 5000 || loaded.Pending.Count > 100 || loaded.Sent.Count > 2000 || loaded.Sent.Any(x => x == null || x.Length > 100)) throw new JsonException();
            foreach (var pair in loaded.Pack.Concat(loaded.Vetoes).Concat(loaded.Pending))
                if (!SharedCommands.Clean(pair.Key) || SharedCommands.Key(pair.Key) != pair.Key || SharedCommands.Parse(pair.Value) == null) throw new JsonException();
            state = loaded;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException)
        { Error = "공유 학습 기록을 읽지 못했어요. grow-state.json과 .previous 파일을 확인하세요. 기존 기록은 덮어쓰지 않습니다."; }
    }
    public GrowState Snapshot() { lock (gate) return Copy(state); }
    static GrowState Copy(GrowState value) => JsonSerializer.Deserialize<GrowState>(JsonSerializer.Serialize(value))!;
    void Update(Action<GrowState> change)
    {
        lock (gate)
        {
            if (Error != null) throw new IOException(Error);
            var next = Copy(state); change(next);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(next, SettingsStore.Json);
            if (bytes.Length > MaxBytes) throw new IOException("공유 학습 기록의 크기 제한을 초과했어요.");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
            var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { output.Write(bytes); output.Flush(true); }
                if (File.Exists(file)) File.Replace(temp, file, file + ".previous"); else File.Move(temp, file);
                state = next;
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
    public bool Record(string said, CommandPlan plan, UiSettings settings)
    {
        if (settings.GrowTogether != "together" || !SharedCommands.CanContribute(said, settings) || SharedCommands.Canonical(plan) is not { } canonical) return false;
        var key = SharedCommands.Key(said);
        lock (gate)
        {
            if (state.Sent.Contains(key + '\u001f' + canonical) || state.Vetoes.GetValueOrDefault(key) == canonical || state.Pending.Count >= 100 && !state.Pending.ContainsKey(key)) return false;
            Update(next => next.Pending[key] = canonical); return true;
        }
    }
    public (CommandPlan Plan, SharedPair Pair)? Lookup(string said)
    {
        if (Error != null || !SharedCommands.Clean(said)) return null;
        lock (gate)
        {
            var key = SharedCommands.Key(said);
            if (!state.Pack.TryGetValue(key, out var canonical) || state.Vetoes.GetValueOrDefault(key) == canonical || SharedCommands.Parse(canonical) is not { } plan) return null;
            return (plan, new(key, canonical));
        }
    }
    public void Veto(SharedPair pair) => Update(next =>
    {
        if (!SharedCommands.Clean(pair.S) || SharedCommands.Parse(pair.C) == null) throw new ArgumentException("공유 명령을 다시 확인하세요.");
        if (next.Vetoes.Count >= 5000 && !next.Vetoes.ContainsKey(pair.S)) throw new IOException("공유 제외 목록이 가득 찼어요. 공유 학습을 꺼 주세요.");
        next.Vetoes[pair.S] = pair.C; next.Pending.Remove(pair.S);
    });
    static bool Due(DateTimeOffset success, DateTimeOffset attempt, DateTimeOffset current) => current - success >= TimeSpan.FromHours(20) && current - attempt >= TimeSpan.FromHours(1);
    public async Task<string> SyncAsync(UiSettings settings, CancellationToken token)
    {
        if (settings.GrowTogether is not ("solo" or "together")) return "공유 학습 꺼짐 · 이 PC에서만 배워요.";
        await sync.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            if (Error != null) throw new IOException(Error);
            string? notice = null;
            if (settings.GrowTogether == "together" && Due(Snapshot().LastUpload, Snapshot().UploadAttempt, now()))
            {
                try { await UploadAsync(settings, token); }
                catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !token.IsCancellationRequested) { notice = "공유 전송을 확인하지 못했어요. 다음에 다시 확인합니다."; }
            }
            var snapshot = Snapshot();
            if (Due(snapshot.LastPack, snapshot.PackAttempt, now()))
            {
                try { await DownloadAsync(token); }
                catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !token.IsCancellationRequested) { notice = "새 공유 명령을 받지 못했어요. 이전 목록을 유지합니다."; }
            }
            token.ThrowIfCancellationRequested();
            snapshot = Snapshot();
            return notice ?? $"{(settings.GrowTogether == "together" ? "다 같이 키우기" : "공유 명령만 받기")} · 명령 {snapshot.Pack.Count}개 · 제외 {snapshot.Vetoes.Count}개";
        }
        finally { sync.Release(); }
    }
    async Task UploadAsync(UiSettings settings, CancellationToken token)
    {
        var snapshot = Snapshot();
        var items = snapshot.Pending.Where(x => SharedCommands.CanContribute(x.Key, settings) && SharedCommands.Parse(x.Value) != null && snapshot.Vetoes.GetValueOrDefault(x.Key) != x.Value && !snapshot.Sent.Contains(x.Key + '\u001f' + x.Value))
            .Take(50).Select(x => new { s = x.Key, c = x.Value }).ToArray();
        // A veto learned from the public pack is also filtered before leaving this PC.
        var vetoes = snapshot.Vetoes.Where(x => SharedCommands.CanContribute(x.Key, settings)).Take(50).Select(x => new { s = x.Key, c = x.Value }).ToArray();
        token.ThrowIfCancellationRequested();
        Update(next => next.UploadAttempt = now());
        if (items.Length == 0 && vetoes.Length == 0) return;
        using var request = new HttpRequestMessage(HttpMethod.Post, Server + "/learn")
        { Content = new StringContent(JsonSerializer.Serialize(new { v = 1, lang = "ko", install = snapshot.Install, items, veto = vetoes, app = typeof(App).Assembly.GetName().Version?.ToString() ?? "" }), Encoding.UTF8, "application/json") };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        token.ThrowIfCancellationRequested();
        if (response.StatusCode != System.Net.HttpStatusCode.OK) throw new HttpRequestException("Learning acknowledgement unavailable");
        Update(next =>
        {
            next.LastUpload = now();
            foreach (var item in items)
            {
                next.Sent.Add(item.s + '\u001f' + item.c);
                if (next.Pending.GetValueOrDefault(item.s) == item.c) next.Pending.Remove(item.s);
            }
            next.Sent = next.Sent.Distinct().TakeLast(2000).ToList();
        });
    }
    async Task DownloadAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); Update(next => next.PackAttempt = now());
        using var request = new HttpRequestMessage(HttpMethod.Get, Server + "/pack?lang=ko");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode != System.Net.HttpStatusCode.OK) throw new HttpRequestException("Learning pack unavailable");
        using var input = await response.Content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream(); var chunk = new byte[8192]; int read;
        while ((read = await input.ReadAsync(chunk, token)) > 0) { if (buffer.Length + read > 512 * 1024) throw new JsonException("Pack too large"); buffer.Write(chunk, 0, read); }
        using var document = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
        if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > 5000) throw new JsonException("Invalid pack");
        var pack = new Dictionary<string, string>(); var ambiguous = new HashSet<string>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("s", out var s) || s.ValueKind != JsonValueKind.String || !item.TryGetProperty("c", out var c) || c.ValueKind != JsonValueKind.String || !SharedCommands.Clean(s.GetString()) || SharedCommands.Parse(c.GetString()) == null) continue;
            var key = SharedCommands.Key(s.GetString()!); var canonical = c.GetString()!;
            if (pack.TryGetValue(key, out var previous) && previous != canonical) ambiguous.Add(key);
            pack[key] = canonical;
        }
        foreach (var key in ambiguous) pack.Remove(key);
        token.ThrowIfCancellationRequested(); Update(next => { next.Pack = pack; next.LastPack = now(); });
    }
}
