using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

public partial class App
{
    static bool BrowserTool(string kind) => kind.StartsWith("browser_", StringComparison.Ordinal) || kind == "mail_select";
    async Task<ActionOutcome> RunBrowserActionAsync(AssistantAction action, CancellationToken token, WindowsActions.InputTarget? expected = null, BrowserAnchor? anchor = null)
    {
        var driver = hands.BrowserDriver(expected, anchor);
        window.SetStatus("브라우저 확인 중", "탭과 주소를 확인하고 있어요. 중지 버튼으로 멈출 수 있어요.", listening, true);
        if (action.Kind == "browser_tabs")
        {
            var page = await browserControl.PageAsync(driver, action.Value == "all", action.Number, token);
            var data = BrowserObservation.Data(page);
            var summary = page.Total == 0 ? "열려 있는 탭이 없어요." : string.Join("\n", page.Tabs.Select(x => $"{x.Number}. {x.Title} — {x.Url ?? "주소 확인 안 됨"}")) + $"\n전체 {page.Total}개 · {page.Page}/{page.Pages}페이지";
            if (page.Page < page.Pages) summary += $"\n‘{(action.Value == "all" ? "전체 " : "")}탭 목록 {page.Page + 1}페이지’로 이어서 확인하세요.";
            return new(summary, data);
        }
        var reply = action.Kind switch
        {
            "browser_select" => await browserControl.SelectAsync(driver, action.Value, false, token),
            "browser_close" => await browserControl.SelectAsync(driver, action.Value, true, token),
            "browser_select_nth" => await browserControl.NthAsync(driver, action.Number, false, token),
            "browser_close_nth" => await browserControl.NthAsync(driver, action.Number, true, token),
            "browser_close_site" => await browserControl.CloseSiteAsync(driver, action.Value, token),
            "mail_select" => await browserControl.MailAsync(driver, action.Number, token),
            _ => throw new InvalidOperationException("지원하지 않는 브라우저 동작이에요.")
        };
        if (action.Kind is "browser_close" or "browser_close_nth" or "browser_close_site") actionHistory.Record(reply, unavailable: "닫은 탭은 브라우저에서 Ctrl+Shift+T로 다시 여세요. 저장하지 않은 페이지 상태는 브라우저에 따라 다릅니다.");
        else actionHistory.Record(reply);
        return new(reply);
    }
}
