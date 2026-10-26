using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace VoiceAssistant.Desktop;

public static partial class Native
{
    [DllImport("user32.dll")] internal static extern IntPtr GetKeyboardLayout(uint thread);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern short VkKeyScanEx(char value, IntPtr layout);
    internal static uint SendKeyStrokes(IReadOnlyList<KeyStroke> strokes)
    {
        var events = strokes.Select(x => Key(x.Key, (x.Up ? 2u : 0u) | (x.Extended ? 1u : 0u))).ToArray();
        return SendInput((uint)events.Length, events, Marshal.SizeOf<INPUT>());
    }
}

public sealed partial class WindowsActions
{
    public sealed record KeyFocus(AutomationElement Element, string Name, string Id, int Role);
    static KeyFocus ReadKeyFocus()
    {
        var element = AutomationElement.FocusedElement ?? throw new AgentStoppedException("키를 받을 항목을 확인하지 못했어요.");
        var current = element.Current; if (current.IsPassword) throw new AgentStoppedException("암호 입력란에는 키를 전달하지 않아요.");
        return new(element, current.Name, current.AutomationId, current.ControlType.Id);
    }
    static void EnsureKeyFocus(KeyFocus expected)
    {
        var current = ReadKeyFocus();
        if (!Automation.Compare(expected.Element, current.Element) || expected.Name != current.Name || expected.Id != current.Id || expected.Role != current.Role) throw new AgentStoppedException("확인 중 키를 받을 항목이 바뀌어 입력하지 않았어요.");
    }
    public async Task<KeyFocus> CaptureKeyFocusAsync(InputTarget expected, CancellationToken token)
    { RequireInputTarget(expected); await Focus(token); RequireInputTarget(expected); return ReadKeyFocus(); }
    public async Task KeyAsync(string value, CancellationToken token = default, InputTarget? expected = null, IEnumerable<HotkeySpec?>? reserved = null, KeyFocus? focus = null)
    {
        var chord = KeyboardChord.Parse(value);
        if (expected != null) RequireInputTarget(expected);
        var selected = await Focus(token); token.ThrowIfCancellationRequested();
        var thread = Native.GetWindowThreadProcessId(selected, out _); var layout = Native.GetKeyboardLayout(thread);
        chord = chord.Resolve(character => Native.VkKeyScanEx(character, layout));
        if (reserved?.Any(chord.Matches) == true) throw new AgentStoppedException("비서의 말하기·중지 단축키와 같아 대상 앱에 보내지 않았어요.");
        void GuardKey()
        {
            token.ThrowIfCancellationRequested(); if (expected != null) RequireInputTarget(expected);
            if (Native.GetForegroundWindow() != selected) throw new AgentStoppedException("입력 창이 바뀌어 키를 누르지 않았어요.");
            if (focus != null) EnsureKeyFocus(focus); else _ = ReadKeyFocus();
            if (Native.GetKeyboardLayout(thread) != layout) throw new AgentStoppedException("키보드 배열이 바뀌어 입력하지 않았어요.");
        }
        KeyboardDispatch.Send(chord, Native.SendKeyStrokes, Native.KeyDown, GuardKey, token);
        await Task.Delay(100, token);
    }
}
