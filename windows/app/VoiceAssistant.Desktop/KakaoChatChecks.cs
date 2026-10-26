using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

internal static class KakaoChatChecks
{
    sealed class Driver : IKakaoChatDriver
    {
        public KakaoChat[] Rooms = [new("room-a", "시험방")];
        public string Draft = "";
        public int Writes, Sends;
        public bool FailAfterSend, Cleared = true;
        public Task<IReadOnlyList<KakaoChat>> ReadAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<KakaoChat>>(Rooms);
        public Task<KakaoDraft> PrepareAsync(KakaoChat chat, CancellationToken token) => Task.FromResult(new KakaoDraft(chat, "composer", Draft));
        public Task<KakaoResult> ApplyAsync(KakaoDraft draft, string text, bool send, bool existing, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (draft.Text != Draft || !Rooms.Contains(draft.Chat)) throw new AgentStoppedException("Changed room or draft");
            if (!existing) { Draft = text; Writes++; }
            if (send) { Sends++; if (FailAfterSend) throw new AgentStoppedException("Uncertain result"); if (Cleared) Draft = ""; }
            return Task.FromResult(new KakaoResult(draft.Chat.Title, send, send && Cleared));
        }
    }
    static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    static async Task Stopped(Func<Task> work)
    { try { await work(); } catch (AgentStoppedException) { return; } throw new Exception("Expected stop"); }
    static async Task<AssistantAction> Action(KakaoChats chats, string kind = "kakao_draft", string text = "확인 문장")
    { var page = await chats.ListAsync("", 0, default); return new(kind, text, Target: page.Chats[0].Id); }
    public static async Task<int> RunAsync(List<object> rows)
    {
        var failed = 0;
        async Task Check(string name, Func<Task> test)
        { try { await test(); rows.Add(new { name, pass = true }); } catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.Message }); } }
        await Check("Kakao Korean draft and send commands preserve recipient and body", () =>
        {
            foreach (var (text, kind, recipient, body) in new[] {
                ("카톡 떠 있는 거 영웅님한테 아 힘들다라고 답장해", "kakao_send", "영웅님", "아 힘들다"),
                ("카톡에 알겠어라고 보내", "kakao_send", "", "알겠어"),
                ("카카오톡으로 엄마한테 오늘 늦어요라고 보내 줘", "kakao_send", "엄마", "오늘 늦어요"),
                ("지금 열려있는 카톡창에 오늘도 힘드네요 라고 입력해줘", "kakao_draft", "", "오늘도 힘드네요"),
                ("카톡에 \"Hello 😀\"라고 입력해 줘", "kakao_draft", "", "Hello 😀"),
                ("카톡 지금 켜져 있는 곳에다가 오늘도 화이팅 하십시오 라고 입력해서 엔터까지 쳐", "kakao_send", "", "오늘도 화이팅 하십시오") })
                Require(KakaoCommands.Parse(text) == new KakaoCommand(kind, recipient, body), "Parse mismatch: " + text);
            foreach (var text in new[] { "카톡 열어", "카톡에 안녕이라고 보내지 마", "카톡에 안녕이라고 보내는 방법", "카톡 전송하지 마", "카톡 사용법" }) Require(KakaoCommands.Parse(text) == null, "Unintended action: " + text);
            Require(KakaoCommands.Parse("카톡방 전송 눌러줘")?.Kind == "kakao_send_draft", "Existing draft not parsed");
            return Task.CompletedTask;
        });
        await Check("Kakao cue recognition requires exact text, native color and verified client version", () =>
        {
            Require(KakaoChats.IsEmptyCue("메시지 입력", 0x949494, "26.9.0.5337"), "Verified cue rejected");
            foreach (var color in new object?[] { 0, null, "9737364", 0x808080 }) Require(!KakaoChats.IsEmptyCue("메시지 입력", color, "26.9.0.5337"), "Real or unknown text removed");
            Require(!KakaoChats.IsEmptyCue("메시지 입력", 0x949494, "future") && !KakaoChats.IsEmptyCue("실제 초안", 0x949494, "26.9.0.5337"), "Unverified cue accepted");
            Require(KakaoChats.Lines("첫째\r\n둘째\r셋째") == "첫째\n둘째\n셋째", "Newline conversion"); return Task.CompletedTask;
        });
        await Check("Kakao exact room selection rejects missing and ambiguous recipients", async () =>
        {
            var rooms = new KakaoChatPage([new("a", "엄마"), new("b", "엄마 모임")], 2, null);
            Require(KakaoCommands.Select(rooms, "엄마님").Id == "a", "Exact honorific lookup failed");
            await Stopped(() => Task.FromResult(KakaoCommands.Select(rooms, "")));
            await Stopped(() => Task.FromResult(KakaoCommands.Select(rooms, "엄")));
            var driver = new Driver { Rooms = [new("a", "같은 이름"), new("b", "같은 이름")] }; var chats = new KakaoChats(driver);
            await Stopped(async () => await chats.PrepareAsync(await Action(chats), default));
        });
        await Check("Kakao room IDs expire and older pages cannot be reused", async () =>
        {
            var time = DateTimeOffset.UtcNow; var driver = new Driver { Rooms = Enumerable.Range(0, 15).Select(i => new KakaoChat(i.ToString(), "방" + i)).ToArray() };
            var chats = new KakaoChats(driver, () => time); var first = await chats.ListAsync("", 0, default);
            Require(first.Chats.Count == 12 && first.Next == 12, "Pagination");
            var old = new AssistantAction("kakao_draft", "내용", Target: first.Chats[0].Id);
            var second = await chats.ListAsync("", 12, default); Require(second.Chats.Count == 3 && second.Next == null, "Final page");
            await Stopped(() => chats.PrepareAsync(old, default));
            var action = await Action(chats); time = time.AddMinutes(3); await Stopped(() => chats.PrepareAsync(action, default));
            action = await Action(chats); await chats.PrepareAsync(action, default); time = time.AddMinutes(3); await Stopped(() => chats.ApplyAsync(action, default));
            Require(driver.Writes == 0 && driver.Sends == 0, "Expired request mutated");
        });
        await Check("Kakao existing drafts and placeholder-like text are never overwritten", async () =>
        {
            foreach (var existing in new[] { "사용자 초안", "메시지 입력", " ", "\r\n" })
            {
                var driver = new Driver { Draft = existing }; var chats = new KakaoChats(driver); var action = await Action(chats);
                await Stopped(() => chats.PrepareAsync(action, default)); Require(driver.Draft == existing && driver.Writes == 0, "Draft overwritten");
            }
        });
        await Check("Kakao approval displays full recipient and body and cancellation has no effects", async () =>
        {
            foreach (var kind in new[] { "kakao_draft", "kakao_send", "kakao_send_draft" })
            {
                var driver = new Driver { Draft = kind == "kakao_send_draft" ? "기존 초안" : "" }; var chats = new KakaoChats(driver);
                var action = await Action(chats, kind, kind == "kakao_send_draft" ? "" : "새 문장");
                var run = new McpAgentRun(async (a, token) => { var preview = await chats.PrepareAsync(a, token); Require(preview.Contains("시험방") && preview.Contains(kind == "kakao_send_draft" ? "기존 초안" : "새 문장"), "Approval incomplete"); return false; },
                    async (a, token) => new ActionOutcome(KakaoChats.Summary(await chats.ApplyAsync(a, token))), null, null);
                await run.DispatchAsync(action, default); Require(driver.Writes == 0 && driver.Sends == 0, "Declined action executed");
            }
        });
        await Check("Kakao draft-only writes once without sending and changed approved actions are consumed", async () =>
        {
            var driver = new Driver(); var chats = new KakaoChats(driver); var action = await Action(chats);
            await chats.PrepareAsync(action, default); var result = await chats.ApplyAsync(action, default);
            Require(driver.Draft == "확인 문장" && driver.Writes == 1 && driver.Sends == 0 && !result.SendRequested, "Draft sent");
            await Stopped(() => chats.ApplyAsync(action, default));
            driver.Draft = ""; action = await Action(chats); await chats.PrepareAsync(action, default);
            await Stopped(() => chats.ApplyAsync(action with { Kind = "kakao_send" }, default));
            await Stopped(() => chats.ApplyAsync(action, default)); Require(driver.Sends == 0, "Changed operation sent");
        });
        await Check("Kakao changed room, changed draft and cancellation prevent execution", async () =>
        {
            foreach (var mode in new[] { "draft", "room", "cancel" })
            {
                var driver = new Driver(); var chats = new KakaoChats(driver); var action = await Action(chats, "kakao_send"); await chats.PrepareAsync(action, default);
                if (mode == "draft") driver.Draft = "직접 수정"; if (mode == "room") driver.Rooms = [];
                using var cancel = new CancellationTokenSource(); if (mode == "cancel") cancel.Cancel();
                try { await chats.ApplyAsync(action, cancel.Token); throw new Exception("Expected cancellation"); } catch (Exception ex) when (ex is AgentStoppedException or OperationCanceledException) { }
                Require(driver.Writes == 0 && driver.Sends == 0, "Changed target was written");
                await Stopped(() => chats.ApplyAsync(action, default));
            }
        });
        await Check("Kakao sends an approved existing draft once and does not retry uncertain sends", async () =>
        {
            foreach (var uncertain in new[] { false, true })
            {
                var driver = new Driver { Draft = "보낼 초안", FailAfterSend = uncertain }; var chats = new KakaoChats(driver); var action = await Action(chats, "kakao_send_draft", "");
                await chats.PrepareAsync(action, default);
                if (uncertain) await Stopped(() => chats.ApplyAsync(action, default)); else Require((await chats.ApplyAsync(action, default)).ComposerCleared, "No observation");
                await Stopped(() => chats.ApplyAsync(action, default)); Require(driver.Writes == 0 && driver.Sends == 1, "Rewritten or repeated");
            }
        });
        await Check("Kakao tool validation and generic input protection apply to every provider", () =>
        {
            Require(WindowsActions.IsProtectedProcess("KakaoTalk"), "Generic bypass");
            foreach (var kind in new[] { "kakao_draft", "kakao_send", "kakao_send_draft" })
            {
                Require(McpTools.Catalog.Any(x => x.Name == kind), "Missing tool");
                var action = new AssistantAction(kind, kind == "kakao_send_draft" ? "" : "내용", Target: Guid.NewGuid().ToString("N"));
                ActionPolicy.ValidateAi(action); Require(ActionPolicy.NeedsConfirmation(action, true) && ActionPolicy.NeedsConfirmation(action, false), "No approval");
                Require(!Personalization.CanReuse([action]), "Message learned as routine");
                try { ActionPolicy.ValidateAi(action with { Target = "guess" }); throw new Exception("Guessed ID allowed"); } catch (InvalidOperationException) { }
            }
            return Task.CompletedTask;
        });
        return failed;
    }
}
