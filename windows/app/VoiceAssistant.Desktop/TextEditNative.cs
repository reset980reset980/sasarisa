using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace VoiceAssistant.Desktop;

public sealed partial class WindowsActions
{
    internal ActionHistory? EditHistory { get; set; }

    // Only plain Edit controls that expose matching Value and Text patterns can
    // be restored. Rich documents may have formatting/objects not in their text.
    sealed record EditSnapshot(AutomationElement Element, FieldEditState State);
    static EditSnapshot? CaptureEdit(AutomationElement element)
    {
        try
        {
            if (element.Current.IsPassword || element.Current.ControlType != ControlType.Edit ||
                !element.TryGetCurrentPattern(ValuePattern.Pattern, out var raw) || ((ValuePattern)raw).Current.IsReadOnly) return null;
            var state = NativeControlDriver.Selection(element);
            if (state == null || FieldInsertion.Normalize(((ValuePattern)raw).Current.Value) != state.Text) return null;
            return new(element, state);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or ElementNotAvailableException) { return null; }
    }

    static string EditIdentity(AutomationElement element) => string.Join(".", element.GetRuntimeId()) + "\n" + element.Current.AutomationId + "\n" + element.Current.Name;

    async Task TrackEditAsync(string description, InputTarget expected, AutomationElement element, string expectedText,
        Func<Task> apply, CancellationToken token)
    {
        var before = CaptureEdit(element);
        BrowserAnchor? browserAnchor = null;
        if (before != null && IsBrowserProcess(Native.ProcessName(expected.Window)))
        {
            try
            {
                browserAnchor = await CaptureBrowserAnchorAsync(expected, token);
                if (browserAnchor?.Address == null) before = null;
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or ElementNotAvailableException) { before = null; }
        }
        try
        {
            await apply();
            token.ThrowIfCancellationRequested();
            var after = before == null ? null : CaptureEdit(element);
            if (before != null && after != null && after.State.Text == FieldInsertion.Normalize(expectedText))
            {
                if (browserAnchor != null) await EnsureBrowserAnchorAsync(browserAnchor, token);
                var identity = EditIdentity(element); var title = Native.Title(expected.Window);
                FieldEditState Read(CancellationToken ct)
                {
                    ct.ThrowIfCancellationRequested();
                    var current = CaptureInputTarget();
                    if (current.Window != expected.Window || current.ProcessId != expected.ProcessId || current.ProcessStart != expected.ProcessStart)
                        throw new AgentStoppedException("글을 입력했던 창을 선택한 뒤 다시 되돌려 주세요.");
                    RequireInputTarget(current);
                    if (Native.Title(current.Window) != title || EditIdentity(element) != identity || element.Current.ProcessId != current.ProcessId || element.Current.IsOffscreen || !element.Current.IsEnabled)
                        throw new AgentStoppedException("원래 문서나 입력칸이 바뀌어 되돌리지 않았어요.");
                    _ = new NativeControlDriver(this, current).Context(element);
                    if (browserAnchor != null)
                    {
                        var view = ReadBrowserUi(BrowserWindowFor(current.Window));
                        if (view.Tabs.Single(x => x.Selected).Key != browserAnchor.Tab || view.Address != browserAnchor.Address)
                            throw new AgentStoppedException("브라우저 탭이나 주소가 바뀌어 되돌리지 않았어요.");
                    }
                    return CaptureEdit(element)?.State ?? throw new AgentStoppedException("원래 입력칸의 내용을 확인할 수 없어 되돌리지 않았어요.");
                }
                var undo = new TextEditUndo(before.State, after.State,
                    ct => Task.Run(() => Read(ct), ct),
                    (original, written, ct) => Task.Run(async () =>
                    {
                        _ = Read(ct); await Focus(ct); element.SetFocus(); await Task.Delay(80, ct);
                        void Guard(string content)
                        {
                            if (Read(ct).Text != content) throw new AgentStoppedException("문서 내용이 바뀌어 되돌리기를 멈췄어요.");
                            var focused = AutomationElement.FocusedElement;
                            if (Native.GetForegroundWindow() != expected.Window || focused == null || !Automation.Compare(focused, element))
                                throw new AgentStoppedException("입력 위치가 바뀌어 되돌리기를 멈췄어요.");
                        }
                        Guard(written);
                        ((ValuePattern)element.GetCurrentPattern(ValuePattern.Pattern)).SetValue(original.Text);
                        await Task.Delay(80, ct); Guard(original.Text);
                        SelectEditRange(element, original);
                    }, ct));
                EditHistory?.Record(description, undo.RunAsync, documentEdit: true);
            }
            else EditHistory?.Record(description, unavailable: "이 입력칸은 원문과 선택 위치를 정확히 복원할 수 없어요. 문서에서 직접 실행 취소하세요.", documentEdit: true);
        }
        catch
        {
            EditHistory?.Record("중단된 " + description, unavailable: "일부가 입력됐을 수 있어 자동 복원하지 않아요. 대상 문서를 확인하세요.", documentEdit: true);
            throw;
        }
    }

    static void SelectEditRange(AutomationElement element, FieldEditState state)
    {
        var document = ((TextPattern)element.GetCurrentPattern(TextPattern.Pattern)).DocumentRange;
        if (FieldInsertion.Normalize(document.GetText(6001)) != state.Text) throw new AgentStoppedException("복원 중 문서 내용이 바뀌었어요.");
        TextPatternRange Prefix(int offset)
        {
            // UIA character units need not equal UTF-16 offsets (emoji, CRLF).
            int low = 0, high = state.Text.Length + 1;
            while (low <= high)
            {
                int count = low + (high - low) / 2;
                var range = document.Clone(); range.MoveEndpointByRange(TextPatternRangeEndpoint.End, range, TextPatternRangeEndpoint.Start);
                range.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, count);
                var length = FieldInsertion.Normalize(range.GetText(6001)).Length;
                if (length == offset) return range;
                if (length < offset) low = count + 1; else high = count - 1;
            }
            throw new InvalidOperationException("원래 선택 위치를 복원할 수 없어요.");
        }
        var start = Prefix(state.Start); var end = Prefix(state.Start + state.Length);
        var selection = document.Clone(); selection.MoveEndpointByRange(TextPatternRangeEndpoint.Start, start, TextPatternRangeEndpoint.End);
        selection.MoveEndpointByRange(TextPatternRangeEndpoint.End, end, TextPatternRangeEndpoint.End); selection.Select();
    }
}
