using System;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

// A UIA provider can block inside a control pattern, including Invoke.
// Keep the dispatcher available and do not queue another mutation behind it.
internal sealed class BrowserAutomationWork
{
    int busy;
    internal bool IsBusy => Volatile.Read(ref busy) != 0;
    internal async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken token, TimeSpan? limit = null)
    {
        token.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) throw new AgentStoppedException("이전 브라우저 접근이 아직 끝나지 않아 추가 닫기를 보내지 않았어요. 브라우저의 확인창을 확인해 주세요.");
        var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var work = Task.Run(async () => { stop.Token.ThrowIfCancellationRequested(); return await action(stop.Token).ConfigureAwait(false); });
        _ = ReleaseAsync();
        try { return await work.WaitAsync(limit ?? TimeSpan.FromSeconds(8), token); }
        catch (TimeoutException)
        {
            Cancel(); throw new AgentStoppedException("브라우저 응답 대기를 중단했어요. 닫기 요청이 전달됐을 수 있으니 탭과 저장·나가기 확인창을 확인해 주세요.");
        }
        catch (OperationCanceledException) { Cancel(); throw; }
        void Cancel() { try { stop.Cancel(); } catch (ObjectDisposedException) { } }
        async Task ReleaseAsync()
        {
            try { await work.ConfigureAwait(false); } catch { /* The caller observes the result; also observe a late failure. */ }
            finally { stop.Dispose(); Volatile.Write(ref busy, 0); }
        }
    }
}
