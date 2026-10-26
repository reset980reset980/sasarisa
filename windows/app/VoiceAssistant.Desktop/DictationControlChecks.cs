using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

internal static class DictationControlChecks
{
    static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static async Task<int> RunAsync(List<object> rows)
    {
        int failed = 0;
        async Task Check(string name, Func<Task> check)
        {
            try { await check(); rows.Add(new { name, pass = true }); }
            catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.Message }); }
        }
        var settings = new UiSettings { Name = "자비스" };
        await Check("Dictation controls distinguish stop undo Enter and literal content", () =>
        {
            foreach (var phrase in new[] { "지워", "취소", "되돌려", "실행 취소", "UNDO", "delete that", "자비스, 취소!" })
            {
                Require(DictationUtterance.Parse(phrase, settings.Name).Control == DictationControl.Undo, "Missed undo: " + phrase);
                Require(!DictationProcessor.IsStop(phrase, settings.Name), "Undo stopped dictation: " + phrase);
            }
            foreach (var phrase in new[] { "줄바꿈", "엔터", "보내", "전송", "send", "newline", "자비스 enter" }) Require(DictationUtterance.Parse(phrase, settings.Name).Control == DictationControl.Enter, "Missed Enter: " + phrase);
            foreach (var phrase in new[] { "그만", "끝", "받아쓰기 끝", "자비스 멈춰" }) Require(DictationProcessor.IsStop(phrase, settings.Name), "Missed stop: " + phrase);
            foreach (var phrase in new[] { "끝까지 읽어요", "취소하는 방법입니다", "메시지를 보내 주세요", "자비스라는 이름을 적어요", "\"취소\"", "\"send\"" })
            {
                var parsed = DictationUtterance.Parse(phrase, settings.Name);
                Require(parsed.Control == DictationControl.Text && parsed.Text == phrase && !parsed.EnterAfter, "Literal became a control: " + phrase);
            }
            return Task.CompletedTask;
        });
        await Check("Dictation suffix requires a word boundary and preserves quoted content", () =>
        {
            foreach (var suffix in new[] { "엔터", "보내.", "ENTER!", "send" })
            {
                var parsed = DictationUtterance.Parse("안녕하세요. " + suffix, settings.Name);
                Require(parsed.EnterAfter && parsed.Text == "안녕하세요.", "Suffix not separated: " + suffix);
            }
            foreach (var phrase in new[] { "resend", "다시보내", "\"오늘 보내\"", "보내라고 적어요", "send later" }) Require(!DictationUtterance.Parse(phrase, settings.Name).EnterAfter, "False suffix: " + phrase);
            return Task.CompletedTask;
        });
        await Check("Explicit dictation command keeps punctuation and cannot be an AI tool", () =>
        {
            var action = CommandParser.Parse("받아적어 안녕하세요.\n내일 봬요!")!.Actions.Single();
            Require(action.Kind == "dictation_once" && action.Value == "안녕하세요.\n내일 봬요!", "Command changed literal text");
            ActionPolicy.Validate(action);
            bool rejected = false; try { ActionPolicy.ValidateAi(action); } catch (InvalidOperationException) { rejected = true; }
            Require(rejected, "AI gained an unadvertised text path");
            Require(CommandParser.Parse("취소")!.Actions.Single().Kind == "stop", "Normal command cancellation changed");
            return Task.CompletedTask;
        });
        await Check("Polish sees text without control suffix and cannot create a control", async () =>
        {
            var polish = settings with { Text = new() { PolishDictation = true } }; string prompt = ""; int calls = 0;
            Task<CommandPlan> Ask(string request, CancellationToken ct) { calls++; prompt = request; return Task.FromResult(new CommandPlan("안녕하세요.", Array.Empty<AssistantAction>())); }
            var edit = await DictationProcessor.PrepareAsync("음 안녕하세요 send", polish, CancellationToken.None, Ask);
            Require(edit.Utterance.EnterAfter && edit.Result.Polished && edit.Result.Text == "안녕하세요." && !prompt.Contains("send"), "Control suffix reached the model");
            await DictationProcessor.PrepareAsync("취소", polish, CancellationToken.None, Ask);
            Require(calls == 1, "Undo called the model");
            var literal = await DictationProcessor.PrepareAsync("안녕하세요", polish, CancellationToken.None, (_,_) => Task.FromResult(new CommandPlan("send", Array.Empty<AssistantAction>())));
            var sent = new List<string>();
            await DictationWriter.ApplyAsync(literal, _ => throw new Exception("Unneeded confirmation"), (s,_) => { sent.Add(s); return Task.CompletedTask; }, (_,_) => throw new Exception("Model output became a key"), CancellationToken.None);
            Require(sent.Single() == "send ", "Model output was interpreted as a command");
        });
        await Check("Declining dictation Enter or multiline approval leaves the document untouched", async () =>
        {
            foreach (var phrase in new[] { "안녕하세요 보내", "줄바꿈", "첫 줄\n둘째 줄" })
            {
                var edit = await DictationProcessor.PrepareAsync(phrase, settings, CancellationToken.None); int asked = 0, touched = 0;
                var applied = await DictationWriter.ApplyAsync(edit, _ => { asked++; return false; }, (_,_) => { touched++; return Task.CompletedTask; }, (_,_) => { touched++; return Task.CompletedTask; }, CancellationToken.None, () => touched++);
                Require(!applied && asked == 1 && touched == 0, "Declined operation had an effect: " + phrase);
            }
        });
        await Check("Approved dictation types before Enter and exact undo does not type", async () =>
        {
            var effects = new List<string>();
            async Task Apply(string phrase)
            {
                var edit = await DictationProcessor.PrepareAsync(phrase, settings, CancellationToken.None);
                await DictationWriter.ApplyAsync(edit, _ => { effects.Add("approve"); return true; }, (s,_) => { effects.Add("type:" + s); return Task.CompletedTask; }, (s,_) => { effects.Add("key:" + s); return Task.CompletedTask; }, CancellationToken.None,
                    settle: _ => { effects.Add("settle"); return Task.CompletedTask; });
            }
            await Apply("음 내일 봐요 보내"); await Apply("취소"); await Apply("줄바꿈");
            Require(effects.SequenceEqual(new[] { "approve", "type:내일 봐요 ", "settle", "key:enter", "key:ctrl+z", "approve", "key:enter" }), "Wrong edit sequence");
        });
        await Check("Cancellation or insertion failure prevents trailing Enter and never retries text", async () =>
        {
            var edit = await DictationProcessor.PrepareAsync("내일 봐요 보내", settings, CancellationToken.None);
            foreach (var fail in new[] { false, true })
            {
                using var stop = new CancellationTokenSource(); int typed = 0, keyed = 0; bool interrupted = false;
                try
                {
                    await DictationWriter.ApplyAsync(edit, _ => true, (_,_) => { typed++; if (fail) throw new IOException("Target changed or insertion failed"); stop.Cancel(); return Task.CompletedTask; }, (_,_) => { keyed++; return Task.CompletedTask; }, stop.Token);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException) { interrupted = true; }
                Require(interrupted && typed == 1 && keyed == 0, "Input repeated or trailing Enter escaped cancellation");
            }
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); bool reached = false;
            try { await DictationWriter.ApplyAsync(edit, _ => { reached = true; return true; }, (_,_) => { reached = true; return Task.CompletedTask; }, (_,_) => { reached = true; return Task.CompletedTask; }, cancelled.Token); }
            catch (OperationCanceledException) { }
            Require(!reached, "Already-cancelled operation reached approval/input");
        });
        await Check("Filler-only send and stop never touch the document", async () =>
        {
            foreach (var phrase in new[] { "음 으음 보내", "끝" })
            {
                var edit = await DictationProcessor.PrepareAsync(phrase, settings, CancellationToken.None);
                var applied = await DictationWriter.ApplyAsync(edit, _ => throw new Exception("Unexpected approval"), (_,_) => throw new Exception("Unexpected typing"), (_,_) => throw new Exception("Unexpected key"), CancellationToken.None);
                Require(!applied, "Empty edit reported as applied");
            }
        });
        return failed;
    }
}
