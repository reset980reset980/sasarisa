using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace VoiceAssistant.Desktop;

public sealed record KeyboardChord(string Label, ushort Key, uint Modifiers, bool Extended, char? Symbol = null)
{
    public static KeyboardChord Parse(string value)
    {
        if (value == null || value.Length is < 1 or > 80 || value.Any(char.IsControl)) throw new ArgumentException("키 조합은 한 번에 하나씩 지정하세요.");
        var parts = value.Split('+', StringSplitOptions.TrimEntries); uint modifiers = 0;
        if (parts.Length > 5 || parts.Any(string.IsNullOrEmpty)) throw new ArgumentException("키 조합을 확인하세요. + 키는 plus라고 지정하세요.");
        foreach (var part in parts[..^1])
        {
            var flag = part.ToLowerInvariant() switch { "ctrl" or "control" or "cmd" or "command" => 2u, "alt" or "opt" or "option" => 1u, "shift" => 4u, "win" or "windows" or "super" => 8u, _ => 0u };
            if (flag == 0 || (modifiers & flag) != 0) throw new ArgumentException("알 수 없거나 중복된 보조 키예요.");
            modifiers |= flag;
        }
        var name = parts[^1].ToLowerInvariant() switch
        {
            "return" => "enter", "esc" => "escape", "back" => "backspace", "del" or "forwarddelete" => "delete",
            "pgup" => "pageup", "pgdn" => "pagedown", "ins" => "insert", "kp_enter" or "numpadenter" => "numpadenter",
            "plus" => "plus", "comma" => ",", "period" => ".", "slash" => "/", "backslash" => "\\", "minus" => "-", "equal" => "=",
            var other => other
        };
        ushort key = 0; char? symbol = null;
        if (name.Length == 1 && name[0] is >= 'a' and <= 'z') key = char.ToUpperInvariant(name[0]);
        else if (name.Length == 1 && name[0] is >= '0' and <= '9') key = name[0];
        else if (name.StartsWith('f') && int.TryParse(name[1..], out var f) && f is >= 1 and <= 24) key = (ushort)(0x70 + f - 1);
        else if (name == "plus") symbol = '+';
        else if (name.Length == 1 && ",./[]\\=-;'`".Contains(name[0])) symbol = name[0];
        else key = name switch { "enter" or "numpadenter" => 13, "escape" => 27, "tab" => 9, "space" => 32, "backspace" => 8, "delete" => 0x2E, "insert" => 0x2D, "left" => 0x25, "up" => 0x26, "right" => 0x27, "down" => 0x28, "home" => 0x24, "end" => 0x23, "pageup" => 0x21, "pagedown" => 0x22, _ => 0 };
        if (key == 0 && symbol == null) throw new ArgumentException("지원하지 않는 키예요. 글 입력은 type을 사용하세요.");
        if (key == 0x2E && (modifiers & 3) == 3) throw new ArgumentException("Ctrl+Alt+Delete 보안 화면 키는 자동으로 누를 수 없어요.");
        if (key == 0x52 && (modifiers & 8) != 0) throw new ArgumentException("실행 대화상자 단축키는 지원하지 않아요. 앱은 등록된 이름으로 열어 주세요.");
        var labels = new List<string>(); if ((modifiers & 2) != 0) labels.Add("ctrl"); if ((modifiers & 1) != 0) labels.Add("alt"); if ((modifiers & 4) != 0) labels.Add("shift"); if ((modifiers & 8) != 0) labels.Add("win"); labels.Add(name);
        return new(string.Join('+', labels), key, modifiers, key is >= 0x21 and <= 0x28 or 0x2D or 0x2E || name == "numpadenter", symbol);
    }
    public KeyboardChord Resolve(Func<char, short> lookup)
    {
        if (Symbol == null) return this;
        var mapping = lookup(Symbol.Value); var state = (mapping >> 8) & 255; var key = (ushort)(mapping & 255);
        if (mapping == -1 || (state & ~7) != 0 || key is 0 or 255) throw new InvalidOperationException("대상 키보드 배열에서 해당 기호 키를 찾지 못했어요.");
        var extra = ((state & 1) != 0 ? 4u : 0u) | ((state & 2) != 0 ? 2u : 0u) | ((state & 4) != 0 ? 1u : 0u);
        return this with { Key = key, Modifiers = Modifiers | extra, Symbol = null };
    }
    public bool Matches(HotkeySpec? spec) => spec != null && Symbol == null && Key == spec.Key && Modifiers == spec.Modifiers;
    public bool NeedsConfirmation => (Modifiers & 9) != 0 || Symbol != null || !(
        Key is >= 0x21 and <= 0x28 || Key == 9 || Key == 27 && Modifiers == 0 ||
        Modifiers == 2 && Key is 0x41 or 0x43 or 0x56 or 0x5A);
    public bool MayNavigateBrowser => Key is 13 or 0x74 || (Modifiers & 1) != 0 && Key is 0x25 or 0x27 or 0x24 ||
        (Modifiers & 2) != 0 && Key is 9 or 0x21 or 0x22 or 0x54 or 0x57 or 0x4E or 0x52;
}

internal readonly record struct KeyStroke(ushort Key, bool Up, bool Extended);
internal static class KeyboardDispatch
{
    internal static KeyStroke[] Sequence(KeyboardChord chord)
    {
        if (chord.Symbol != null || chord.Key == 0) throw new ArgumentException("키보드 배열을 먼저 확인하세요.");
        var keys = new List<KeyStroke>();
        foreach (var (flag, key) in new (uint, ushort)[] { (2, 0x11), (1, 0x12), (4, 0x10), (8, 0x5B) }) if ((chord.Modifiers & flag) != 0) keys.Add(new(key, false, key == 0x5B));
        var modifiers = keys.ToArray(); keys.Add(new(chord.Key, false, chord.Extended)); keys.Add(new(chord.Key, true, chord.Extended));
        keys.AddRange(modifiers.Reverse().Select(x => x with { Up = true })); return keys.ToArray();
    }
    internal static void Send(KeyboardChord chord, Func<IReadOnlyList<KeyStroke>, uint> send, Func<int, bool> held, Action guard, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); guard();
        if (new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C, chord.Key }.Distinct().Any(held)) throw new AgentStoppedException("키가 이미 눌려 있어 조합이 섞이지 않도록 멈췄어요. 키를 놓고 다시 요청하세요.");
        var events = Sequence(chord); token.ThrowIfCancellationRequested(); guard(); var sent = send(events);
        if (sent == events.Length) return;
        var pressed = new List<KeyStroke>();
        foreach (var input in events.Take((int)Math.Min(sent, (uint)events.Length)))
            if (!input.Up) pressed.Add(input); else pressed.RemoveAll(x => x.Key == input.Key);
        if (pressed.Count > 0)
        {
            var release = pressed.AsEnumerable().Reverse().Select(x => x with { Up = true }).ToArray();
            if (send(release) != release.Length) throw new AgentStoppedException("키 입력이 일부만 전달됐고 눌림 해제를 확인하지 못했어요. 키를 직접 놓고 대상 앱을 확인하세요.");
        }
        if (sent == 0) throw new InvalidOperationException("대상 창에 키를 전달하지 못했어요. 관리자 권한 창인지 확인하세요.");
        throw new AgentStoppedException("키 입력이 일부만 전달되어 중단하고 눌린 키를 해제했어요. 대상 앱의 결과를 확인하세요.");
    }
}
