using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceAssistant.Desktop;

internal sealed record LearnedInvocation(Guid Id, string Key, CommandPlan Plan, int Version, string Stamp);

public sealed partial class Personalization
{
    readonly Func<DateTimeOffset> clock;
    readonly HashSet<Guid> recordedInvocations = new();
    readonly Queue<Guid> recordedOrder = new();
    CommandPlan? lastLearnedPlan;
    static readonly DateTimeOffset referenceDate = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);
    double LearningTime => (clock().ToUniversalTime() - referenceDate).TotalSeconds;
    static CommandPlan CopyLearning(CommandPlan plan) => new(plan.Reply, plan.Actions.ToArray());
    static bool SameLearning(CommandPlan left, CommandPlan right) => left.Reply == right.Reply && left.Actions.SequenceEqual(right.Actions);
    void ClearRecentLearning() { lastLearned = ""; lastLearnedPlan = null; }
    internal void BeginLearningCommand(string phrase)
    {
        var compact = CommandParser.Normalize(phrase).Replace(" ", "");
        if (compact is "아니" or "그게아니야" or "틀렸어") return;
        ClearRecentLearning();
    }
    internal LearnedInvocation? PrepareInvocation(string phrase, CommandPlan plan)
    {
        if (Error != null || CommandParser.RequiresObservations(phrase) || RoutineMatch(CommandParser.Normalize(phrase)) != null) return null;
        var matches = state.Learned.Where(x => Key(x.Key) == Key(phrase) && SameLearning(x.Value, plan)).Take(2).ToArray();
        if (matches.Length != 1) return null;
        return new(Guid.NewGuid(), matches[0].Key, CopyLearning(matches[0].Value), Version, StorageStamp);
    }
    bool CurrentInvocation(LearnedInvocation invocation) => invocation.Version == Version && invocation.Stamp == knownStamp && state.Learned.TryGetValue(invocation.Key, out var current) && SameLearning(current, invocation.Plan);
    internal bool ActivateInvocation(LearnedInvocation invocation)
    {
        DataFileStamp.Require(FilePath, knownStamp);
        if (!CurrentInvocation(invocation)) return false;
        lastLearned = invocation.Key; lastLearnedPlan = CopyLearning(invocation.Plan); return true;
    }
    internal bool CompleteInvocation(LearnedInvocation invocation)
    {
        if (recordedInvocations.Contains(invocation.Id)) return true;
        DataFileStamp.Require(FilePath, knownStamp);
        if (!CurrentInvocation(invocation)) return false;
        Update(next =>
        {
            var previous = next.MacLearning.GetValueOrDefault(invocation.Key) ?? new(0, LearningTime);
            next.MacLearning[invocation.Key] = previous with { Uses = previous.Uses == long.MaxValue ? long.MaxValue : previous.Uses + 1 };
            if (next.FastHits < long.MaxValue) next.FastHits++;
        });
        recordedInvocations.Add(invocation.Id); recordedOrder.Enqueue(invocation.Id);
        while (recordedOrder.Count > 64) recordedInvocations.Remove(recordedOrder.Dequeue());
        return true;
    }
    internal int PruneUnusedAutomatic()
    {
        if (Error != null) return 0;
        var time = LearningTime;
        var expired = state.MacLearning.Where(x => x.Value.Automatic && x.Value.Uses == 0 && time >= x.Value.Learned && time - x.Value.Learned >= TimeSpan.FromDays(30).TotalSeconds).Select(x => x.Key).ToArray();
        if (expired.Length == 0) return 0;
        Update(next => { foreach (var key in expired) { next.Learned.Remove(key); next.MacLearning.Remove(key); } });
        if (expired.Contains(lastLearned)) ClearRecentLearning();
        return expired.Length;
    }
}
