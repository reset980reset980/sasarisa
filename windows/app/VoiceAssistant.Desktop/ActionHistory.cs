using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

public sealed class ActionHistory
{
    sealed record Entry(string Description, Func<CancellationToken, Task<string>>? Undo, string Unavailable, bool DocumentEdit)
    {
        public bool Undone { get; set; }
    }
    readonly List<Entry> entries = new();
    public int Version { get; private set; }
    public void Record(string description, Func<CancellationToken, Task<string>>? undo = null, string unavailable = "이 동작은 자동으로 되돌릴 수 없어요.", bool documentEdit = false)
    {
        entries.Add(new(description, undo, unavailable, documentEdit));
        Version++;
        if (entries.Count > 20) entries.RemoveAt(0);
    }
    public string Describe() => entries.Count == 0 ? "아직 실행한 동작이 없어요." : string.Join("\n", entries.TakeLast(5).Reverse().Select(x => (x.Undone ? "되돌림 · " : "실행 · ") + x.Description));
    public async Task<string> UndoAsync(CancellationToken token)
    {
        var entry = entries.LastOrDefault(x => !x.Undone);
        if (entry == null) return "되돌릴 동작이 없어요.";
        if (entry.Undo == null) return entry.Description + " · " + entry.Unavailable;
        token.ThrowIfCancellationRequested();
        var reply = await entry.Undo(token);
        entry.Undone = true;
        return reply;
    }
    public Task<string> UndoDocumentAsync(CancellationToken token)
    {
        if (entries.LastOrDefault(x => !x.Undone) is not { DocumentEdit: true })
            return Task.FromResult("마지막 동작이 복원 가능한 문서 입력이 아니에요. 문서의 글은 그대로 두었어요.");
        return UndoAsync(token);
    }
}
