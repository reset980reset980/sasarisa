using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceAssistant.Desktop;

public sealed record HotkeySpec(uint Modifiers, uint Key, string Label)
{
    public static HotkeySpec? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Trim() is "none" or "사용 안 함") return null;
        var parts = text.Split('+', StringSplitOptions.TrimEntries); uint modifiers = 0;
        foreach (var part in parts[..^1])
        {
            var flag = part.ToLowerInvariant() switch { "ctrl" or "control" => 2u, "alt" => 1u, "shift" => 4u, "win" or "windows" => 8u, _ => 0u };
            if (flag == 0 || (modifiers & flag) != 0) throw new ArgumentException("단축키 조합을 확인하세요. 예: Ctrl+Alt+F8");
            modifiers |= flag;
        }
        string last = parts[^1].ToUpperInvariant(); uint key;
        if (last.Length == 1 && (last[0] is >= 'A' and <= 'Z' or >= '0' and <= '9')) key = last[0];
        else if (last.StartsWith('F') && int.TryParse(last[1..], out var number) && number is >= 1 and <= 24) key = (uint)(0x70 + number - 1);
        else key = last switch { "SPACE" => 0x20, "BACK" or "BACKSPACE" => 8, "ENTER" or "RETURN" => 13, "ESC" or "ESCAPE" => 27, "TAB" => 9, "HOME" => 0x24, "END" => 0x23, "PAGEUP" => 0x21, "PAGEDOWN" => 0x22, "LEFT" => 0x25, "UP" => 0x26, "RIGHT" => 0x27, "DOWN" => 0x28, "INSERT" => 0x2D, "DELETE" => 0x2E, _ => 0 };
        if (key == 0 || key == 0x7B) throw new ArgumentException("글자·숫자·기능키·Space 등을 사용하세요. F12는 Windows 예약 키입니다.");
        if ((modifiers & 11) == 0 && key is not (>= 0x70 and <= 0x87)) throw new ArgumentException("일반 키에는 Ctrl, Alt 또는 Win을 함께 사용하세요.");
        var names = new List<string>(); if ((modifiers & 2) != 0) names.Add("Ctrl"); if ((modifiers & 1) != 0) names.Add("Alt"); if ((modifiers & 4) != 0) names.Add("Shift"); if ((modifiers & 8) != 0) names.Add("Win");
        var keyName = key switch { 8 => "Backspace", 9 => "Tab", 13 => "Enter", 27 => "Escape", 32 => "Space", 0x21 => "PageUp", 0x22 => "PageDown", 0x23 => "End", 0x24 => "Home", 0x25 => "Left", 0x26 => "Up", 0x27 => "Right", 0x28 => "Down", 0x2D => "Insert", 0x2E => "Delete", _ => key is >= 0x70 and <= 0x87 ? "F" + (key - 0x70 + 1) : ((char)key).ToString() };
        names.Add(keyName); return new(modifiers, key, string.Join('+', names));
    }
    public bool IsPressed(Func<int, bool>? down = null)
    {
        down ??= Native.KeyDown;
        return down((int)Key) && ((Modifiers & 2) == 0 || down(0x11)) && ((Modifiers & 1) == 0 || down(0x12)) &&
            ((Modifiers & 4) == 0 || down(0x10)) && ((Modifiers & 8) == 0 || down(0x5B) || down(0x5C));
    }
}

public interface IHotkeyBackend
{
    bool Register(int id, HotkeySpec spec);
    void Unregister(int id);
}
public sealed class HotkeyManager : IDisposable
{
    sealed class WindowsBackend(IntPtr handle) : IHotkeyBackend
    {
        public bool Register(int id, HotkeySpec spec) => Native.RegisterHotKey(handle, id, spec.Modifiers | 0x4000, spec.Key);
        public void Unregister(int id) => Native.UnregisterHotKey(handle, id);
    }
    readonly IHotkeyBackend backend;
    public HotkeySpec? Wake { get; private set; }
    public HotkeySpec? Stop { get; private set; }
    public HotkeyManager(IntPtr handle, IHotkeyBackend? backend = null) => this.backend = backend ?? new WindowsBackend(handle);
    static bool Same(HotkeySpec? a, HotkeySpec? b) => a?.Modifiers == b?.Modifiers && a?.Key == b?.Key;
    public static void ValidatePair(string wakeText, string stopText, bool hold)
    {
        var wake = HotkeySpec.Parse(wakeText); var stop = HotkeySpec.Parse(stopText);
        if (wake != null && Same(wake, stop)) throw new ArgumentException("말하기와 중지 단축키는 서로 다르게 지정하세요.");
        if (hold && wake == null) throw new ArgumentException("누르는 동안 말하기에는 말하기 단축키가 필요해요.");
    }
    public IReadOnlyList<string> Initialize(string wakeText, string stopText)
    {
        var notices = new List<string>();
        var wake = HotkeySpec.Parse(wakeText); var stop = HotkeySpec.Parse(stopText);
        foreach (var (id, requested, fallback) in new[] { (1, wake, "Ctrl+Alt+F9"), (2, stop, "Ctrl+Alt+F10") })
        {
            if (requested == null) continue;
            HotkeySpec? active = null;
            foreach (var candidate in new[] { requested, HotkeySpec.Parse(fallback)! }.Distinct())
                if (backend.Register(id, candidate)) { active = candidate; break; }
            if (id == 1) Wake = active; else Stop = active;
            if (!Same(requested, active)) notices.Add($"{(id == 1 ? "말하기" : "중지")} 단축키 {requested.Label}를 사용할 수 없어 " + (active == null ? "화면 버튼을 사용하세요." : active.Label + "를 이번 실행에서 사용합니다."));
        }
        return notices;
    }
    public void Configure(string wakeText, string stopText, Action persist)
    {
        ValidatePair(wakeText, stopText, false);
        var nextWake = HotkeySpec.Parse(wakeText); var nextStop = HotkeySpec.Parse(stopText);
        if (Same(Wake, nextWake) && Same(Stop, nextStop)) { persist(); return; }
        var previousWake = Wake; var previousStop = Stop;
        Release();
        try
        {
            if (nextWake != null && !backend.Register(1, nextWake)) throw new InvalidOperationException(nextWake.Label + " 단축키를 사용할 수 없어요. 다른 조합을 선택하세요.");
            Wake = nextWake;
            if (nextStop != null && !backend.Register(2, nextStop)) throw new InvalidOperationException(nextStop.Label + " 단축키를 사용할 수 없어요. 다른 조합을 선택하세요.");
            Stop = nextStop; persist();
        }
        catch (Exception error)
        {
            Release();
            if (previousWake != null && backend.Register(1, previousWake)) Wake = previousWake;
            if (previousStop != null && backend.Register(2, previousStop)) Stop = previousStop;
            var restored = Same(previousWake, Wake) && Same(previousStop, Stop);
            throw new InvalidOperationException(error.Message + (restored ? " 기존 단축키를 유지했어요." : " 기존 단축키도 복원되지 않았어요. 화면 버튼을 사용하고 설정을 다시 저장하세요."), error);
        }
    }
    void Release() { backend.Unregister(1); backend.Unregister(2); Wake = Stop = null; }
    public void Dispose() => Release();
}
