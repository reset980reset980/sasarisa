using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace VoiceAssistant.Desktop;

public sealed record AiTokenUsage(long Input = 0, long Output = 0, long CacheRead = 0, long CacheWrite = 0, bool Reported = false)
{
    [JsonIgnore] public long Total => checked(Input + Output + CacheRead + CacheWrite);
    [JsonIgnore] public long Equivalent => checked(Input + Output + CacheWrite + CacheRead / 10);
}
public sealed record AiQuotaWindow(string Provider, string Key, string Label, double Used, DateTimeOffset? Resets, DateTimeOffset Seen);
public sealed record AiUsageSample(string Id, string Provider, DateTimeOffset Finished, long Milliseconds, string Outcome, AiTokenUsage Tokens);
public sealed record AiDailyUsage
{
    public long Calls { get; set; }
    public long ReportedCalls { get; set; }
    public long Responses { get; set; }
    public long Failed { get; set; }
    public long Canceled { get; set; }
    public long Milliseconds { get; set; }
    public AiTokenUsage Tokens { get; set; } = new();
}
public sealed record AiUsageState
{
    public int Version { get; set; } = 1;
    public DateTimeOffset? Started { get; set; }
    public Dictionary<string, Dictionary<string, AiDailyUsage>> Days { get; set; } = new();
    public Dictionary<string, AiQuotaWindow> Windows { get; set; } = new();
    public List<string> Recent { get; set; } = new();
}
public sealed record AiUsageSnapshot(AiUsageState State, string? Error, int Pending, int Dropped);

public sealed class AiUsageStore
{
    internal const int MaxBytes = 4 * 1024 * 1024;
    static readonly JsonSerializerOptions Json = new(SettingsStore.Json) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 12, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    static readonly Lazy<AiUsageStore> shared = new(() => new(Path.Combine(AppPaths.Home, "ai-usage.json")));
    internal static AiUsageStore Shared => shared.Value;
    readonly string path;
    readonly object gate = new();
    readonly List<(AiUsageSample? Sample, AiQuotaWindow[] Windows)> pending = new();
    AiUsageState state = new(); string stamp = "missing"; string? error; int dropped;
    internal string BackupPath => path + ".backup";
    internal static bool Provider(string value) => !string.IsNullOrEmpty(value) && (value is "claude" or "codex" or "ollama" or "aside" || ApiProviders.Find(value) != null);
    public AiUsageStore(string path) { this.path = path; lock (gate) Reload(); }
    static AiUsageState Copy(AiUsageState value) => JsonSerializer.Deserialize<AiUsageState>(JsonSerializer.Serialize(value, Json), Json)!;
    static void Number(long value) { if (value is < 0 or > 9_000_000_000_000_000) throw new InvalidDataException("사용 기록의 숫자 범위를 확인하세요."); }
    static void Tokens(AiTokenUsage value) { if (value == null) throw new InvalidDataException(); Number(value.Input); Number(value.Output); Number(value.CacheRead); Number(value.CacheWrite); Number(value.Total); if (!value.Reported && value.Total != 0) throw new InvalidDataException("보고되지 않은 토큰을 사용량으로 셀 수 없어요."); }
    static void Window(AiQuotaWindow value)
    {
        if (value == null || value.Provider is not ("claude" or "codex") || string.IsNullOrEmpty(value.Key) || value.Key.Length > 40 || value.Key.Any(x => !char.IsAsciiLetterOrDigit(x) && x != '_') || string.IsNullOrEmpty(value.Label) || value.Label.Length > 60 || !double.IsFinite(value.Used) || value.Used is < 0 or > 1) throw new InvalidDataException("사용 한도 정보의 형식을 확인하세요.");
    }
    static void Validate(AiUsageState value)
    {
        if (value.Version != 1 || value.Days == null || value.Windows == null || value.Recent == null || value.Days.Count > 5000 || value.Windows.Count > 16 || value.Recent.Count > 512 || value.Recent.Any(x => !Guid.TryParseExact(x, "N", out _))) throw new InvalidDataException("지원하는 AI 사용 기록 파일이 아니에요.");
        foreach (var day in value.Days)
        {
            if (!DateOnly.TryParseExact(day.Key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) || day.Value == null || day.Value.Count > 16) throw new InvalidDataException("일별 사용 기록을 확인하세요.");
            foreach (var pair in day.Value)
            {
                if (!Provider(pair.Key) || pair.Value == null) throw new InvalidDataException(); var row = pair.Value;
                foreach (var count in new[] { row.Calls, row.ReportedCalls, row.Responses, row.Failed, row.Canceled, row.Milliseconds }) Number(count);
                if (row.ReportedCalls > row.Calls || row.Responses + row.Failed + row.Canceled != row.Calls || row.Tokens == null || row.Tokens.Reported != (row.ReportedCalls > 0)) throw new InvalidDataException("요청 집계가 일치하지 않아요."); Tokens(row.Tokens);
            }
        }
        foreach (var pair in value.Windows) { Window(pair.Value); if (pair.Key != pair.Value.Provider + "|" + pair.Value.Key) throw new InvalidDataException(); }
    }
    void Reload()
    {
        try
        {
            var actual = DataFileStamp.Read(path); if (actual == stamp && error == null) return;
            if (actual == "missing") { state = new(); stamp = actual; error = null; return; }
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > MaxBytes) throw new InvalidDataException("AI 사용 기록은 4 MB 이내여야 합니다.");
            using var reader = new StreamReader(input, new UTF8Encoding(false, true)); var text = reader.ReadToEnd();
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 12 }); SettingsTransfer.CheckTree(doc.RootElement);
            var loaded = JsonSerializer.Deserialize<AiUsageState>(text, Json) ?? throw new InvalidDataException(); Validate(loaded);
            input.Position = 0; var loadedStamp = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input)); state = loaded; stamp = loadedStamp; error = null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException or OverflowException) { error = "사용 기록을 읽거나 저장하지 못했어요. 현재 파일과 .backup 파일을 확인하세요. " + ex.GetType().Name; }
    }
    public AiUsageSnapshot Snapshot(bool flush = true)
    {
        lock (gate) { Reload(); if (flush) Flush(); return new(Copy(state), error, pending.Count, dropped); }
    }
    public bool Record(AiUsageSample? sample, IEnumerable<AiQuotaWindow>? windows = null)
    {
        var quotas = windows?.ToArray() ?? Array.Empty<AiQuotaWindow>();
        if (sample != null)
        {
            if (!Guid.TryParseExact(sample.Id, "N", out _) || !Provider(sample.Provider) || sample.Outcome is not ("response" or "failed" or "canceled")) throw new ArgumentException("사용 기록의 요청 정보를 확인하세요."); Number(sample.Milliseconds); Tokens(sample.Tokens);
        }
        if (quotas.Length > 8) throw new ArgumentException("사용 한도 창이 너무 많아요."); foreach (var quota in quotas) Window(quota);
        lock (gate)
        {
            if (sample == null && quotas.Length == 0) return true;
            if (pending.Count >= 256) { dropped++; error = "저장 대기 중인 요청이 너무 많아 일부 사용 기록이 빠졌어요."; return false; }
            if (sample == null || !pending.Any(x => x.Sample?.Id == sample.Id)) pending.Add((sample, quotas));
            Reload(); return Flush();
        }
    }
    bool Flush()
    {
        if (pending.Count == 0) return error == null;
        if (error != null) return false;
        try
        {
            var next = Copy(state);
            foreach (var item in pending)
            {
                if (item.Sample is { } sample && !next.Recent.Contains(sample.Id))
                {
                    if (next.Started == null || sample.Finished < next.Started) next.Started = sample.Finished;
                    var key = sample.Finished.LocalDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    if (!next.Days.TryGetValue(key, out var day)) next.Days[key] = day = new();
                    var old = day.GetValueOrDefault(sample.Provider) ?? new(); var t = old.Tokens; var u = sample.Tokens;
                    day[sample.Provider] = old with { Calls = checked(old.Calls + 1), ReportedCalls = checked(old.ReportedCalls + (u.Reported ? 1 : 0)), Responses = checked(old.Responses + (sample.Outcome == "response" ? 1 : 0)), Failed = checked(old.Failed + (sample.Outcome == "failed" ? 1 : 0)), Canceled = checked(old.Canceled + (sample.Outcome == "canceled" ? 1 : 0)), Milliseconds = checked(old.Milliseconds + sample.Milliseconds), Tokens = new(checked(t.Input + u.Input), checked(t.Output + u.Output), checked(t.CacheRead + u.CacheRead), checked(t.CacheWrite + u.CacheWrite), old.ReportedCalls > 0 || u.Reported) };
                    next.Recent.Add(sample.Id); while (next.Recent.Count > 512) next.Recent.RemoveAt(0);
                }
                foreach (var quota in item.Windows)
                {
                    var key = quota.Provider + "|" + quota.Key;
                    if (!next.Windows.TryGetValue(key, out var previous) || quota.Seen >= previous.Seen) next.Windows[key] = quota;
                }
            }
            Validate(next); var text = JsonSerializer.Serialize(next, Json); if (Encoding.UTF8.GetByteCount(text) > MaxBytes) throw new InvalidDataException("AI 사용 기록이 4 MB를 넘어요.");
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp"; Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            try { File.WriteAllText(temp, text, new UTF8Encoding(false)); DataFileStamp.Require(path, stamp); if (File.Exists(path)) File.Replace(temp, path, BackupPath); else File.Move(temp, path); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            state = next; stamp = DataFileStamp.Text(text); pending.Clear(); error = null; return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or OverflowException) { error = "사용 기록을 저장하지 못했어요. 새로고침하면 대기 중인 기록을 다시 저장합니다. " + ex.GetType().Name; return false; }
    }
    internal static AiDailyUsage Sum(IEnumerable<AiDailyUsage> rows)
    {
        var result = new AiDailyUsage();
        foreach (var row in rows) result = result with { Calls = checked(result.Calls + row.Calls), ReportedCalls = checked(result.ReportedCalls + row.ReportedCalls), Responses = checked(result.Responses + row.Responses), Failed = checked(result.Failed + row.Failed), Canceled = checked(result.Canceled + row.Canceled), Milliseconds = checked(result.Milliseconds + row.Milliseconds), Tokens = new(result.Tokens.Input + row.Tokens.Input, result.Tokens.Output + row.Tokens.Output, result.Tokens.CacheRead + row.Tokens.CacheRead, result.Tokens.CacheWrite + row.Tokens.CacheWrite, result.ReportedCalls + row.ReportedCalls > 0) };
        return result;
    }
}

internal sealed class AiUsageAttempt : IDisposable
{
    readonly AiUsageStore store;
    readonly string provider;
    readonly Stopwatch watch = Stopwatch.StartNew();
    readonly string id = Guid.NewGuid().ToString("N");
    readonly ConcurrentDictionary<string, AiQuotaWindow> windows = new();
    readonly CancellationToken cancellation;
    AiTokenUsage tokens = new(); string outcome = "failed"; bool disposed;
    internal AiUsageAttempt(string provider, AiUsageStore? store = null, CancellationToken cancellation = default) { this.provider = provider; this.store = store ?? AiUsageStore.Shared; this.cancellation = cancellation; }
    void Quotas(IEnumerable<AiQuotaWindow> values) { foreach (var value in values) windows[value.Provider + "|" + value.Key] = value; }
    internal void ObserveApi(JsonElement root) { if (disposed) return; try { tokens = AiUsageParsing.ApiTokens(root, provider); } catch (Exception ex) when (ex is InvalidOperationException or FormatException or OverflowException) { } }
    internal void ObserveCodex(JsonElement root) { if (disposed) return; try { if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "turn.completed") tokens = AiUsageParsing.CodexTokens(root); Quotas(AiUsageParsing.CodexQuota(root, DateTimeOffset.UtcNow)); } catch (Exception ex) when (ex is InvalidOperationException or FormatException or OverflowException) { } }
    internal void ObserveClaude(JsonElement root) { if (disposed) return; try { if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("usage", out _)) tokens = AiUsageParsing.ClaudeTokens(root); Quotas(AiUsageParsing.ClaudeQuota(root, DateTimeOffset.UtcNow)); } catch (Exception ex) when (ex is InvalidOperationException or FormatException or OverflowException) { } }
    internal void Complete() => outcome = "response";
    internal void Canceled() => outcome = "canceled";
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        if (outcome != "response" && cancellation.IsCancellationRequested) outcome = "canceled";
        try { store.Record(new(id, provider, DateTimeOffset.UtcNow, watch.ElapsedMilliseconds, outcome, tokens), windows.Values); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or OverflowException) { AppPaths.Log("ai-usage", ex.GetType().Name); }
    }
}
