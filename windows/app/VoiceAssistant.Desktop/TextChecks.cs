using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

internal static class TextChecks
{
    static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    static void Reject(Action action) { try { action(); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException) { return; } throw new Exception("Unexpected acceptance"); }
    public static async Task<int> RunAsync(List<object> rows)
    {
        int failed = 0;
        async Task Check(string name, Func<Task> test) { try { await test(); rows.Add(new { name, pass = true }); } catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.Message }); } }
        await Check("Dictation removes whole fillers and preserves word interiors and lines", () =>
        {
            var options = new TextPreferences();
            Require(DictationProcessor.Clean("음, 음식을 먹고\n으음... 음악을 들어요.", options) == "음식을 먹고\n 음악을 들어요.", "Content word or newline changed");
            Require(DictationProcessor.Clean("음 으음 음음", options) == "", "Fillers not removed");
            Require(DictationProcessor.Clean("음 그대로", options with { RemoveFillers = false }) == "음 그대로", "Disabled cleanup modified content");
            Require(DictationProcessor.Clean("um, umbrella uh", options with { FillerWords = new[] { "um", "uh" } }) == "umbrella", "Custom fillers mismatch");
            return Task.CompletedTask;
        });
        await Check("Vocabulary and dictation preferences survive old and new settings", () =>
        {
            Require(!JsonSerializer.Deserialize<UiSettings>("{}")!.Text.PolishDictation, "Polish enabled by default");
            var settings = new UiSettings { Text = new() { CustomWords = new[] { "인천상아초등학교" }, FillerWords = Array.Empty<string>(), PolishDictation = true } };
            var copy = JsonSerializer.Deserialize<UiSettings>(JsonSerializer.Serialize(settings))!; SettingsStore.Validate(copy);
            Require(copy.Text.PolishDictation && copy.Text.CustomWords.Single() == "인천상아초등학교" && copy.Text.FillerWords.Length == 0, "Text settings not persisted");
            Reject(() => new TextPreferences { CustomWords = new[] { "bad\nword" } }.Validate()); Reject(() => new TextPreferences { CustomWords = Enumerable.Repeat("word",151).ToArray() }.Validate()); Reject(() => new TextPreferences { FillerWords = new[] { "two words" } }.Validate());
            return Task.CompletedTask;
        });
        await Check("Dictation end commands remain distinct from dictated content", () =>
        {
            foreach (var phrase in new[] { "끝", "받아쓰기 끝", "자비스, 받아쓰기 끝", "자비스 멈춰" }) Require(DictationProcessor.IsStop(phrase, "자비스"), "Missed end command: " + phrase);
            foreach (var phrase in new[] { "끝까지 읽어 보세요", "자비스라는 이름을 적습니다", "오늘 수업 끝" }) Require(!DictationProcessor.IsStop(phrase, "자비스"), "Content treated as stop: " + phrase);
            return Task.CompletedTask;
        });
        await Check("Dictation polish uses vocabulary and never executes proposed actions", async () =>
        {
            var settings = new UiSettings { Text = new() { PolishDictation = true, CustomWords = new[] { "학교" } } }; string request = "";
            var result = await DictationProcessor.ProcessAsync("음 학교 에 갑니다", settings, CancellationToken.None, (prompt, _) => { request = prompt; return Task.FromResult(new CommandPlan("학교에 갑니다.", Array.Empty<AssistantAction>())); });
            Require(result.Polished && result.Text == "학교에 갑니다." && request.Contains("transcript") && request.Contains("words"), "Polish result missing");
            var unsafeResult = await DictationProcessor.ProcessAsync("음 학교 에 갑니다", settings, CancellationToken.None, (_,_) => Task.FromResult(new CommandPlan("학교에 갑니다.", new[] { new AssistantAction("open_app", "notepad") })));
            Require(!unsafeResult.Polished && unsafeResult.Text == "학교 에 갑니다" && unsafeResult.Notice != null, "Tool proposal used for dictation");
        });
        await Check("Dictation timeout invalid result and network failure retain cleaned source", async () =>
        {
            var settings = new UiSettings { Text = new() { PolishDictation = true } };
            foreach (var reply in new[] { "", new string('x', 1000), "a" })
            {
                var result = await DictationProcessor.ProcessAsync("음 학교 에 갑니다", settings, CancellationToken.None, (_,_) => Task.FromResult(new CommandPlan(reply, Array.Empty<AssistantAction>())));
                Require(!result.Polished && result.Text == "학교 에 갑니다", "Bad rewrite accepted");
            }
            var timed = await DictationProcessor.ProcessAsync("음 학교 에 갑니다", settings, CancellationToken.None, async (_, ct) => { await Task.Delay(1000, ct); return new("too late", Array.Empty<AssistantAction>()); }, TimeSpan.FromMilliseconds(20));
            Require(timed.Text == "학교 에 갑니다" && timed.Notice != null, "Timeout lost source");
            var failedResult = await DictationProcessor.ProcessAsync("음 학교 에 갑니다", settings, CancellationToken.None, (_,_) => throw new IOException("network"));
            Require(failedResult.Text == "학교 에 갑니다" && failedResult.Notice != null, "Failure lost source");
        });
        await Check("Cancelled dictation never returns fallback for insertion", async () =>
        {
            using var stop = new CancellationTokenSource(); bool rejected = false;
            try { await DictationProcessor.ProcessAsync("학교 에 갑니다", new UiSettings { Text = new() { PolishDictation = true } }, stop.Token, (_,_) => { stop.Cancel(); return Task.FromResult(new CommandPlan("학교에 갑니다.", Array.Empty<AssistantAction>())); }); }
            catch (OperationCanceledException) { rejected = true; }
            Require(rejected, "Cancelled text could still be inserted");
        });
        var folder = Path.Combine(AppPaths.WindowsRoot, "artifacts", "personal-tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        await Check("Personal editor saves edits deletes restores and survives restart", () =>
        {
            var file = Path.Combine(folder, "personal.json"); var store = new Personalization(file);
            store.SaveNote(null, "검증용 기억", store.Version); store.SaveNote(0, "수정한 기억", store.Version);
            var before = store.Snapshot(); store.Remove("notes", "0", store.Version); Require(store.Snapshot().Notes.Count == 0, "Note deletion failed"); store.Restore(before, store.Version);
            store.SaveRoutine("검증 루틴", new[] { new AssistantAction("timer", "검증", 60) }, null, store.Version);
            store.SaveRoutine("바꾼 루틴", new[] { new AssistantAction("timer", "검증", 90) }, "검증 루틴", store.Version);
            store.SaveLearned("검증 호출", "실행했어요.", new[] { new AssistantAction("volume", Number: 30) }, null, store.Version);
            var reloaded = new Personalization(file); Require(reloaded.Snapshot().Notes.Single() == "수정한 기억" && reloaded.Resolve("바꾼 루틴")!.Actions.Single().Number == 90 && reloaded.Resolve("검증 호출")!.Actions.Single().Number == 30, "Editor changes not restored");
            reloaded.Remove("learned", "검증 호출", reloaded.Version); Require(new Personalization(file).Resolve("검증 호출") == null, "Learned deletion not persisted"); return Task.CompletedTask;
        });
        await Check("Stale personal edit or undo cannot overwrite newer changes", () =>
        {
            var store = new Personalization(Path.Combine(folder, "stale.json")); store.SaveNote(null, "처음", 0); var before = store.Snapshot(); var oldVersion = store.Version;
            store.SaveNote(null, "나중", store.Version); Reject(() => store.Restore(before, oldVersion)); Reject(() => store.SaveNote(0, "잘못된 수정", oldVersion));
            Require(store.Snapshot().Notes.SequenceEqual(new[] { "처음", "나중" }), "Newer changes overwritten"); return Task.CompletedTask;
        });
        await Check("Personal write failure and corrupt file preserve authoritative data", () =>
        {
            var file = Path.Combine(folder, "locked.json"); var store = new Personalization(file); store.SaveNote(null, "원래 내용", 0); var version = store.Version;
            using (File.Open(file, FileMode.Open, FileAccess.Read, FileShare.None)) Reject(() => store.SaveNote(0, "실패할 수정", version));
            Require(store.Version == version && store.Snapshot().Notes.Single() == "원래 내용", "In-memory state changed before write");
            var corrupt = Path.Combine(folder, "corrupt.json"); File.WriteAllText(corrupt, "{\"Learned\":null}"); var broken = new Personalization(corrupt);
            Require(broken.Error != null, "Corrupt file hidden"); Reject(() => broken.SaveNote(null, "덮어쓰기", 0)); Require(File.ReadAllText(corrupt) == "{\"Learned\":null}", "Corrupt source overwritten"); return Task.CompletedTask;
        });
        await Check("Routine editor rejects unsafe actions duplicate names and overlong plans", () =>
        {
            var store = new Personalization(Path.Combine(folder, "boundaries.json")); store.SaveRoutine("작업 시작", new[] { new AssistantAction("volume", Number: 30) }, null, 0);
            Reject(() => store.SaveRoutine("작업시작", new[] { new AssistantAction("volume", Number: 20) }, null, store.Version));
            Reject(() => store.SaveRoutine("글 입력", new[] { new AssistantAction("type", "hello") }, null, store.Version));
            Reject(() => store.SaveRoutine("많은 동작", Enumerable.Repeat(new AssistantAction("volume", Number: 30),9), null, store.Version));
            Reject(() => Personalization.ParseStep("안녕하세요 라고 써 줘")); Reject(() => Personalization.ParseStep("https://user:secret@example.com"));
            Require(Personalization.ParseStep("https://example.com").Actions.Single().Kind == "open_url", "URL step not supported"); return Task.CompletedTask;
        });
        return failed;
    }
}
