using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

public sealed record KakaoCommand(string Kind, string Recipient = "", string Text = "");
public static class KakaoCommands
{
    public static KakaoCommand? Parse(string text)
    {
        var input = text.Trim();
        if (Regex.IsMatch(input, @"^(?:카톡|카카오톡)\s*(?:대화방|채팅방|창)?\s*(?:목록|목록 보여줘|목록 보여 줘|대화방 보여줘)$")) return new("kakao_chats");
        if (Regex.IsMatch(input, @"^(?:카톡|카카오톡)(?:방|창)?\s*(?:초안\s*)?(?:전송|보내기|엔터)\s*(?:눌러\s*줘|눌러|해\s*줘|해)?$")) return new("kakao_send_draft");
        var match = Regex.Match(input, @"^(?:지금\s*)?(?:열려\s*있는\s*)?(?:카톡|카카오톡)(?:방|창)?\s*(?:(?:지금\s*)?(?:떠|켜져|열려)\s*있는\s*(?:거|것|창|방|곳|대화방|채팅방))?\s*(?:에다가|에서|으로|에)?\s*(?:(?<to>\S+?)(?:한테|에게|께)\s+)?(?<body>[\s\S]+?)\s*(?:이라고|라고|하고)\s*(?<operation>답장|답|보내|전송|전해|말해|입력|써|쳐|적어)(?<suffix>해\s*줘|해줘|해|\s*줘|줘|\s*주세요|주세요|해서\s*엔터까지\s*쳐)?[.!]?$", RegexOptions.CultureInvariant);
        if (!match.Success) return null;
        var body = match.Groups["body"].Value.Trim();
        if (body.Length >= 2 && ((body[0] == '"' && body[^1] == '"') || (body[0] == '“' && body[^1] == '”'))) body = body[1..^1];
        if (string.IsNullOrWhiteSpace(body)) return null;
        var inputOnly = match.Groups["operation"].Value is "입력" or "써" or "쳐" or "적어";
        var send = !inputOnly || match.Groups["suffix"].Value.Contains("엔터");
        return new(send ? "kakao_send" : "kakao_draft", match.Groups["to"].Value, body);
    }
    internal static ObservedKakaoChat Select(KakaoChatPage page, string recipient)
    {
        if (page.Next != null || page.Total != page.Chats.Count) throw new AgentStoppedException("열린 대화방이 많아요. 사용할 대화방만 남기거나 AI에게 대화방 이름을 지정하세요.");
        var name = recipient.EndsWith("님", StringComparison.Ordinal) ? recipient[..^1] : recipient;
        var matches = recipient.Length == 0 ? page.Chats.ToArray() : page.Chats.Where(x => x.Title == recipient).ToArray();
        if (matches.Length == 0 && name != recipient) matches = page.Chats.Where(x => x.Title == name).ToArray();
        if (matches.Length != 1) throw new AgentStoppedException("대화방을 하나로 확인하지 못했어요. 카카오톡에서 사용할 대화방을 열고 정확한 이름으로 요청하세요. 이름을 생략하려면 대화방 하나만 열어 주세요.");
        return matches[0];
    }
}

public partial class App
{
    async Task<string> RunKakaoAsync(KakaoCommand command, CancellationToken token)
    {
        overlay.HideOverlay(); var chats = new KakaoChats(hands.KakaoChatDriver());
        var page = await chats.ListAsync("", 0, token);
        if (command.Kind == "kakao_chats") return page.Chats.Count == 0 ? "열린 카카오톡 대화방이 없어요. 사용할 대화방을 먼저 열어 주세요." :
            "열린 카카오톡 대화방: " + string.Join(", ", page.Chats.Select(x => x.Title)) + (page.Next != null ? " 외 추가 대화방이 있어요." : ".");
        var room = KakaoCommands.Select(page, command.Recipient);
        var action = new AssistantAction(command.Kind, command.Text, Target: room.Id);
        var description = await chats.PrepareAsync(action, token);
        if (!ActionApprovalWindow.Confirm(window, description, token)) return "카카오톡 요청을 취소했어요. 초안과 메시지는 변경하지 않았어요.";
        var result = await chats.ApplyAsync(action, token);
        actionHistory.Record(result.SendRequested ? "카카오톡 전송 요청" : "카카오톡 초안 입력", unavailable: "카카오톡에서 초안과 전송 결과를 직접 확인하세요.");
        return KakaoChats.Summary(result);
    }
}
