using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

public sealed record KakaoChat(string Key, string Title);
public sealed record ObservedKakaoChat(string Id, string Title);
public sealed record KakaoChatPage(IReadOnlyList<ObservedKakaoChat> Chats, int Total, int? Next);
public sealed record KakaoDraft(KakaoChat Chat, string Composer, string Text);
public sealed record KakaoResult(string Title, bool SendRequested, bool ComposerCleared);
public interface IKakaoChatDriver
{
    Task<IReadOnlyList<KakaoChat>> ReadAsync(CancellationToken token);
    Task<KakaoDraft> PrepareAsync(KakaoChat chat, CancellationToken token);
    Task<KakaoResult> ApplyAsync(KakaoDraft draft, string text, bool send, bool existing, CancellationToken token);
}

// Only observed open chat windows are eligible. Approval binds the exact room, input and text.
public sealed class KakaoChats(IKakaoChatDriver driver, Func<DateTimeOffset>? clock = null)
{
    // Verified Korean client cue. Unknown versions/colors remain ordinary text and are preserved.
    internal static bool IsEmptyCue(string text, object? color, string version) => text == "메시지 입력" && color is int value && value == 0x949494 && version == "26.9.0.5337";
    internal static string Lines(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');
    readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    readonly Dictionary<string, KakaoChat> observed = new(StringComparer.Ordinal);
    DateTimeOffset expires;
    (AssistantAction Action, KakaoDraft Draft, string Text)? prepared;
    public async Task<KakaoChatPage> ListAsync(string query, int offset, CancellationToken token)
    {
        observed.Clear(); prepared = null;
        if (query.Length > 120 || offset is < 0 or > 240) throw new ArgumentException("대화방 검색어나 페이지를 확인하세요.");
        var rooms = await driver.ReadAsync(token); token.ThrowIfCancellationRequested();
        if (rooms.Count > 240) throw new AgentStoppedException("열린 대화방이 너무 많아요. 사용할 대화방만 남긴 뒤 다시 요청하세요.");
        // Reject identical titles even when they fall on another page.
        var duplicates = rooms.GroupBy(x => x.Title, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matches = rooms.Where(x => x.Title.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        var page = matches.Skip(offset).Take(12).Select(x =>
        {
            var id = Guid.NewGuid().ToString("N");
            if (!duplicates.Contains(x.Title)) observed[id] = x;
            return new ObservedKakaoChat(id, x.Title);
        }).ToArray();
        expires = now().AddMinutes(2);
        return new(page, matches.Length, offset + page.Length < matches.Length ? offset + page.Length : null);
    }
    public async Task<string> PrepareAsync(AssistantAction action, CancellationToken token)
    {
        prepared = null; ActionPolicy.Validate(action);
        if (action.Kind is not ("kakao_draft" or "kakao_send" or "kakao_send_draft")) throw new ArgumentException("카카오톡 입력 요청이 필요해요.");
        if (now() >= expires || !observed.TryGetValue(action.Target, out var chat)) throw new AgentStoppedException("대화방 목록을 다시 확인하세요. 같은 이름의 열린 대화방은 하나만 남겨 주세요.");
        var draft = await driver.PrepareAsync(chat, token); token.ThrowIfCancellationRequested();
        if (draft.Chat != chat) throw new AgentStoppedException("선택한 대화방이 바뀌었어요.");
        var existing = action.Kind == "kakao_send_draft";
        if (!existing && draft.Text.Length != 0) throw new AgentStoppedException("대화방에 기존 초안이 있어 덮어쓰지 않았어요. 입력칸을 확인하세요.");
        var text = existing ? draft.Text : action.Value;
        if (string.IsNullOrWhiteSpace(text) || Lines(text).Length > 6000 || text.Any(c => char.IsControl(c) && c is not ('\r' or '\n'))) throw new AgentStoppedException("메시지는 줄바꿈을 포함해 1~6,000자로 입력하세요. 빈 초안이나 특수 제어 문자는 보내지 않아요.");
        prepared = (action, draft, text); expires = now().AddMinutes(2);
        return $"카카오톡 대화방: {chat.Title}\n\n{text}\n\n" +
            (action.Kind == "kakao_draft" ? "이 내용을 초안으로 입력합니다. 전송하지 않습니다." : "이 대화방에 위 내용을 전송합니다. 상대와 내용을 확인하세요.");
    }
    public async Task<KakaoResult> ApplyAsync(AssistantAction action, CancellationToken token)
    {
        var item = prepared; prepared = null; observed.Clear();
        token.ThrowIfCancellationRequested();
        if (item == null || item.Value.Action != action || now() >= expires) throw new AgentStoppedException("대화방과 내용을 다시 확인하세요. 이전 요청은 재전송하지 않아요.");
        return await driver.ApplyAsync(item.Value.Draft, item.Value.Text, action.Kind != "kakao_draft", action.Kind == "kakao_send_draft", token);
    }
    public static string Summary(KakaoResult result) => !result.SendRequested ? "카카오톡 대화방에 초안을 입력했어요. 전송하지 않았어요." :
        result.ComposerCleared ? "카카오톡에 전송 키를 전달하고 입력칸이 비워진 것을 확인했어요. 상대의 수신 여부는 대화방에서 확인하세요." :
        "전송 키를 한 번 전달했지만 입력칸이 비워졌는지는 확인하지 못했어요. 대화방에서 결과를 확인하세요. 자동 재전송하지 않습니다.";
}
