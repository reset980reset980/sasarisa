using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace VoiceAssistant.Desktop;

public partial class App
{
    async Task<AgentRunResult> RunAiAsync(string text, string screen, CancellationToken token)
    {
        var requestSettings = settings;
        if (requestSettings.BrainProvider == "aside")
        {
            void Progress(string value) => Dispatcher.BeginInvoke(() =>
            {
                if (token.IsCancellationRequested || closing) return;
                window.SetStatus("Aside 작업 중", value, listening, true);
                overlay.SetContent("Aside 작업 중", value, true); overlay.ShowOverlay();
            });
            void Notice(string value) => Dispatcher.BeginInvoke(() =>
            {
                AppPaths.Log("aside-stop", value);
                if (!closing) window.AddMessage("system", value);
            });
            var reply = await brain.AskAsideAsync(text, personal.MemoryContext + "\n" + webSearch.Context + "\n" + screen, requestSettings, token, Progress, Notice);
            token.ThrowIfCancellationRequested();
            return new(reply, Array.Empty<ActionObservation>(), true);
        }
        WindowsActions.InputTarget? Capture()
        { try { hands.TrackForeground(); return hands.CaptureInputTarget(); } catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception) { return null; } }
        var bound = Capture();
        var windows = new WindowTargets(hands.WindowTargetDriver());
        var claudeSessions = new ClaudeSessions(hands.ClaudeSessionDriver());
        var kakaoChats = new KakaoChats(hands.KakaoChatDriver());
        WindowsActions.KeyFocus? confirmedKeyFocus = null;
        AssistantAction? confirmedKeyAction = null;
        DesktopControls? controls = null;
        VisualControls? visual = null;
        WindowsActions.InputTarget? visualTarget = null;
        VisualControls Visual()
        {
            if (bound == null) throw new AgentStoppedException("이미지로 확인할 창을 선택하세요.");
            if (visual == null || visualTarget != bound) { visualTarget = bound; visual = new VisualControls(hands.VisualDriver(bound)); }
            return visual;
        }
        WindowsActions.InputTarget? controlsTarget = null;
        DesktopControls Controls()
        {
            if (bound == null) throw new AgentStoppedException("작업할 창을 확인하지 못했어요.");
            if (controls == null || controlsTarget != bound) { controlsTarget = bound; controls = new DesktopControls(hands.DesktopControlDriver(bound)); }
            return controls;
        }
        WindowsActions.ForegroundContext? context = hands.CaptureForegroundContext();
        BrowserAnchor? browserAnchor = null;
        try { browserAnchor = await hands.CaptureBrowserAnchorAsync(bound, token); }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
        async Task Guard(AssistantAction action)
        {
            token.ThrowIfCancellationRequested();
            if (action.Kind is "list_windows" or "select_window" or "front")
            {
                if (context != null) hands.EnsureForegroundContext(context);
                if (browserAnchor != null) await hands.EnsureBrowserAnchorAsync(browserAnchor, token);
                return;
            }
            if (BrowserTool(action.Kind))
            {
                if (context == null) throw new AgentStoppedException("작업할 브라우저 창을 선택한 뒤 다시 요청하세요.");
                hands.EnsureForegroundContext(context);
                if (browserAnchor != null) await hands.EnsureBrowserAnchorAsync(browserAnchor, token);
                return;
            }
            if (action.Kind is not ("type" or "key" or "tap" or "browser" or "read_screen" or "list_controls" or "press_control" or "fill_field" or "insert_field" or "screenshot" or "click")) return;
            if (bound == null) throw new AgentStoppedException("작업할 창을 확인하지 못해 멈췄어요. 대상 창을 선택한 뒤 다시 요청하세요.");
            if ((action.Kind is "list_controls" or "press_control" or "fill_field" or "insert_field" or "screenshot" or "click") && browserAnchor == null && (Native.ProcessName(bound.Window).ToLowerInvariant() is "chrome" or "msedge" or "firefox" or "brave")) throw new AgentStoppedException("브라우저 주소를 확인하지 못해 화면 조작을 멈췄어요. 대상 탭을 확인한 뒤 다시 요청하세요.");
            try { hands.EnsureInputTarget(bound); }
            catch (InvalidOperationException ex) { throw new AgentStoppedException(ex.Message); }
            if (browserAnchor != null) await hands.EnsureBrowserAnchorAsync(browserAnchor, token);
        }
        async Task RefreshBrowser(BrowserWindow? known, CancellationToken cancellation)
        {
            windows.Invalidate();
            controls = null; controlsTarget = null;
            visual = null; visualTarget = null;
            var candidate = Capture();
            bound = known != null && candidate?.Window == known.Handle && candidate.ProcessId == known.ProcessId && candidate.ProcessStart == known.Started ? candidate : null;
            context = bound == null ? null : hands.CaptureForegroundContext(); browserAnchor = null;
            if (bound != null) browserAnchor = await hands.CaptureBrowserAnchorAsync(bound, cancellation);
        }
        async Task<bool> Approve(AssistantAction action, CancellationToken cancellation)
            {
                await Guard(action);
                if (action.Kind == "display_off") return ActionApprovalWindow.Confirm(window, AgentRun.Describe(action), cancellation);
                if (action.Kind is "kakao_draft" or "kakao_send" or "kakao_send_draft")
                {
                    overlay.HideOverlay();
                    var kakaoPreview = await kakaoChats.PrepareAsync(action, cancellation);
                    bound = null; controls = null; visual = null; browserAnchor = null; context = hands.CaptureForegroundContext(); windows.Invalidate();
                    return ActionApprovalWindow.Confirm(window, kakaoPreview, cancellation);
                }
                if (action.Kind == "delegate_session")
                {
                    overlay.HideOverlay();
                    var handoff = await claudeSessions.PrepareAsync(action.Target, action.Value, cancellation);
                    bound = null; controls = null; visual = null; browserAnchor = null; context = hands.CaptureForegroundContext();
                    return ActionApprovalWindow.Confirm(window, handoff, cancellation);
                }
                var description = action.Kind is "press_control" or "fill_field" or "insert_field" ? Controls().Describe(action) : AgentRun.Describe(action);
                if (action.Kind == "key")
                {
                    var chord = KeyboardChord.Parse(action.Value);
                    if (chord.Matches(hotkeys?.Wake) || chord.Matches(hotkeys?.Stop)) throw new AgentStoppedException("비서의 말하기·중지 단축키는 다른 앱으로 보내지 않아요.");
                    confirmedKeyFocus = await hands.CaptureKeyFocusAsync(bound!, cancellation); confirmedKeyAction = action;
                    description += "\n키를 받을 항목: " + (confirmedKeyFocus.Name.Length == 0 ? "현재 초점 항목" : confirmedKeyFocus.Name[..Math.Min(180, confirmedKeyFocus.Name.Length)]);
                }
                if (action.Kind == "screenshot") description = "이 창의 현재 이미지를 " + Brain.NameFor(requestSettings.BrainProvider) + "에 전달합니다.";
                var preview = action.Kind == "click" ? Visual().Preview(action.Value, action.X, action.Y) : null;
                return ActionApprovalWindow.Confirm(window, $"대상: {hands.TargetLabel}\n\n{description}", cancellation, preview);
            }
        async Task<ActionOutcome> Execute(AssistantAction action, CancellationToken cancellation)
            {
                cancellation.ThrowIfCancellationRequested();
                await Guard(action);
                if (action.Kind is "open_app" or "open_url" or "type" or "key" or "tap" or "browser" or "press_control" or "fill_field" or "insert_field" or "click") windows.Invalidate();
                if (action.Kind is "open_app" or "open_url" or "type" or "key" or "tap" or "browser" or "press_control" or "fill_field" or "insert_field") visual?.Invalidate();
                if (BrowserTool(action.Kind))
                {
                    var browserResult = await RunBrowserActionAsync(action, cancellation, bound, browserAnchor);
                    await RefreshBrowser(browserControl.LastTarget, cancellation);
                    return browserResult;
                }
                switch (action.Kind)
                {
                    case "kakao_chats":
                        var chatPage = await kakaoChats.ListAsync(action.Value, action.Number, cancellation);
                        return new($"열린 카카오톡 대화방 {chatPage.Chats.Count}개를 확인했어요.", JsonSerializer.Serialize(chatPage));
                    case "kakao_draft": case "kakao_send": case "kakao_send_draft":
                        overlay.HideOverlay();
                        var chatResult = await kakaoChats.ApplyAsync(action, cancellation);
                        actionHistory.Record(chatResult.SendRequested ? "카카오톡 전송 요청" : "카카오톡 초안 입력", unavailable: "카카오톡에서 현재 초안과 전송 결과를 확인하세요.");
                        return new(KakaoChats.Summary(chatResult), JsonSerializer.Serialize(chatResult));
                    case "list_sessions":
                        var sessionPage = await claudeSessions.ListAsync(action.Value, action.Number, cancellation);
                        return new($"Claude 세션 {sessionPage.Sessions.Count}개를 확인했어요.", JsonSerializer.Serialize(sessionPage));
                    case "switch_session":
                        overlay.HideOverlay();
                        var activeSession = await claudeSessions.SelectAsync(action.Value, cancellation);
                        bound = null; controls = null; visual = null; browserAnchor = null; context = hands.CaptureForegroundContext();
                        return new("Claude에서 선택한 세션으로 이동했어요. 메시지는 보내지 않았어요.", JsonSerializer.Serialize(activeSession));
                    case "delegate_session":
                        overlay.HideOverlay();
                        var delegated = await claudeSessions.SendAsync(action.Value, cancellation);
                        actionHistory.Record("Claude 기존 세션에 작업 전달", unavailable: "이미 전달한 요청은 Claude 앱에서 중지하거나 확인하세요.");
                        return new("Claude 세션에 요청을 전달했어요. 작업 완료 여부와 권한 요청은 Claude 앱에서 확인하세요.", JsonSerializer.Serialize(delegated));
                    case "key":
                        var keyChord = KeyboardChord.Parse(action.Value); var keyFocus = confirmedKeyAction == action ? confirmedKeyFocus : null;
                        confirmedKeyFocus = null; confirmedKeyAction = null;
                        if (keyChord.NeedsConfirmation && keyFocus == null) throw new AgentStoppedException("키 입력 대상 확인이 필요해 멈췄어요.");
                        try { await hands.KeyAsync(action.Value, cancellation, bound, [hotkeys?.Wake, hotkeys?.Stop], keyFocus); }
                        catch { actionHistory.Record("중단된 키 입력", unavailable: "대상 앱에서 실제 키 입력 결과를 확인하세요."); throw; }
                        controls = null; controlsTarget = null;
                        var afterKey = Capture(); var changedWindow = afterKey != bound;
                        if (changedWindow) { bound = null; context = hands.CaptureForegroundContext(); browserAnchor = null; }
                        else if (browserAnchor != null && keyChord.MayNavigateBrowser)
                        {
                            try { browserAnchor = await hands.CaptureBrowserAnchorAsync(bound, cancellation); }
                            catch (Exception ex) when (ex is not OperationCanceledException) { bound = null; browserAnchor = null; context = hands.CaptureForegroundContext(); }
                        }
                        actionHistory.Record("키 입력 · " + keyChord.Label, unavailable: "해당 앱에서 입력 결과를 확인하고 필요한 경우 직접 되돌리세요.");
                        return new(bound == null ? "키 입력을 전달했어요. 후속 입력 전 창 목록에서 작업 대상을 다시 선택하세요." : "키 입력을 대상 앱에 전달했어요. 실제 결과는 화면에서 확인하세요.", JsonSerializer.Serialize(new { key = keyChord.Label, WindowChanged = changedWindow, CanContinue = bound != null }));
                    case "list_windows":
                        var windowPage = await windows.ListAsync(action.Value, action.Number, cancellation);
                        return new($"열린 앱 창 {windowPage.Windows.Count}개를 확인했어요.", WindowTargets.Data(windowPage));
                    case "select_window":
                        overlay.HideOverlay();
                        controls = null; controlsTarget = null; visual = null; visualTarget = null;
                        var chosen = await windows.SelectAsync(action.Value, cancellation);
                        var selected = Capture(); bound = selected?.Window == chosen.Handle && selected.ProcessId == chosen.ProcessId && selected.ProcessStart == chosen.Started ? selected : null;
                        context = hands.CaptureForegroundContext(); browserAnchor = null;
                        if (bound == null) throw new AgentStoppedException("전환한 창을 입력 대상으로 확인하지 못했어요.");
                        try { browserAnchor = await hands.CaptureBrowserAnchorAsync(bound, cancellation); }
                        catch (Exception ex) when (ex is not OperationCanceledException and not AgentStoppedException) { bound = null; }
                        return new(bound == null ? "창은 선택했지만 브라우저 주소를 확인하지 못해 입력 대상은 설정하지 않았어요." : "선택한 창으로 전환하고 다음 동작의 대상으로 확인했어요.", JsonSerializer.Serialize(new { chosen.Application, Window = chosen.Title, CanContinue = bound != null }));
                    case "front":
                        var front = await windows.FrontAsync(cancellation);
                        return new(front.Window == null ? "조작 가능한 작업 창이 선택되지 않았어요." : "현재 작업 창을 확인했어요.", JsonSerializer.Serialize(new { Application = front.Window?.Application, Window = front.Window?.Title, front.Address, front.BrowserAddressAvailable, Minimized = front.Window?.Minimized, CanContinue = bound != null && front.Window?.Handle == bound.Window }));
                    case "screenshot":
                        overlay.HideOverlay(); var snapshot = await Visual().CaptureAsync(cancellation);
                        return new("선택한 창의 화면 이미지를 확인했어요.", VisualControls.Data(snapshot), snapshot.Frame.Image);
                    case "click":
                        overlay.HideOverlay(); controls = null; controlsTarget = null;
                        try { await Visual().ClickAsync(action.Value, action.X, action.Y, cancellation); }
                        catch { actionHistory.Record("중단된 이미지 위치 누르기", unavailable: "대상 앱에서 실제 반영된 결과를 확인하세요."); throw; }
                        actionHistory.Record("확인한 이미지 위치 누르기", unavailable: "대상 앱에서 실제 반영된 결과를 확인하세요.");
                        if (browserAnchor != null && bound != null) browserAnchor = await hands.CaptureBrowserAnchorAsync(bound, cancellation);
                        return new("확인한 이미지 위치에 왼쪽 누르기를 전달했어요. 결과 화면을 다시 확인하세요.");
                    case "list_controls":
                        var page = await Controls().ListAsync(action.Value, action.Number, cancellation);
                        return new($"화면 항목 {page.Controls.Count}개를 확인했어요.", ControlObservation.Data(page));
                    case "press_control":
                        try { await Controls().PressAsync(action.Value, cancellation); }
                        catch { actionHistory.Record("중단된 화면 항목 누르기 요청", unavailable: "대상 앱에서 실제 반영된 결과를 확인하세요."); throw; }
                        actionHistory.Record("조회한 화면 항목 누르기", unavailable: "대상 앱에서 실제 반영된 결과를 확인하세요.");
                        return new("조회한 항목에 누르기를 전달했어요. 결과 화면을 다시 확인하세요.");
                    case "fill_field":
                        try { await Controls().FillAsync(action.Target, action.Value, cancellation); }
                        catch { actionHistory.Record("중단된 입력칸 글 교체 요청", unavailable: "글의 일부가 입력됐을 수 있어요. 대상 입력칸을 확인하세요."); throw; }
                        actionHistory.Record("조회한 입력칸의 글 교체", unavailable: "해당 문서에서 직접 Ctrl+Z를 사용할 수 있어요.");
                        return new("선택한 입력칸의 내용이 요청한 글과 일치하는 것을 확인했어요.", JsonSerializer.Serialize(new { characters = action.Value.Length }));
                    case "insert_field":
                        try { await Controls().InsertAsync(action.Target, action.Value, cancellation); }
                        catch { actionHistory.Record("중단된 입력칸 글 추가 요청", unavailable: "글의 일부가 입력됐을 수 있어요. 대상 입력칸을 확인하세요."); throw; }
                        actionHistory.Record("조회한 입력칸에 글 추가", unavailable: "해당 문서에서 직접 Ctrl+Z를 사용할 수 있어요.");
                        return new("선택한 입력칸에 글을 넣고 나머지 내용과 최종 커서 위치를 확인했어요.", JsonSerializer.Serialize(new { characters = action.Value.Length }));
                    case "list_timers":
                        var list = timers.Items.OrderBy(x => x.Due).ToArray();
                        return new($"진행 중인 타이머 {list.Length}개를 확인했어요.", JsonSerializer.Serialize(list.Select(x => new { id = x.Id, label = x.Label, due = x.Due, remaining_seconds = Math.Max(0, (int)Math.Ceiling((x.Due - DateTimeOffset.Now).TotalSeconds)) })));
                    case "current_time": return new("현재 시각을 확인했어요.", JsonSerializer.Serialize(new { now = DateTimeOffset.Now, time_zone = TimeZoneInfo.Local.Id }));
                    case "volume_status": return new("현재 음량을 확인했어요.", JsonSerializer.Serialize(new { percent = (int)Math.Round(VolumeControl.Capture().Level * 100) }));
                    case "read_screen": return new("선택한 창의 내용을 읽었어요.", hands.ScreenContext(bound));
                    case "youtube_find":
                        var videos = await youtube.SearchAsync(action.Value, cancellation);
                        return new($"유튜브 영상 {videos.Count}개를 찾았어요.", JsonSerializer.Serialize(videos.Select((x, i) => new { number = i + 1, title = x.Title, url = x.WatchUrl })));
                }
                var previousOwned = ownedWindows.LastOrDefault();
                var result = await ExecuteAsync(action, cancellation, bound);
                cancellation.ThrowIfCancellationRequested();
                if (action.Kind is "open_app" or "open_url" or "type" or "key" or "tap" or "browser") { controls = null; controlsTarget = null; }
                if (action.Kind == "open_app")
                {
                    // Adopt only the newly identified application window. A brokered
                    // launch must not cause a later input to go to an unrelated window.
                    var created = ownedWindows.LastOrDefault(); var candidate = Capture();
                    bound = created != null && !ReferenceEquals(created, previousOwned) && candidate?.Window == created.Handle ? candidate : null;
                    context = bound == null ? null : hands.CaptureForegroundContext(); browserAnchor = null;
                }
                else if (action.Kind == "open_url") { bound = null; context = null; browserAnchor = null; }
                else if (action.Kind == "browser" && bound != null)
                { browserAnchor = await hands.CaptureBrowserAnchorAsync(bound, cancellation); }
                return new(result ?? (action.Kind switch
                {
                    "timer" => $"{action.Number}초 타이머 ‘{action.Value}’를 만들었어요.",
                    "cancel_timers" => "모든 타이머를 취소했어요.",
                    "open_app" => "앱 실행 요청을 전달했어요.",
                    "open_url" => "브라우저에 웹 페이지 열기를 요청했어요.",
                    "type" => "대상 창에 글 입력을 전달했어요.",
                    "key" or "tap" or "browser" or "media" => "대상 앱에 동작을 전달했어요. 앱 화면의 결과는 추가 확인이 필요할 수 있어요.",
                    "volume" or "volume_delta" => $"음량이 {Math.Round(VolumeControl.Capture().Level * 100)}%예요.",
                    _ => "동작을 처리했어요."
                }));
            }
        void Starting(int step, AssistantAction action)
            {
                if (token.IsCancellationRequested) return;
                var label = AgentRun.Describe(action); if (label.Length > 160) label = label[..160] + "…";
                window.SetStatus($"{step}번째 동작", label, listening, true);
                overlay.SetContent("진행 중", label, true); overlay.ShowOverlay();
            }
        void Observed(ActionObservation observation)
            {
                if (token.IsCancellationRequested) return;
                window.AddMessage("system", $"{observation.Step}번째 동작 · {observation.Summary}");
                AppPaths.Log("agent-step", JsonSerializer.Serialize(new { observation.Round, observation.Step, observation.Action.Kind, observation.Status, observation.Summary }));
            }
        if (requestSettings.BrainProvider is "claude" or "codex")
        {
            var run = new McpAgentRun(Approve, Execute, Starting, Observed);
            await using var session = new McpSession((action, cancellation) => Dispatcher.InvokeAsync(() => run.DispatchAsync(action, cancellation)).Task.Unwrap(), token);
            window.SetStatus("생각하는 중", Brain.NameFor(requestSettings.BrainProvider) + "와 작업을 진행하고 있어요.", listening, true);
            var answer = await brain.AskAsync(text, screen, requestSettings, token, personal.MemoryContext + "\n" + webSearch.Context, mcp: session.Endpoint);
            token.ThrowIfCancellationRequested(); return run.Finish(answer);
        }
        var apiRun = new McpAgentRun(Approve, Execute, Starting, Observed);
        window.SetStatus("생각하는 중", Brain.NameFor(requestSettings.BrainProvider) + "와 작업을 진행하고 있어요.", listening, true);
        var apiAnswer = await brain.AskAsync(text, screen, requestSettings, token, personal.MemoryContext + "\n" + webSearch.Context,
            directTools: (action, cancellation) => Dispatcher.InvokeAsync(() => apiRun.DispatchAsync(action, cancellation)).Task.Unwrap());
        token.ThrowIfCancellationRequested(); return apiRun.Finish(apiAnswer);
    }
}
