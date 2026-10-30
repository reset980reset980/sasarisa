using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace VoiceAssistant.Desktop;

public sealed partial class WindowsActions
{
    public IDesktopControlDriver DesktopControlDriver(InputTarget expected) => new NativeControlDriver(this, expected);

    sealed class NativeControlDriver(WindowsActions owner, InputTarget expected) : IDesktopControlDriver
    {
        readonly Dictionary<string, AutomationElement> elements = new(StringComparer.Ordinal);
        static string Short(string? value, int max = 100) => string.IsNullOrEmpty(value) ? "" : value.Length <= max ? value : value[..max];
        static string Identity(AutomationElement element) => string.Join(".", element.GetRuntimeId());
        static bool IsField(AutomationElement element) => element.Current.ControlType == ControlType.Edit || element.Current.ControlType == ControlType.Document;
        static string? Text(AutomationElement element)
        {
            if (element.Current.IsPassword) throw new AgentStoppedException("암호 입력란은 읽거나 수정하지 않아요.");
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var value)) return ((ValuePattern)value).Current.Value;
            if (element.TryGetCurrentPattern(TextPattern.Pattern, out var text)) return ((TextPattern)text).DocumentRange.GetText(6001);
            return null;
        }
        static bool Editable(AutomationElement element)
        {
            if (!IsField(element) || !element.Current.IsKeyboardFocusable) return false;
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var value)) return !((ValuePattern)value).Current.IsReadOnly;
            if (element.TryGetCurrentPattern(TextPattern.Pattern, out var text))
                return ((TextPattern)text).DocumentRange.GetAttributeValue(TextPattern.IsReadOnlyAttribute) is bool readOnly && !readOnly;
            return element.Current.IsPassword && ScreenAccess.AllowsPasswordInput;
        }
        internal static FieldEditState? Selection(AutomationElement element)
        {
            if (element.Current.IsPassword) throw new AgentStoppedException("암호 입력란은 읽거나 수정하지 않아요.");
            if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var value)) return null;
            var pattern = (TextPattern)value;
            if (pattern.SupportedTextSelection == SupportedTextSelection.None) return null;
            var ranges = pattern.GetSelection(); if (ranges == null || ranges.Length != 1) return null;
            var document = pattern.DocumentRange; var selection = ranges[0];
            if (selection.CompareEndpoints(TextPatternRangeEndpoint.Start, document, TextPatternRangeEndpoint.Start) < 0 || selection.CompareEndpoints(TextPatternRangeEndpoint.End, document, TextPatternRangeEndpoint.End) > 0) return null;
            var prefix = document.Clone(); prefix.MoveEndpointByRange(TextPatternRangeEndpoint.End, selection, TextPatternRangeEndpoint.Start);
            var suffix = document.Clone(); suffix.MoveEndpointByRange(TextPatternRangeEndpoint.Start, selection, TextPatternRangeEndpoint.End);
            var full = FieldInsertion.Normalize(document.GetText(6001));
            var before = FieldInsertion.Normalize(prefix.GetText(6001)); var selected = FieldInsertion.Normalize(selection.GetText(6001)); var after = FieldInsertion.Normalize(suffix.GetText(6001));
            if (full != before + selected + after || full.Length > 6000) return null;
            var state = new FieldEditState(full, before.Length, selected.Length); state.Validate(); return state;
        }
        static FieldEditState? TrySelection(AutomationElement element)
        {
            try { return Selection(element); }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or ElementNotAvailableException) { return null; }
        }
        internal string Context(AutomationElement element)
        {
            var labels = new List<string>(); var current = TreeWalker.ControlViewWalker.GetParent(element);
            for (var depth = 0; current != null && depth < 24; depth++, current = TreeWalker.ControlViewWalker.GetParent(current))
            {
                if (current.Current.NativeWindowHandle == expected.Window.ToInt64()) return string.Join(" / ", labels.AsEnumerable().Reverse());
                if (current.Current.IsPassword || current.Current.ProcessId != expected.ProcessId) break;
                if (labels.Count < 3 && current.Current.Name.Length > 0) labels.Add(Short(current.Current.Name, 40));
            }
            throw new AgentStoppedException("화면 항목이 원래 창에 속하지 않아 멈췄어요.");
        }
        internal DesktopControl? Describe(AutomationElement element)
        {
            var current = element.Current;
            if (current.ProcessId != expected.ProcessId || current.IsPassword && !ScreenAccess.AllowsPasswordInput || current.IsOffscreen || !current.IsEnabled) return null;
            var role = current.ControlType.ProgrammaticName.Replace("ControlType.", "");
            var operation = ""; var state = "";
            if (current.ControlType is var kind && (kind == ControlType.Button || kind == ControlType.Hyperlink || kind == ControlType.MenuItem) && element.TryGetCurrentPattern(InvokePattern.Pattern, out _)) operation = "invoke";
            else if (element.TryGetCurrentPattern(TogglePattern.Pattern, out var toggle)) { operation = "toggle"; state = ((TogglePattern)toggle).Current.ToggleState.ToString(); }
            else if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var select)) { operation = "select"; state = ((SelectionItemPattern)select).Current.IsSelected ? "selected" : "unselected"; }
            else if (element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expand) && ((ExpandCollapsePattern)expand).Current.ExpandCollapseState != ExpandCollapseState.LeafNode) { operation = "expand_or_collapse"; state = ((ExpandCollapsePattern)expand).Current.ExpandCollapseState.ToString(); }
            var sensitive=current.IsPassword;var editable = Editable(element); var text = editable && !sensitive ? Text(element) : null;
            if(sensitive)state="contents-hidden";
            if (text?.Length > 6000) editable = false;
            if (operation.Length == 0 && !editable) return null;
            var revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Native.Title(expected.Window) + "\n" + current.AutomationId + "\n" + current.Name + "\n" + (text ?? ""))));
            var selection = editable && !sensitive ? TrySelection(element) : null;
            return new(Identity(element), role, Short(current.Name), Context(element), operation, editable, state, revision, selection?.Start, selection?.Length ?? 0,sensitive);
        }
        AutomationElement Validate(DesktopControl control, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); owner.RequireInputTarget(expected);
            if (!elements.TryGetValue(control.Key, out var element)) throw new AgentStoppedException("조회하지 않은 화면 항목이에요.");
            try
            {
                var current = Describe(element);
                // Whole-field replacement and button operations do not depend on
                // the old selection. InsertAsync checks it separately before input.
                if (current == null || current with { SelectionStart = control.SelectionStart, SelectionLength = control.SelectionLength } != control) throw new AgentStoppedException("화면 항목이나 입력 내용이 바뀌어 멈췄어요. 목록을 다시 확인하세요.");
            }
            catch (ElementNotAvailableException) { throw new AgentStoppedException("원래 화면 항목이 사라져 멈췄어요."); }
            token.ThrowIfCancellationRequested(); owner.RequireInputTarget(expected); return element;
        }
        public Task<DesktopControlSnapshot> ReadAsync(CancellationToken token) => Task.Run(() =>
        {
            token.ThrowIfCancellationRequested(); owner.RequireInputTarget(expected); elements.Clear();
            var root = AutomationElement.FromHandle(expected.Window); var controls = new List<DesktopControl>();
            var timer = Stopwatch.StartNew(); var visited = 0; var truncated = false;
            void Walk(AutomationElement element, int depth)
            {
                token.ThrowIfCancellationRequested();
                if (depth > 24 || visited++ >= 800 || controls.Count >= 240 || timer.ElapsedMilliseconds > 2000) { truncated = true; return; }
                try
                {
                    if (element.Current.IsPassword && !ScreenAccess.AllowsPasswordInput || element.Current.IsOffscreen || element.Current.ProcessId != expected.ProcessId) return;
                    if (element != root && Describe(element) is { } item && !elements.ContainsKey(item.Key)) { elements.Add(item.Key, element); controls.Add(item); }
                    if(element.Current.IsPassword)return; // Never traverse or read the secret subtree.
                    var child = TreeWalker.ControlViewWalker.GetFirstChild(element);
                    while (child != null && !truncated) { Walk(child, depth + 1); child = TreeWalker.ControlViewWalker.GetNextSibling(child); }
                }
                catch (ElementNotAvailableException) { }
            }
            Walk(root, 0); token.ThrowIfCancellationRequested(); owner.RequireInputTarget(expected);
            return new DesktopControlSnapshot(Native.ProcessName(expected.Window), Short(Native.Title(expected.Window), 180), controls, truncated);
        }, token);
        public Task PressAsync(DesktopControl control, CancellationToken token) => Task.Run(async () =>
        {
            _ = Validate(control, token); await owner.Focus(token); var element = Validate(control, token);
            if (Native.GetForegroundWindow() != expected.Window) throw new AgentStoppedException("작업할 창이 바뀌어 누르지 않았어요.");
            switch (control.Operation)
            {
                case "invoke": ((InvokePattern)element.GetCurrentPattern(InvokePattern.Pattern)).Invoke(); break;
                case "toggle": ((TogglePattern)element.GetCurrentPattern(TogglePattern.Pattern)).Toggle(); break;
                case "select": ((SelectionItemPattern)element.GetCurrentPattern(SelectionItemPattern.Pattern)).Select(); break;
                case "expand_or_collapse":
                    var expand = (ExpandCollapsePattern)element.GetCurrentPattern(ExpandCollapsePattern.Pattern);
                    if (expand.Current.ExpandCollapseState == ExpandCollapseState.Expanded) expand.Collapse(); else expand.Expand(); break;
                default: throw new InvalidOperationException("누르기를 지원하지 않는 항목이에요.");
            }
            token.ThrowIfCancellationRequested();
        }, token);
        public Task FillAsync(DesktopControl control, string text, CancellationToken token) => Task.Run(async () =>
        {
            _ = Validate(control, token); await owner.Focus(token); var element = Validate(control, token);
            element.SetFocus(); await Task.Delay(80, token); _ = Validate(control, token);
            void GuardField()
            {
                token.ThrowIfCancellationRequested(); owner.RequireInputTarget(expected);
                var focused = AutomationElement.FocusedElement;
                if (Native.GetForegroundWindow() != expected.Window || focused == null || focused.Current.IsPassword && !ScreenAccess.AllowsPasswordInput || !Automation.Compare(focused, element)) throw new AgentStoppedException("입력칸이 바뀌어 글 입력을 멈췄어요.");
            }
            if(control.Sensitive)
            {
                void GuardSecret(){GuardField();if(new[]{0x10,0x11,0x12,0x5B,0x5C}.Any(Native.KeyDown))throw new AgentStoppedException("보조 키를 놓은 뒤 다시 요청하세요.");}
                await owner.TrackEditAsync("암호 입력 요청",expected,element,"",()=>SensitiveFieldWriter.ReplaceAsync(text,GuardSecret,()=>Native.Press(0x41,0x11),()=>Native.Press(0x08),Native.Type,token),token);
                return;
            }
            await owner.TrackEditAsync("조회한 입력칸의 글 교체", expected, element, text, async () =>
            {
            GuardField(); Native.Press(0x41, 0x11); // Select only the explicitly observed field.
            if (text.Length == 0) { GuardField(); Native.Press(0x08); }
            for (var offset = 0; offset < text.Length; offset += 64) { GuardField(); Native.Type(text.Substring(offset, Math.Min(64, text.Length - offset))); await Task.Yield(); }
            await Task.Delay(100, token); GuardField();
            var actual = Text(element)?.Replace("\r\n", "\n").TrimEnd('\r');
            if (actual != text.Replace("\r\n", "\n").TrimEnd('\r')) throw new InvalidOperationException("입력은 전달했지만 입력칸의 최종 내용이 일치하지 않아요. 화면을 확인하세요.");
            }, token);
        }, token);
        public Task InsertAsync(DesktopControl control, string text, CancellationToken token) => Task.Run(async () =>
        {
            var element = Validate(control, token);
            var before = Selection(element) ?? throw new AgentStoppedException("입력칸의 커서 위치를 확인하지 못했어요.");
            if (before.Start != control.SelectionStart || before.Length != control.SelectionLength) throw new AgentStoppedException("확인을 기다리는 동안 커서·선택 영역이 바뀌어 입력하지 않았어요.");
            await owner.Focus(token); element = Validate(control, token); element.SetFocus(); await Task.Delay(80, token);
            FieldEditState Read()
            {
                token.ThrowIfCancellationRequested(); owner.RequireInputTarget(expected);
                var focused = AutomationElement.FocusedElement;
                if (Native.GetForegroundWindow() != expected.Window || focused == null || focused.Current.IsPassword || !Automation.Compare(focused, element)) throw new AgentStoppedException("입력칸이 바뀌어 글 추가를 멈췄어요.");
                _ = Context(element);
                if (!Editable(element)) throw new AgentStoppedException("입력칸이 읽기 전용으로 바뀌었어요.");
                return Selection(element) ?? throw new AgentStoppedException("입력칸의 선택 영역을 더 이상 확인할 수 없어요.");
            }
            await owner.TrackEditAsync("조회한 입력칸에 글 추가", expected, element, before.Apply(FieldInsertion.Normalize(text)).Text,
                () => FieldInsertion.RunAsync(before, text, Read, part =>
            {
                if (new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(Native.KeyDown)) throw new AgentStoppedException("보조 키가 눌려 있어 글 추가를 멈췄어요.");
                Native.Type(part);
            }, token), token);
        }, token);
    }
}
