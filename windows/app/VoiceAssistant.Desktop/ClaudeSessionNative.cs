using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace VoiceAssistant.Desktop;

public sealed partial class WindowsActions
{
    public IClaudeSessionDriver ClaudeSessionDriver() => new NativeClaudeSessions(this);

    // This exception to the generic protected-app policy is limited to the verified package identity's
    // Code sidebar, empty prompt and Send button. Generic typing/keys remain prohibited.
    sealed class NativeClaudeSessions(WindowsActions owner) : IClaudeSessionDriver
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern int GetPackageFamilyName(IntPtr process, ref uint length, StringBuilder name);
        static readonly SemaphoreSlim nativeGate = new(1, 1);
        readonly Dictionary<string, (AutomationElement Element, string Name, ClaudeSession Session)> entries = new(StringComparer.Ordinal);
        IntPtr window;
        int processId;
        long processStart;
        ForegroundContext? context;
        bool truncated;
        (SelectedClaudeSession Session, string Composer, string EmptyValue)? ready;
        static string Key(AutomationElement e) => string.Join(".", e.GetRuntimeId());
        static bool Named(AutomationElement e, params string[] names) => names.Contains(e.Current.Name, StringComparer.OrdinalIgnoreCase);
        static AutomationElement[] Find(AutomationElement root, ControlType type) => root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, type)).Cast<AutomationElement>().ToArray();
        static bool Official(Process process)
        {
            uint length = 256; var name = new StringBuilder((int)length);
            return process.ProcessName.Equals("claude", StringComparison.OrdinalIgnoreCase) && GetPackageFamilyName(process.Handle, ref length, name) == 0 && name.ToString() == "Claude_pzs8sxrjxfjjc";
        }
        void Locate()
        {
            var found = new List<(IntPtr Window, int Pid, long Started)>();
            foreach (var handle in OwnedWindow.VisibleWindows())
            {
                try
                {
                    var pid = GetPid(handle); using var process = Process.GetProcessById(pid);
                    if (Official(process) && Native.Title(handle).Length > 0) found.Add((handle, pid, process.StartTime.ToUniversalTime().Ticks));
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
            if (found.Count != 1) throw new AgentStoppedException("공식 Claude Windows 앱의 Code 창을 하나 열어 주세요. 여러 창이 있으면 사용할 창만 남긴 뒤 다시 요청하세요.");
            (window, processId, processStart) = found[0];
        }
        AutomationElement Root()
        {
            if (!Native.IsWindow(window) || GetPid(window) != processId) throw new AgentStoppedException("Claude 창이 바뀌어 중단했어요.");
            using var process = Process.GetProcessById(processId);
            if (process.StartTime.ToUniversalTime().Ticks != processStart || !Official(process)) throw new AgentStoppedException("Claude 앱의 실행 상태를 확인하지 못했어요.");
            return AutomationElement.FromHandle(window);
        }
        void Guard(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (context == null) throw new AgentStoppedException("Claude 세션 목록을 먼저 확인하세요.");
            owner.EnsureForegroundContext(context); _ = Root();
        }
        static string Value(AutomationElement element)
        {
            if (element.Current.IsPassword || !element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern)) throw new AgentStoppedException("Claude 입력칸의 내용을 확인하지 못했어요.");
            return ((ValuePattern)pattern).Current.Value;
        }
        (AutomationElement Document, string Title, string Address)? CurrentOrNull()
        {
            try
            {
                var matches = Root().FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "RootWebArea")).Cast<AutomationElement>()
                    .Where(e => e.Current.ControlType == ControlType.Document && e.TryGetCurrentPattern(ValuePattern.Pattern, out _))
                    .Select(e => (Document: e, Title: e.Current.Name, Address: Value(e))).Where(x => ClaudeSessions.IsCodeAddress(x.Address)).ToArray();
                if (matches.Length > 1) throw new AgentStoppedException("Claude Code 세션 화면이 여러 개라 대상을 확인하지 못했어요.");
                if (matches.Length == 0 || !matches[0].Title.EndsWith(" - Claude Code", StringComparison.Ordinal)) return null;
                return (matches[0].Document, matches[0].Title[..^14], matches[0].Address);
            }
            catch (ElementNotAvailableException) { return null; } // A Code tab replaces its document during navigation.
        }
        (AutomationElement Document, string Title, string Address) Current() => CurrentOrNull() ??
            throw new AgentStoppedException("Claude 앱의 Code 세션 화면을 확인하지 못했어요. Code 탭에서 세션을 열어 주세요.");
        AutomationElement SidebarDocument()
        {
            var documents = Root().FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "RootWebArea")).Cast<AutomationElement>()
                .Where(e => e.Current.ControlType == ControlType.Document && e.TryGetCurrentPattern(ValuePattern.Pattern, out _))
                .Where(e => ClaudeSessions.IsCodeAddress(Value(e)) || ClaudeSessions.IsCodeHomeAddress(Value(e))).ToArray();
            if (documents.Length != 1) throw new AgentStoppedException("Claude 앱의 Code 화면을 하나 열어 주세요. 새 세션 화면에서도 기존 세션을 찾을 수 있어요.");
            return documents[0];
        }
        static AutomationElement Composer(AutomationElement document)
        {
            var fields = Find(document, ControlType.Edit).Where(e => Named(e, "프롬프트", "Prompt") && e.Current.IsEnabled && !e.Current.IsPassword && !e.Current.IsOffscreen).ToArray();
            if (fields.Length != 1 || !fields[0].TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) || ((ValuePattern)pattern).Current.IsReadOnly) throw new AgentStoppedException("Claude의 편집 가능한 프롬프트를 하나로 확인하지 못했어요.");
            return fields[0];
        }
        static AutomationElement[] ActiveButtons(AutomationElement document, params string[] names) =>
            document.FindAll(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.IsEnabledProperty, true),
                new PropertyCondition(AutomationElement.IsOffscreenProperty, false),
                new OrCondition(names.Select(name => (Condition)new PropertyCondition(AutomationElement.NameProperty, name, PropertyConditionFlags.IgnoreCase)).ToArray())))
            .Cast<AutomationElement>().ToArray();
        static bool Busy(AutomationElement document) => ActiveButtons(document, "중지", "Stop", "Stop response", "Stop generating").Length != 0;
        static void Invoke(AutomationElement element, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!element.Current.IsEnabled || !element.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern)) throw new AgentStoppedException("Claude 항목을 누를 수 없어요.");
            ((InvokePattern)pattern).Invoke();
        }
        async Task<T> Run<T>(Func<CancellationToken, T> work, CancellationToken token)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(TimeSpan.FromSeconds(12));
            var cancellation = limit.Token;
            try { return await Task.Run(async () =>
            {
                await nativeGate.WaitAsync(cancellation);
                try { return work(cancellation); }
                finally { nativeGate.Release(); }
            }, cancellation).WaitAsync(cancellation); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { throw new AgentStoppedException("Claude 화면 응답이 늦어 중단했어요. 세션과 입력칸의 현재 상태를 확인하세요. 자동으로 재전송하지 않습니다."); }
            catch (Exception ex) when (ex is ElementNotAvailableException or System.ComponentModel.Win32Exception or ArgumentException)
            { AppPaths.Log("claude-session-error", ex.ToString()); throw new AgentStoppedException("Claude 창이나 화면 항목을 다시 확인해야 해요. 세션과 입력칸을 확인한 뒤 다시 요청하세요."); }
        }
        public Task<ClaudeSessionSnapshot> ReadAsync(CancellationToken token) => Run(cancellation =>
        {
            entries.Clear(); ready = null; truncated = false; context = owner.CaptureForegroundContext(); Locate(); Guard(cancellation);
            var document = SidebarDocument();
            var show = ActiveButtons(document, "사이드바 표시", "사이드바 열기", "Show sidebar", "Open sidebar");
            if (show.Length == 1) { Invoke(show[0], cancellation); Thread.Sleep(150); Guard(cancellation); document = SidebarDocument(); }
            var sidebars = document.FindAll(TreeScope.Descendants, new OrCondition(new PropertyCondition(AutomationElement.NameProperty, "사이드바"), new PropertyCondition(AutomationElement.NameProperty, "Sidebar"))).Cast<AutomationElement>().ToArray();
            var sessions = new List<ClaudeSession>();
            foreach (var sidebar in sidebars)
            {
                string project = "", previous = ""; AutomationElement? candidate = null;
                foreach (var button in Find(sidebar, ControlType.Button))
                {
                    Guard(cancellation); var name = button.Current.Name;
                    const string section = " 섹션에서 새 세션";
                    if (name.EndsWith(section, StringComparison.Ordinal)) project = name[..^section.Length];
                    var title = SessionTitle(name);
                    if (title != null && candidate != null && (previous == title || previous.StartsWith(title + " ", StringComparison.Ordinal)) && project.Length > 0)
                    {
                        var key = Key(candidate);
                        if (!entries.ContainsKey(key))
                        {
                            var entry = new ClaudeSession(key, title, project); entries.Add(key, (candidate, previous, entry)); sessions.Add(entry);
                        }
                    }
                    previous = name; candidate = button;
                    if (sessions.Count >= 240) { truncated = true; return new ClaudeSessionSnapshot(sessions, true); }
                }
            }
            Guard(cancellation);
            if (sessions.Count == 0) throw new AgentStoppedException("Claude Code 사이드바에서 세션을 찾지 못했어요. 사이드바와 프로젝트를 펼친 뒤 다시 요청하세요.");
            return new ClaudeSessionSnapshot(sessions);
        }, token);
        internal static string? SessionTitle(string name)
        {
            const string suffix = "에 대한 더 많은 옵션";
            return name.EndsWith(suffix, StringComparison.Ordinal) && name.Length > suffix.Length ? name[..^suffix.Length].TrimEnd() : null;
        }
        SelectedClaudeSession Select(ClaudeSession session, CancellationToken token)
        {
            ready = null; Guard(token);
            if (truncated) throw new AgentStoppedException("Claude 세션이 너무 많아 제목 중복 여부를 확인하지 못했어요. 사용할 프로젝트만 펼친 뒤 다시 요청하세요.");
            if (!entries.TryGetValue(session.Key, out var entry) || entry.Session != session || entry.Element.Current.Name != entry.Name || Key(entry.Element) != session.Key) throw new AgentStoppedException("Claude 세션 목록이 바뀌었어요. 다시 조회하세요.");
            if (entries.Values.Count(x => x.Session.Title == session.Title) != 1) throw new AgentStoppedException("같은 제목의 세션이 여러 개라 전환 결과를 확인할 수 없어요. Claude에서 제목을 구분한 뒤 다시 요청하세요.");
            // Activate through the application's input thread; keep UIA work on the MTA worker.
            var beforeFocus = Native.ProcessName(Native.GetForegroundWindow()); var requested = false;
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                token.ThrowIfCancellationRequested(); owner.EnsureForegroundContext(context!);
                Native.ShowWindow(window, 9); requested = Native.SetForegroundWindow(window);
            });
            Thread.Sleep(120); token.ThrowIfCancellationRequested();
            AppPaths.Log("claude-focus", $"before={beforeFocus} requested={requested} after={Native.ProcessName(Native.GetForegroundWindow())}");
            if (Native.GetForegroundWindow() != window) throw new AgentStoppedException("Claude 창으로 전환하지 못했어요. 직접 창을 선택한 뒤 다시 요청하세요.");
            owner.TrackForeground(); context = owner.CaptureForegroundContext(); Guard(token);
            Invoke(entry.Element, token);
            for (var attempt = 0; attempt < 40; attempt++)
            {
                Guard(token); var current = CurrentOrNull();
                if (current is { } loaded && loaded.Title == session.Title) return new(loaded.Title, session.Project, loaded.Address);
                Thread.Sleep(75);
            }
            throw new AgentStoppedException("선택한 Claude 세션으로 전환됐는지 확인하지 못했어요.");
        }
        public Task<SelectedClaudeSession> SelectAsync(ClaudeSession session, CancellationToken token) => Run(cancellation => Select(session, cancellation), token);
        public Task<SelectedClaudeSession> PrepareAsync(ClaudeSession session, CancellationToken token) => Run(cancellation =>
        {
            var selected = Select(session, cancellation);
            for (var attempt = 0; attempt < 40; attempt++)
            {
                Guard(cancellation);
                try
                {
                    var current = CurrentOrNull();
                    if (current is { } loaded)
                    {
                        if (loaded.Address != selected.Address || Busy(loaded.Document)) throw new AgentStoppedException("Claude가 이미 작업 중이거나 세션이 바뀌어 새 작업을 보내지 않았어요.");
                        var composer = Composer(loaded.Document);
                        var draft = Value(composer);
                        if (!string.IsNullOrWhiteSpace(draft)) throw new AgentStoppedException("Claude 입력칸에 기존 초안이 있어 덮어쓰거나 보내지 않았어요. 초안을 먼저 확인하세요.");
                        // Chromium's empty contenteditable can expose a line break. Preserve the exact
                        // observed empty representation so even a whitespace edit during approval stops.
                        ready = (selected, Key(composer), draft); return selected;
                    }
                }
                catch (ElementNotAvailableException) { } // Read-only retry while the selected tab settles.
                Thread.Sleep(75);
            }
            throw new AgentStoppedException("Claude 세션의 입력칸이 아직 준비되지 않았어요. 화면을 확인한 뒤 다시 요청하세요.");
        }, token);
        public async Task SendAsync(SelectedClaudeSession selected, string text, CancellationToken token)
        {
            await Run(cancellation =>
            {
                var prepared = ready; ready = null; Guard(cancellation);
                if (prepared == null || prepared.Value.Session != selected) throw new AgentStoppedException("확인한 Claude 세션과 달라 전송하지 않았어요.");
                var current = Current(); var composer = Composer(current.Document);
                if (current.Address != selected.Address || current.Title != selected.Title || Key(composer) != prepared.Value.Composer || Value(composer) != prepared.Value.EmptyValue || Busy(current.Document)) throw new AgentStoppedException("확인 중 Claude 세션·초안·실행 상태가 바뀌어 전송하지 않았어요.");
                Guard(cancellation); ((ValuePattern)composer.GetCurrentPattern(ValuePattern.Pattern)).SetValue(text);
                Thread.Sleep(150); Guard(cancellation); current = Current();
                if (current.Address != selected.Address || Key(Composer(current.Document)) != prepared.Value.Composer || Value(composer) != text) throw new AgentStoppedException("Claude 초안이 확인한 내용과 달라 보내지 않았어요. 입력칸을 확인하세요.");
                var send = ActiveButtons(current.Document, "보내기", "Send", "Send message");
                if (send.Length != 1 || Busy(current.Document)) throw new AgentStoppedException("Claude 보내기 버튼을 확인하지 못했어요. 초안은 입력칸에서 확인하세요.");
                Guard(cancellation); Invoke(send[0], cancellation);
                for (var attempt = 0; attempt < 40; attempt++)
                {
                    Guard(cancellation); current = Current();
                    if (current.Address != selected.Address) throw new AgentStoppedException("전송 후 Claude 세션이 바뀌었어요. 결과를 직접 확인하세요. 재전송하지 않습니다.");
                    if (string.IsNullOrWhiteSpace(Value(Composer(current.Document)))) return true;
                    Thread.Sleep(75);
                }
                throw new AgentStoppedException("Claude의 전송 접수를 확인하지 못했어요. 결과를 직접 확인하세요. 재전송하지 않습니다.");
            }, token);
        }
    }
}
