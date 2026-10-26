using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

public sealed record ClaudeSession(string Key, string Title, string Project);
public sealed record ClaudeSessionSnapshot(IReadOnlyList<ClaudeSession> Sessions, bool Truncated = false);
public sealed record ObservedClaudeSession(string Id, string Title, string Project);
public sealed record ClaudeSessionPage(IReadOnlyList<ObservedClaudeSession> Sessions, int Total, int? Next, bool Truncated);
public sealed record SelectedClaudeSession(string Title, string Project, string Address);
public interface IClaudeSessionDriver
{
    Task<ClaudeSessionSnapshot> ReadAsync(CancellationToken token);
    Task<SelectedClaudeSession> SelectAsync(ClaudeSession session, CancellationToken token);
    Task<SelectedClaudeSession> PrepareAsync(ClaudeSession session, CancellationToken token);
    Task SendAsync(SelectedClaudeSession selected, string text, CancellationToken token);
}

// A request can name only sessions it actually observed. Sending is a separately approved operation.
public sealed class ClaudeSessions(IClaudeSessionDriver driver, Func<DateTimeOffset>? clock = null)
{
    internal static bool IsCodeAddress(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && uri.Host == "claude.ai" && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
        System.Text.RegularExpressions.Regex.IsMatch(uri.AbsolutePath,
            @"^/epitaxy/(session_[A-Za-z0-9]+|local_[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})/?$");

    // The Code landing page exposes the same sidebar, but is never a send destination.
    internal static bool IsCodeHomeAddress(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && uri.Host == "claude.ai" && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        uri.Fragment.Length == 0 && (uri.AbsolutePath == "/epitaxy" || uri.AbsolutePath == "/epitaxy/") &&
        (uri.Query.Length == 0 || uri.Query == "?coldLaunch=1");

    readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    readonly Dictionary<string, ClaudeSession> observed = new(StringComparer.Ordinal);
    DateTimeOffset expires;
    (SelectedClaudeSession Session, string Text)? prepared;
    public async Task<ClaudeSessionPage> ListAsync(string query, int offset, CancellationToken token)
    {
        observed.Clear(); prepared = null;
        if (query.Length > 120 || offset is < 0 or > 240) throw new ArgumentException("세션 검색어나 페이지를 확인하세요.");
        var snapshot = await driver.ReadAsync(token); token.ThrowIfCancellationRequested();
        var matches = snapshot.Sessions.Where(x => (x.Title + " " + x.Project).Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        var page = matches.Skip(offset).Take(12).Select(x => { var id = Guid.NewGuid().ToString("N"); observed[id] = x; return new ObservedClaudeSession(id, x.Title, x.Project); }).ToArray();
        expires = now().AddMinutes(2);
        return new(page, matches.Length, offset + page.Length < matches.Length ? offset + page.Length : null, snapshot.Truncated);
    }
    ClaudeSession Find(string id)
    {
        if (now() >= expires || !observed.TryGetValue(id, out var session)) throw new AgentStoppedException("Claude 세션 목록을 다시 확인한 뒤 선택하세요.");
        return session;
    }
    public async Task<SelectedClaudeSession> SelectAsync(string id, CancellationToken token)
    { prepared = null; return await driver.SelectAsync(Find(id), token); }
    public async Task<string> PrepareAsync(string id, string text, CancellationToken token)
    {
        prepared = null;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 6000) throw new ArgumentException("맡길 작업은 1~6,000자로 입력하세요.");
        var selected = await driver.PrepareAsync(Find(id), token); token.ThrowIfCancellationRequested();
        prepared = (selected, text);
        return $"Claude Code · {selected.Project} / {selected.Title}\n\n이 세션에 다음 작업을 보냅니다:\n{text}\n\n이후 작업과 권한 요청은 Claude 앱에서 확인하세요.";
    }
    public async Task<SelectedClaudeSession> SendAsync(string text, CancellationToken token)
    {
        var item = prepared ?? throw new AgentStoppedException("먼저 작업을 보낼 Claude 세션과 내용을 확인하세요.");
        prepared = null; observed.Clear(); // Consume before any side effect; never replay a send.
        token.ThrowIfCancellationRequested();
        if (text != item.Text) throw new AgentStoppedException("확인한 작업 내용과 달라 전송하지 않았어요.");
        await driver.SendAsync(item.Session, text, token);
        return item.Session;
    }
}
