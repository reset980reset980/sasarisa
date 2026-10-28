using System;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

internal sealed class SendPreview(Func<DateTimeOffset>? clock = null)
{
    readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    readonly TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    DateTimeOffset expires;
    public bool Pending => !completion.Task.IsCompleted && now() < expires;
    public void Begin() => expires = now().AddSeconds(20);
    public void Cancel() => completion.TrySetResult(false);
    public bool Answer(string text, bool remote)
    {
        if (!Pending || remote) { Cancel(); return false; }
        var answer = ExecutionConsent.ParseAnswer(text);
        completion.TrySetResult(answer == FreeModeAnswer.Yes);
        return answer != FreeModeAnswer.None;
    }
    public async Task<bool> WaitAsync(CancellationToken token)
    {
        try { return await completion.Task.WaitAsync(TimeSpan.FromSeconds(20), token); }
        catch (TimeoutException) { Cancel(); return false; }
    }
}
