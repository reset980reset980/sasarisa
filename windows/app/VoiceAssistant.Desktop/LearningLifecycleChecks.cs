using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

internal static class LearningLifecycleChecks
{
    static void Require(bool value, string why) { if (!value) throw new Exception(why); }
    static void Reject(Action run) { try { run(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException) { return; } throw new Exception("Unexpected stale write"); }
    static CommandPlan Plan(int volume = 20) => new("volume", new[] { new AssistantAction("volume", Number: volume) });
    internal static Task<int> RunAsync(List<object> rows)
    {
        var failed = 0; var now = new DateTimeOffset(2026, 10, 30, 0, 0, 0, TimeSpan.Zero);
        var root = Path.Combine(AppPaths.WindowsRoot, "artifacts", "learning-lifecycle-tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        Personalization Store(string name) => new(Path.Combine(root, name + ".json"), () => now);
        LearnedInvocation Invocation(Personalization store, string phrase) => store.PrepareInvocation(phrase, store.Resolve(phrase)!) ?? throw new Exception("Stored learned invocation not found");
        void Check(string name, Action run) { try { run(); rows.Add(new { name, pass = true }); } catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.ToString() }); } }
        Check("Learning lifecycle counts successful canonical invocations once and survives restart", () =>
        {
            var store = Store("success"); store.Learn("차분하게 들려줘", Plan()); var date = store.Snapshot().MacLearning.Single().Value.Learned;
            var ticket = Invocation(store, "차분하게들려줘"); Require(store.Snapshot().FastHits == 0, "Lookup counted as execution");
            Require(store.ActivateInvocation(ticket) && store.CompleteInvocation(ticket) && store.CompleteInvocation(ticket), "Successful invocation not recorded");
            var saved = new Personalization(store.StoragePath).Snapshot(); Require(saved.FastHits == 1 && saved.MacLearning.Single().Value.Uses == 1 && saved.MacLearning.Single().Value.Learned == date, "Count duplicated, lost on restart or changed original date");
        });
        Check("Learning lifecycle veto after a restarted replay removes that exact entry and retains lifetime hits", () =>
        {
            var store = Store("veto"); store.Learn("첫 번째 방법", Plan()); store.Learn("둘째 방법", Plan(30)); store = new(store.StoragePath, () => now);
            var ticket = Invocation(store, "첫번째방법"); Require(store.ActivateInvocation(ticket) && store.CompleteInvocation(ticket), "Replay activation failed"); store.Resolve("틀렸어");
            var state = store.Snapshot(); Require(!state.Learned.ContainsKey("첫 번째 방법") && state.Learned.ContainsKey("둘째 방법") && !state.MacLearning.ContainsKey("첫 번째 방법") && state.FastHits == 1, "Veto removed another method or erased lifetime count");
        });
        Check("Learning lifecycle veto cannot delete a method edited after the relevant execution", () =>
        {
            var store = Store("edited-veto"); store.Learn("차분한 방법", Plan()); var ticket = Invocation(store, "차분한 방법"); store.ActivateInvocation(ticket);
            store.SaveLearned("차분한 방법", "edited", Plan(45).Actions, "차분한 방법", store.Version);
            var reply = store.Resolve("아니")!; Require(reply.Actions.Count == 0 && store.Snapshot().Learned["차분한 방법"].Actions.Single().Number == 45, "Stale veto erased an edited plan");
        });
        Check("Learning lifecycle rejects stale invocation versions and changed files without losing edits", () =>
        {
            var store = Store("stale"); store.Learn("조용한 방법", Plan()); var ticket = Invocation(store, "조용한 방법");
            store.SaveNote(null, "new note", store.Version); Require(!store.ActivateInvocation(ticket) && !store.CompleteInvocation(ticket) && store.Snapshot().FastHits == 0, "Stale version counted");
            ticket = Invocation(store, "조용한 방법"); var changed = File.ReadAllText(store.StoragePath) + "\n"; File.WriteAllText(store.StoragePath, changed);
            Reject(() => store.ActivateInvocation(ticket)); Reject(() => store.CompleteInvocation(ticket)); Require(File.ReadAllText(store.StoragePath) == changed && store.Snapshot().FastHits == 0, "Foreign file edit overwritten");
        });
        Check("Learning lifecycle failed or declined invocations do not count but can be vetoed", () =>
        {
            var store = Store("failed"); store.Learn("실패한 방법", Plan()); var ticket = Invocation(store, "실패한 방법");
            Require(store.ActivateInvocation(ticket) && store.Snapshot().FastHits == 0 && store.Snapshot().MacLearning.Single().Value.Uses == 0, "Starting counted as completion");
            store.Resolve("그게 아니야"); Require(store.Snapshot().Learned.Count == 0, "Failed replay could not be corrected");
            store.Learn("이전 방법", Plan()); var previous = Invocation(store, "이전 방법"); store.ActivateInvocation(previous); store.BeginLearningCommand("지금 몇 시야"); store.Resolve("아니");
            Require(store.Snapshot().Learned.ContainsKey("이전 방법"), "Correction after an unrelated command removed older learning");
        });
        Check("Learning lifecycle routine precedence and ambiguous phrases never acquire learned-use tickets", () =>
        {
            var store = Store("precedence"); store.SaveLearned("작업", "same", Plan().Actions, null, store.Version); store.SaveRoutine("작업", Plan().Actions, null, store.Version);
            Require(store.PrepareInvocation("작업", store.Resolve("작업")!) == null, "Routine counted as a learned replay");
            var state = new PersonalState { Learned = new() { ["a b"] = Plan(), ["ab"] = Plan() } }; var path = Path.Combine(root, "ambiguous.json"); File.WriteAllText(path, JsonSerializer.Serialize(state));
            Require(new Personalization(path).PrepareInvocation("a b", Plan()) == null, "Ambiguous canonical phrase acquired a ticket");
        });
        Check("Learning lifecycle removes only never-used automatic entries at the exact 30-day boundary", () =>
        {
            var store = Store("expiry"); var learnedAt = now; store.Learn("사용 안 한 방법", Plan()); store.Learn("사용한 방법", Plan(30));
            var ticket = Invocation(store, "사용한 방법"); store.ActivateInvocation(ticket); store.CompleteInvocation(ticket);
            store.SaveLearned("직접 저장한 방법", "manual", Plan(40).Actions, null, store.Version);
            now = learnedAt.AddDays(30).AddTicks(-1); Require(store.PruneUnusedAutomatic() == 0, "Early expiry"); now = learnedAt.AddDays(30); Require(store.PruneUnusedAutomatic() == 1, "Expiry boundary missed");
            var state = store.Snapshot(); Require(state.Learned.Count == 2 && state.Learned.ContainsKey("사용한 방법") && state.Learned.ContainsKey("직접 저장한 방법") && state.FastHits == 1, "Used/manual data or lifetime count pruned");
            var backup = new Personalization(store.BackupPath).Snapshot(); Require(backup.Learned.Count == 3, "Prune not recoverable from previous save");
            store.Restore(backup, store.Version); Require(store.Snapshot().Learned.Count == 3 && store.Snapshot().FastHits == 1, "Pruned methods not restorable"); now = learnedAt;
        });
        Check("Learning lifecycle preserves future dates and legacy or imported methods during cleanup", () =>
        {
            var learnedAt = now; var store = Store("legacy"); store.SaveLearned("수동 방법", "manual", Plan().Actions, null, store.Version);
            var state = store.Snapshot(); state.Learned["옛 방법"] = Plan(30); state.MacLearning.Remove("옛 방법"); state.Learned["Mac 방법"] = Plan(40); state.MacLearning["Mac 방법"] = new(0, 1);
            store.ApplySnapshot(state, store.Version, store.StorageStamp); store.Learn("미래 방법", Plan(50));
            now = learnedAt.AddDays(-1); Require(store.PruneUnusedAutomatic() == 0, "Backward clock deleted data");
            now = learnedAt.AddDays(31); var future = store.Snapshot(); future.MacLearning["미래 방법"] = future.MacLearning["미래 방법"] with { Learned = double.MaxValue }; store.ApplySnapshot(future, store.Version, store.StorageStamp);
            Require(store.PruneUnusedAutomatic() == 0 && store.Snapshot().Learned.Count == 4, "Legacy/manual/imported/future entry pruned"); now = learnedAt;
        });
        Check("Learning lifecycle manual edits preserve usage and original date while pinning the entry", () =>
        {
            var learnedAt = now; var store = Store("pin"); store.Learn("자동 방법", Plan()); var ticket = Invocation(store, "자동 방법"); store.ActivateInvocation(ticket); store.CompleteInvocation(ticket);
            var info = store.Snapshot().MacLearning.Single().Value; store.SaveLearned("수정한 방법", "new reply", Plan(35).Actions, "자동 방법", store.Version);
            Require(store.Snapshot().MacLearning["수정한 방법"] == info with { Automatic = false }, "Manual edit lost usage/date or retained automatic deletion");
            now = learnedAt.AddDays(90); Require(store.PruneUnusedAutomatic() == 0, "Manual method expired"); now = learnedAt;
        });
        Check("Learning lifecycle imported automatic metadata is pinned without changing its original uses and date", () =>
        {
            var source = Store("export"); source.Learn("가져온 방법", Plan()); var package = PersonalDataTransfer.Build(source.Snapshot(), Array.Empty<PortableApp>());
            var draft = PersonalDataTransfer.Prepare(package, new(), Array.Empty<RegisteredApp>(), new Dictionary<string, string>(), new());
            var info = draft.Next.MacLearning.Single().Value; Require(!info.Automatic && info.Uses == 0 && info.Learned == source.Snapshot().MacLearning.Single().Value.Learned && !draft.Description.Contains("Mac",StringComparison.Ordinal), "Import auto-prune flag, source label or original metadata incorrect");
            var path = Path.Combine(root, "imported.json"); File.WriteAllText(path, JsonSerializer.Serialize(draft.Next)); var imported = new Personalization(path, () => now.AddDays(90));
            Require(imported.PruneUnusedAutomatic() == 0, "Explicitly imported method expired");
        });
        Check("Learning lifecycle persistence failures leave memory counts versions and existing backups unchanged", () =>
        {
            var store = Store("locked"); store.Learn("잠긴 방법", Plan()); var ticket = Invocation(store, "잠긴 방법"); store.ActivateInvocation(ticket); var version = store.Version; var before = File.ReadAllBytes(store.StoragePath);
            using (var held = new FileStream(store.StoragePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Reject(() => store.CompleteInvocation(ticket));
            Require(store.Version == version && store.Snapshot().FastHits == 0 && File.ReadAllBytes(store.StoragePath).SequenceEqual(before), "Failed write mutated state");
            Require(store.CompleteInvocation(ticket) && store.Snapshot().FastHits == 1, "Accounting could not be retried independently of execution");
        });
        Check("Learning lifecycle owns automatic plans and relearning resets only entry usage and date", () =>
        {
            var learnedAt = now; var store = Store("own"); var actions = new[] { new AssistantAction("volume", Number: 20) }; store.Learn("복사한 방법", new("owned", actions)); actions[0] = new("volume", Number: 99);
            Require(store.Resolve("복사한 방법")!.Actions.Single().Number == 20, "Caller mutated stored plan after learning");
            var ticket = Invocation(store, "복사한 방법"); store.ActivateInvocation(ticket); store.CompleteInvocation(ticket); now = learnedAt.AddHours(1); store.Learn("복사한방법", Plan(35));
            var state = store.Snapshot(); Require(state.Learned.Count == 1 && state.MacLearning.Single().Value.Uses == 0 && state.MacLearning.Single().Value.Automatic && state.MacLearning.Single().Value.Learned > (learnedAt - new DateTimeOffset(2001,1,1,0,0,0,TimeSpan.Zero)).TotalSeconds && state.FastHits == 1, "Relearning duplicated a phrase or reset lifetime count"); now = learnedAt;
        });
        Check("Learning lifecycle counts saturate safely and snapshot restore includes lifetime stats", () =>
        {
            var store = Store("overflow"); store.SaveLearned("한도 방법", "done", Plan().Actions, null, store.Version); var snapshot = store.Snapshot(); snapshot.FastHits = long.MaxValue; snapshot.MacLearning["한도 방법"] = new(long.MaxValue, 1);
            store.ApplySnapshot(snapshot, store.Version, store.StorageStamp); var ticket = Invocation(store, "한도 방법"); store.ActivateInvocation(ticket); Require(store.CompleteInvocation(ticket), "Saturated count rejected execution");
            Require(store.Snapshot().FastHits == long.MaxValue && store.Snapshot().MacLearning.Single().Value.Uses == long.MaxValue, "Count overflowed");
            store.Restore(snapshot with { FastHits = 12 }, store.Version); Require(new Personalization(store.StoragePath).Snapshot().FastHits == 12, "Snapshot restore lost lifetime count");
        });
        Check("Learning lifecycle reads legacy optional metadata and rejects corrupt negative counts without overwrite", () =>
        {
            var path = Path.Combine(root, "old.json"); var old = "{\"Routines\":{},\"Learned\":{\"legacy\":{\"Reply\":\"done\",\"Actions\":[{\"Kind\":\"volume\",\"Value\":\"\",\"Number\":20}]}},\"Notes\":[],\"MacLearning\":{\"legacy\":{\"Uses\":2,\"Learned\":1}}}"; File.WriteAllText(path, old);
            var store = new Personalization(path); Require(store.Error == null && store.Snapshot().FastHits == 0 && !store.Snapshot().MacLearning.Single().Value.Automatic, "Legacy data rejected or made eligible for deletion");
            var bad = Path.Combine(root, "bad.json"); File.WriteAllText(bad, old[..^1] + ",\"FastHits\":-1}"); var invalid = new Personalization(bad); Require(invalid.Error != null, "Corrupt counts accepted"); Reject(() => invalid.SaveNote(null, "do not overwrite", 0));
            Require(File.ReadAllText(bad).EndsWith("\"FastHits\":-1}"), "Corrupt source overwritten");
        });
        return Task.FromResult(failed);
    }
}
