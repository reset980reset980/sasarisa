using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace VoiceAssistant.Desktop;

public sealed partial class WindowsActions
{
    public IKakaoChatDriver KakaoChatDriver() => new NativeKakaoChats(this);
    sealed class NativeKakaoChats(WindowsActions owner) : IKakaoChatDriver
    {
        sealed record Entry(KakaoChat Chat, IntPtr Window, int Pid, long Started, string Root, string Composer);
        readonly Dictionary<string, Entry> entries = new();
        static readonly SemaphoreSlim gate = new(1, 1);
        ForegroundContext? context;
        KakaoDraft? ready;
        [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr ReadText(IntPtr window, uint message, UIntPtr capacity, StringBuilder text, uint flags, uint timeout, out UIntPtr copied);
        static string Key(AutomationElement element) => string.Join(".", element.GetRuntimeId());
        static bool Official(Process process)
        {
            if (!process.ProcessName.Equals("KakaoTalk", StringComparison.OrdinalIgnoreCase)) return false;
            var path = process.MainModule?.FileName;
            return new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles }.Any(folder =>
                string.Equals(path, Path.Combine(Environment.GetFolderPath(folder), "Kakao", "KakaoTalk", "KakaoTalk.exe"), StringComparison.OrdinalIgnoreCase));
        }
        static AutomationElement? ComposerOrNull(AutomationElement root)
        {
            var fields = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "1006"))
                .Cast<AutomationElement>().Where(e => e.Current.ControlType == ControlType.Document && !e.Current.IsPassword && e.Current.IsEnabled &&
                    e.TryGetCurrentPattern(ValuePattern.Pattern, out var p) && !((ValuePattern)p).Current.IsReadOnly).ToArray();
            return fields.Length == 1 ? fields[0] : null;
        }
        static string Value(AutomationElement field)
        {
            // This client stores its cue in the RichEdit buffer. Text alone is insufficient:
            // an actual same-text draft is black, while the verified cue is uniformly grey.
            var handle = new IntPtr(field.Current.NativeWindowHandle);
            if (handle == IntPtr.Zero || GetPid(handle) != field.Current.ProcessId) throw new AgentStoppedException("카카오톡 입력칸의 실제 내용을 확인하지 못했어요.");
            var buffer = new StringBuilder(12002); // up to 6,000 logical chars with CRLF expansion
            if (ReadText(handle, 0x000D, new UIntPtr(12002), buffer, 0x22, 1000, out var copied) == IntPtr.Zero || copied.ToUInt64() > 12000 || KakaoChats.Lines(buffer.ToString()).Length > 6000)
                throw new AgentStoppedException("카카오톡 초안을 읽지 못했거나 6,000자를 넘었어요.");
            object? color = field.TryGetCurrentPattern(TextPattern.Pattern, out var pattern)
                ? ((TextPattern)pattern).DocumentRange.GetAttributeValue(TextPattern.ForegroundColorAttribute) : null;
            using var process = Process.GetProcessById(field.Current.ProcessId);
            var version = process.MainModule?.FileVersionInfo.FileVersion ?? "";
            return KakaoChats.IsEmptyCue(buffer.ToString(), color, version) ? "" : buffer.ToString();
        }
        void Guard(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (context == null) throw new AgentStoppedException("카카오톡 대화방 목록을 먼저 확인하세요.");
            owner.EnsureForegroundContext(context);
        }
        AutomationElement Validate(Entry entry, CancellationToken token)
        {
            Guard(token);
            if (!Native.IsWindow(entry.Window) || GetPid(entry.Window) != entry.Pid || Native.Title(entry.Window) != entry.Chat.Title)
                throw new AgentStoppedException("카카오톡 대화방 창이 바뀌어 중단했어요.");
            using var process = Process.GetProcessById(entry.Pid);
            if (!Official(process) || process.StartTime.ToUniversalTime().Ticks != entry.Started) throw new AgentStoppedException("카카오톡 실행 상태가 바뀌었어요.");
            var root = AutomationElement.FromHandle(entry.Window); var field = ComposerOrNull(root);
            if (Key(root) != entry.Root || field == null || Key(field) != entry.Composer || field.Current.ProcessId != entry.Pid)
                throw new AgentStoppedException("카카오톡 입력칸이 바뀌어 중단했어요.");
            return field;
        }
        AutomationElement Focus(Entry entry, CancellationToken token)
        {
            _ = Validate(entry, token);
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                Guard(token); Native.ShowWindow(entry.Window, 9); Native.SetForegroundWindow(entry.Window);
            });
            Thread.Sleep(100); token.ThrowIfCancellationRequested();
            if (Native.GetForegroundWindow() != entry.Window) throw new AgentStoppedException("카카오톡 대화방을 선택하지 못했어요. 사용할 대화방을 직접 선택하세요.");
            owner.TrackForeground(); context = owner.CaptureForegroundContext();
            var field = Validate(entry, token); field.SetFocus();
            Thread.Sleep(80); GuardFocus(entry, token); return field;
        }
        void GuardFocus(Entry entry, CancellationToken token)
        {
            var field = Validate(entry, token);
            if (Native.GetForegroundWindow() != entry.Window || !Automation.Compare(field, AutomationElement.FocusedElement))
                throw new AgentStoppedException("메시지 입력 위치가 바뀌어 중단했어요.");
        }
        async Task<T> Run<T>(Func<CancellationToken, T> work, CancellationToken token)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(TimeSpan.FromSeconds(12));
            var cancellation = limit.Token;
            try
            {
                return await Task.Run(async () => { await gate.WaitAsync(cancellation); try { return work(cancellation); } finally { gate.Release(); } }, cancellation).WaitAsync(cancellation);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { throw new AgentStoppedException("카카오톡 응답이 늦어 중단했어요. 현재 초안과 전송 결과를 직접 확인하세요. 자동 재전송하지 않습니다."); }
            catch (Exception ex) when (ex is ElementNotAvailableException or System.ComponentModel.Win32Exception or ArgumentException)
            { throw new AgentStoppedException("카카오톡 대화방을 확인하지 못했어요. 창과 입력칸을 확인한 뒤 다시 요청하세요."); }
        }
        public Task<IReadOnlyList<KakaoChat>> ReadAsync(CancellationToken token) => Run<IReadOnlyList<KakaoChat>>(cancellation =>
        {
            entries.Clear(); ready = null; context = owner.CaptureForegroundContext();
            foreach (var handle in OwnedWindow.VisibleWindows())
            {
                Guard(cancellation);
                try
                {
                    using var process = Process.GetProcessById(GetPid(handle));
                    if (!Official(process)) continue;
                    var title = Native.Title(handle);
                    if (string.IsNullOrWhiteSpace(title) || title is "카카오톡" or "KakaoTalk") continue;
                    var root = AutomationElement.FromHandle(handle); var field = ComposerOrNull(root);
                    if (field == null) continue;
                    var key = Guid.NewGuid().ToString("N"); var chat = new KakaoChat(key, title);
                    entries.Add(key, new(chat, handle, process.Id, process.StartTime.ToUniversalTime().Ticks, Key(root), Key(field)));
                    if (entries.Count > 240) break;
                }
                catch (Exception ex) when (ex is ElementNotAvailableException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
            Guard(cancellation); return entries.Values.Select(x => x.Chat).ToArray();
        }, token);
        Entry Find(KakaoChat chat) => entries.TryGetValue(chat.Key, out var entry) && entry.Chat == chat ? entry : throw new AgentStoppedException("대화방 목록을 다시 확인하세요.");
        public Task<KakaoDraft> PrepareAsync(KakaoChat chat, CancellationToken token) => Run(cancellation =>
        {
            ready = null; var entry = Find(chat); var field = Focus(entry, cancellation);
            // The native reader distinguishes only the verified cue; actual same-text drafts remain.
            var draft = new KakaoDraft(chat, Key(field), Value(field)); ready = draft; return draft;
        }, token);
        public Task<KakaoResult> ApplyAsync(KakaoDraft draft, string text, bool send, bool existing, CancellationToken token) => Run(cancellation =>
        {
            var prepared = ready; ready = null;
            if (prepared != draft) throw new AgentStoppedException("확인한 카카오톡 초안과 달라 중단했어요.");
            var entry = Find(draft.Chat); var field = Validate(entry, cancellation);
            if (Key(field) != draft.Composer || Value(field) != draft.Text) throw new AgentStoppedException("확인 중 초안이 바뀌어 입력하거나 보내지 않았어요.");
            field = Focus(entry, cancellation);
            if (Value(field) != draft.Text) throw new AgentStoppedException("기존 초안이 바뀌어 중단했어요.");
            if (existing && text != draft.Text || !existing && draft.Text.Length != 0) throw new AgentStoppedException("기존 초안을 덮어쓰거나 다른 내용을 보내지 않았어요.");
            if (!existing)
            {
                // WM_SETTEXT/ValuePattern can bypass Kakao's draft/send-button state.
                // Real Unicode input triggers its editor, and Shift+Enter inserts line breaks.
                var normalized = KakaoChats.Lines(text); var written = "";
                void CheckPrefix()
                {
                    GuardFocus(entry, cancellation);
                    if (KakaoChats.Lines(Value(field)) != written) throw new AgentStoppedException("입력 중 초안이 바뀌어 멈췄어요. 남은 글을 자동으로 덧붙이거나 보내지 않습니다.");
                    if (new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(Native.KeyDown)) throw new AgentStoppedException("다른 키가 눌려 있어 초안 입력을 멈췄어요.");
                }
                var lines = normalized.Split('\n');
                for (var line = 0; line < lines.Length; line++)
                {
                    if (line > 0)
                    {
                        CheckPrefix(); KeyboardDispatch.Send(KeyboardChord.Parse("shift+enter"), Native.SendKeyStrokes, Native.KeyDown, () => GuardFocus(entry, cancellation), cancellation);
                        written += "\n"; Thread.Sleep(50);
                    }
                    for (var offset = 0; offset < lines[line].Length;)
                    {
                        CheckPrefix(); var length = Math.Min(64, lines[line].Length - offset);
                        if (offset + length < lines[line].Length && char.IsHighSurrogate(lines[line][offset + length - 1])) length--;
                        var chunk = lines[line].Substring(offset, length); offset += length;
                        Native.Type(chunk); written += chunk; Thread.Sleep(50);
                    }
                }
                CheckPrefix();
            }
            void Exact() { GuardFocus(entry, cancellation); if (KakaoChats.Lines(Value(field)) != KakaoChats.Lines(text)) throw new AgentStoppedException("초안이 확인한 내용과 달라 전송하지 않았어요. 입력칸을 확인하세요."); }
            Exact();
            if (!send) return new KakaoResult(draft.Chat.Title, false, false);
            KeyboardDispatch.Send(KeyboardChord.Parse("enter"), Native.SendKeyStrokes, Native.KeyDown, Exact, cancellation);
            for (var attempt = 0; attempt < 20; attempt++)
            {
                Thread.Sleep(75); _ = Validate(entry, cancellation);
                if (Value(field).Length == 0) return new KakaoResult(draft.Chat.Title, true, true);
            }
            return new KakaoResult(draft.Chat.Title, true, false);
        }, token);
    }
}
