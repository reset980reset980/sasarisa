using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

public partial class App
{
    readonly List<OwnedLaunch> ownedLaunches = new();
    internal IBrowserPageHost? PageHostForCheck { get; set; }
    OwnedLaunch RememberLaunch(string label, Func<CancellationToken, Task<string>> close, IDisposable? lifetime = null)
    {
        var launch = new OwnedLaunch(label, close, lifetime); ownedLaunches.Add(launch);
        if (ownedLaunches.Count > 20) { ownedLaunches[0].Dispose(); ownedLaunches.RemoveAt(0); }
        return launch;
    }
    void RememberUntrackedLaunch(string label) => RememberLaunch(label, _ => throw new AgentStoppedException("마지막으로 연 항목을 특정하지 못했어요. 앞서 연 다른 창을 닫지 않도록 직접 확인해 주세요."));
    async Task OpenTrackedUrlAsync(string url, CancellationToken token)
    {
        ActionPolicy.Validate(new("open_url", url)); token.ThrowIfCancellationRequested();
        var host = PageHostForCheck ?? hands.PageHost(); var before = await host.CaptureAsync(token);
        bool attempted = false, recorded = false;
        try
        {
            token.ThrowIfCancellationRequested(); attempted = true; host.Open(url); webSearch.Opened(url); token.ThrowIfCancellationRequested();
            BrowserPageIdentity? page = null;
            if (before.Complete)
                for (int i = 0; i < 12; i++)
                {
                    await Task.Delay(250, token); var after = await host.CaptureAsync(token);
                    page = BrowserPageOwnership.Find(before, after, url); if (page != null || !after.Complete) break;
                }
            var description = "웹 페이지 열기 · " + new Uri(url).IdnHost;
            IDisposable? lifetime = null; var close = page == null ? null : host.Own(page, out lifetime);
            if (close != null)
            {
                var launch = RememberLaunch(description, close, lifetime); actionHistory.Record(description, launch.CloseAsync);
            }
            else { lifetime?.Dispose(); RememberUntrackedLaunch(description); actionHistory.Record(description, unavailable: "새로 열린 탭 하나와 주소를 특정하지 못했어요. 기존 탭을 닫지 않도록 직접 확인해 주세요."); }
            recorded = true;
        }
        catch
        {
            if (attempted && !recorded) { RememberUntrackedLaunch("중단된 웹 페이지 열기 요청"); actionHistory.Record("중단된 웹 페이지 열기 요청", unavailable: "페이지가 열렸을 수 있어요. 새 탭을 확실히 식별하지 못해 자동으로 닫지 않아요."); }
            throw;
        }
    }
}
