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

public static partial class Native
{
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint id);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr h, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    public static bool KeyDown(int key)=>(GetAsyncKeyState(key)&0x8000)!=0;
    [DllImport("user32.dll", SetLastError=true)] static extern IntPtr SendMessageTimeout(IntPtr h,uint message,IntPtr w,IntPtr l,uint flags,uint timeout,out IntPtr result);
    public static void AppCommand(int command)
    {
        var h=GetForegroundWindow();
        if(h==IntPtr.Zero||SendMessageTimeout(h,0x0319,h,new IntPtr(command<<16),2,1000,out _)==IntPtr.Zero)
            throw new InvalidOperationException("미디어 명령을 전달하지 못했어요.");
    }
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, INPUT[] input, int size);
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public INPUTUNION data; }
    [StructLayout(LayoutKind.Explicit)] struct INPUTUNION { [FieldOffset(0)] public KEYBDINPUT key; [FieldOffset(0)] public MOUSEINPUT mouse; }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort vk, scan; public uint flags, time; public UIntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int x, y; public uint mouseData, flags, time; public UIntPtr extra; }
    static INPUT Key(ushort vk, uint flags = 0, ushort scan = 0) => new() { type = 1, data = new INPUTUNION { key = new KEYBDINPUT { vk = vk, scan = scan, flags = flags } } };
    static void Send(params INPUT[] keys) { if (SendInput((uint)keys.Length, keys, Marshal.SizeOf<INPUT>()) != keys.Length) throw new InvalidOperationException("대상 창에 입력하지 못했어요. 관리자 권한 창인지 확인하세요."); }
    public static void Press(ushort key, params ushort[] modifiers)
    {
        var list = new List<INPUT>(); foreach (var m in modifiers) list.Add(Key(m)); list.Add(Key(key)); list.Add(Key(key, 2)); foreach (var m in modifiers.Reverse()) list.Add(Key(m, 2)); Send(list.ToArray());
    }
    public static void Type(string text) { foreach (var c in text) { if (c == '\r') continue; if (c == '\n') { Press(0x0D); continue; } Send(Key(0, 4, c), Key(0, 6, c)); } }
    internal static void LeftClick()
    {
        var down = new INPUT { type = 0, data = new INPUTUNION { mouse = new MOUSEINPUT { flags = 2 } } };
        var up = new INPUT { type = 0, data = new INPUTUNION { mouse = new MOUSEINPUT { flags = 4 } } };
        var sent = SendInput(2, [down, up], Marshal.SizeOf<INPUT>());
        if (sent != 2) { if (sent == 1) SendInput(1, [up], Marshal.SizeOf<INPUT>()); throw new InvalidOperationException("대상 창에 마우스 누르기를 전달하지 못했어요."); }
    }
    public static string Title(IntPtr h) { var b = new StringBuilder(512); GetWindowText(h, b, b.Capacity); return b.ToString(); }
    public static string ProcessName(IntPtr h) { GetWindowThreadProcessId(h, out var id); try { return Process.GetProcessById((int)id).ProcessName; } catch { return ""; } }
}

public sealed partial class WindowsActions
{
    IntPtr target;
    long targetVersion;
    public sealed record InputTarget(IntPtr Window, int ProcessId, long ProcessStart, long Version);
    static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase) { "KakaoTalk", "cmd", "powershell", "pwsh", "WindowsTerminal", "conhost", "OpenConsole", "Code", "Cursor", "Claude", "ChatGPT", "1Password", "KeePass", "KeePassXC", "Bitwarden", "CredentialUIBroker", "consent", "SystemSettings", "SecHealthUI", "regedit", "mmc" };
    public string TargetLabel => target != IntPtr.Zero ? Native.Title(target) : "선택된 창 없음";
    public void TrackForeground()
    {
        var h = Native.GetForegroundWindow(); if (h == IntPtr.Zero) return; Native.GetWindowThreadProcessId(h, out var pid);
        if (pid != Environment.ProcessId && Native.IsWindow(h) && target != h)
        {
            target = h; targetVersion++;
            if (Environment.GetEnvironmentVariable("SASARISA_TRACE_INPUT") == "1") AppPaths.Log("input-target", $"process={Native.ProcessName(h)} handle={h.ToInt64():X} version={targetVersion}");
        }
    }
    public static bool IsProtectedProcess(string name) => Protected.Contains(name);
    public InputTarget CaptureInputTarget()
    {
        ValidateTarget(); var pid = GetPid(target); using var process = Process.GetProcessById(pid);
        return new(target, pid, process.StartTime.ToUniversalTime().Ticks, targetVersion);
    }
    void RequireInputTarget(InputTarget expected)
    {
        TrackForeground();
        ValidateTarget();
        if (target != expected.Window || targetVersion != expected.Version || GetPid(target) != expected.ProcessId) throw new AgentStoppedException("요청을 처리하는 동안 대상 창이 바뀌어 멈췄어요. 작업할 창을 선택하고 다시 요청하세요.");
        using var process = Process.GetProcessById(expected.ProcessId);
        if (process.StartTime.ToUniversalTime().Ticks != expected.ProcessStart) throw new InvalidOperationException("원래 입력할 앱이 종료되어 입력하지 않았어요.");
    }
    public void EnsureInputTarget(InputTarget expected) => RequireInputTarget(expected);
    public void ValidateTarget()
    {
        if (target == IntPtr.Zero || !Native.IsWindow(target)) throw new InvalidOperationException("입력할 창을 먼저 선택하세요.");
        var name = Native.ProcessName(target);
        if (name.Equals("KakaoTalk", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("카카오톡은 대화방 목록과 전용 초안·전송 기능을 사용하세요.");
        if (string.IsNullOrEmpty(name) || Protected.Contains(name)) throw new InvalidOperationException("터미널·개발 도구·암호·시스템 설정 창에는 자동으로 입력하지 않아요.");
    }
    async Task<IntPtr> Focus(CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); ValidateTarget(); var selected = target; Native.ShowWindow(selected, 9); Native.SetForegroundWindow(selected); await Task.Delay(120, token);
        if (Native.GetForegroundWindow() != selected) throw new InvalidOperationException("대상 창으로 전환하지 못했어요. 입력할 창을 직접 선택하세요.");
        var element = AutomationElement.FocusedElement;
        if (element == null || element.Current.IsPassword) throw new InvalidOperationException("암호 입력란에는 입력하지 않아요.");
        // UIA provider failures fail closed rather than typing into an unknown field.
        if (element.Current.ProcessId != GetPid(selected)) throw new InvalidOperationException("입력 위치가 바뀌었어요. 대상 창을 다시 선택하세요.");
        return selected;
    }
    static int GetPid(IntPtr h) { Native.GetWindowThreadProcessId(h, out var id); return (int)id; }
    public async Task<TextSelection> ReadSelectionAsync(CancellationToken token)
    {
        var selected = await Focus(token); var element = AutomationElement.FocusedElement;
        for (var depth = 0; element != null && depth < 5 && element.Current.ProcessId == GetPid(selected); depth++, element = TreeWalker.ControlViewWalker.GetParent(element))
        {
            if (element.Current.IsPassword) break;
            if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var pattern)) continue;
            var ranges = ((TextPattern)pattern).GetSelection(); var text = string.Join("", ranges.Select(x => x.GetText(6001)));
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (text.Length > 6000) throw new InvalidOperationException("한 번에 6,000자 이내로 선택하세요.");
            return new(selected, element, text);
        }
        throw new InvalidOperationException("선택한 글을 읽지 못했어요. 문서에서 글을 드래그해 선택한 뒤 다시 말하세요. 이 앱이 선택 영역 읽기를 지원하지 않을 수도 있어요.");
    }
    public async Task ReplaceSelectionAsync(TextSelection selection, string revised, CancellationToken token)
    {
        target = selection.Window; await Focus(token); token.ThrowIfCancellationRequested();
        if (!selection.Element.TryGetCurrentPattern(TextPattern.Pattern, out var pattern)) throw new InvalidOperationException("원래 선택 영역이 사라졌어요.");
        var current = string.Join("", ((TextPattern)pattern).GetSelection().Select(x => x.GetText(6001)));
        if (current != selection.Text) throw new InvalidOperationException("선택한 글이 바뀌어 적용하지 않았어요. 다시 선택해 주세요.");
        if (selection.Element.Current.ControlType == ControlType.Edit && !Automation.Compare(AutomationElement.FocusedElement, selection.Element)) throw new AgentStoppedException("선택한 입력칸의 포커스가 바뀌어 적용하지 않았어요.");
        await TypeAsync(revised, token);
    }
    public async Task TypeAsync(string text, CancellationToken token = default, InputTarget? expected = null)
    {
        if (expected != null) RequireInputTarget(expected);
        var selected = await Focus(token);
        if (expected != null) RequireInputTarget(expected);
        var bound = expected ?? CaptureInputTarget(); var element = AutomationElement.FocusedElement;
        var before = CaptureEdit(element);
        var normalized = FieldInsertion.Normalize(text);
        var tracked = before != null && before.State.Text.Length - before.State.Length + normalized.Length <= 6000;
        FieldEditState Read()
        {
            token.ThrowIfCancellationRequested(); RequireInputTarget(bound);
            if (Native.GetForegroundWindow() != selected || !Automation.Compare(AutomationElement.FocusedElement, element)) throw new AgentStoppedException("입력 위치가 바뀌어 멈췄어요.");
            return CaptureEdit(element)?.State ?? throw new AgentStoppedException("입력칸을 더 이상 확인할 수 없어요.");
        }
        await TrackEditAsync("문서에 글 입력", bound, element, tracked ? before!.State.Apply(normalized).Text : "", async () =>
        {
            if (tracked)
            {
                await FieldInsertion.RunAsync(before!.State, normalized, Read, part =>
                {
                    if (new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(Native.KeyDown)) throw new AgentStoppedException("보조 키가 눌려 있어 글 입력을 멈췄어요.");
                    Native.Type(part);
                }, token);
            }
            else for (var offset = 0; offset < text.Length;)
            {
                token.ThrowIfCancellationRequested(); RequireInputTarget(bound);
                var focused = AutomationElement.FocusedElement;
                if (Native.GetForegroundWindow() != selected || focused == null || focused.Current.IsPassword || !Automation.Compare(focused, element)) throw new AgentStoppedException("입력 위치가 바뀌어 멈췄어요.");
                var count = Math.Min(64, text.Length - offset);
                if (offset + count < text.Length && char.IsHighSurrogate(text[offset + count - 1]) && char.IsLowSurrogate(text[offset + count])) count--;
                Native.Type(text.Substring(offset, count)); offset += count; await Task.Yield();
            }
        }, token);
    }
    public async Task BrowserAsync(string operation, CancellationToken token = default, InputTarget? expected = null)
    {
        if (expected != null) RequireInputTarget(expected);
        ValidateTarget(); if (Native.ProcessName(target).ToLowerInvariant() is not ("chrome" or "msedge" or "firefox" or "brave")) throw new InvalidOperationException("조작할 브라우저 창을 먼저 선택하세요.");
        await Focus(token); token.ThrowIfCancellationRequested(); if (expected != null) RequireInputTarget(expected); switch (operation) { case "back": Native.Press(0x25, 0x12); break; case "forward": Native.Press(0x27, 0x12); break; case "refresh": Native.Press(0x74); break; }
    }
    public static void Media(string action)
    {
        if(action=="play")Native.AppCommand(46);
        else if(action=="pause")Native.AppCommand(47);
        else Native.Press(action switch { "mute" => 0xAD, "next" => 0xB0, _ => (ushort)0xB3 });
    }
    public async Task TapAsync(string label, CancellationToken token = default, InputTarget? expected = null)
    {
        if (expected != null) RequireInputTarget(expected);
        var selected = await Focus(token); var root = AutomationElement.FromHandle(selected);
        if (expected != null) RequireInputTarget(expected);
        var matches = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.IsEnabledProperty, true));
        var hits = new List<AutomationElement>();
        foreach (AutomationElement el in matches)
        {
            if (el.Current.IsPassword || el.Current.IsOffscreen) continue;
            var type = el.Current.ControlType;
            if (type != ControlType.Button && type != ControlType.Hyperlink && type != ControlType.MenuItem) continue;
            if (string.Equals(el.Current.Name, label, StringComparison.OrdinalIgnoreCase)) hits.Add(el);
        }
        if (hits.Count != 1) throw new InvalidOperationException($"‘{label}’ 버튼을 하나로 특정할 수 없어요 ({hits.Count}개). 정확한 버튼 이름을 말하세요.");
        if (!hits[0].TryGetCurrentPattern(InvokePattern.Pattern, out var pattern)) throw new InvalidOperationException("이 버튼은 자동 클릭을 지원하지 않아요.");
        token.ThrowIfCancellationRequested(); if (Native.GetForegroundWindow() != selected) throw new InvalidOperationException("대상 창이 바뀌어 멈췄어요.");
        if (expected != null) RequireInputTarget(expected);
        ((InvokePattern)pattern).Invoke();
    }
    public string ScreenContext(InputTarget? expected = null)
    {
        if (expected != null) RequireInputTarget(expected);
        ValidateTarget(); var lines = new List<string> { "앱: " + Native.ProcessName(target), "창: " + Native.Title(target) };
        var root = AutomationElement.FromHandle(target); var count = 0; var clock = Stopwatch.StartNew();
        void Walk(AutomationElement el, int depth)
        {
            if (depth > 12 || count > 90 || clock.ElapsedMilliseconds > 1200) return;
            try
            {
                if (el.Current.IsPassword || el.Current.IsOffscreen) return;
                var name = el.Current.Name;
                if (!string.IsNullOrWhiteSpace(name)) { lines.Add(el.Current.ControlType.ProgrammaticName + ": " + name[..Math.Min(name.Length, 160)]); count++; }
                if (el.Current.ControlType is var type && (type == ControlType.Document || type == ControlType.Edit))
                {
                    string? content = null;
                    if (el.TryGetCurrentPattern(TextPattern.Pattern, out var textPattern)) content = ((TextPattern)textPattern).DocumentRange.GetText(2001);
                    else if (el.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePattern)) content = ((ValuePattern)valuePattern).Current.Value;
                    if (!string.IsNullOrWhiteSpace(content)) { lines.Add("문서 내용: " + content[..Math.Min(content.Length, 2000)] + (content.Length > 2000 ? " (일부만 표시)" : "")); count++; }
                }
                var child = TreeWalker.ControlViewWalker.GetFirstChild(el);
                while (child != null && count <= 90 && clock.ElapsedMilliseconds < 1200) { Walk(child, depth + 1); child = TreeWalker.ControlViewWalker.GetNextSibling(child); }
            }
            catch (ElementNotAvailableException) { }
        }
        Walk(root, 0); if (expected != null) RequireInputTarget(expected); var value = string.Join("\n", lines); return value[..Math.Min(value.Length, 6000)];
    }
    public static void OpenUrl(string url) { ActionPolicy.Validate(new("open_url", url)); Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
    public static Process? OpenApp(string app)
    {
        ActionPolicy.Validate(new("open_app", app));
        if (AppRegistry.Default.Find(app) is { } custom)
        {
            AppRegistry.ValidatePath(custom.Path);
            return Process.Start(new ProcessStartInfo(custom.Path) { UseShellExecute = true });
        }
        var program = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles); var x86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86); var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = app switch
        {
            "notepad" => new[] { Path.Combine(Environment.SystemDirectory, "notepad.exe") },
            "calculator" => new[] { Path.Combine(Environment.SystemDirectory, "calc.exe") },
            "explorer" => new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe") },
            "chrome" => new[] { Path.Combine(program, "Google", "Chrome", "Application", "chrome.exe"), Path.Combine(x86, "Google", "Chrome", "Application", "chrome.exe"), Path.Combine(local, "Google", "Chrome", "Application", "chrome.exe") },
            "edge" => new[] { Path.Combine(x86, "Microsoft", "Edge", "Application", "msedge.exe"), Path.Combine(program, "Microsoft", "Edge", "Application", "msedge.exe") },
            "kakao" => new[] { Path.Combine(x86, "Kakao", "KakaoTalk", "KakaoTalk.exe"), Path.Combine(program, "Kakao", "KakaoTalk", "KakaoTalk.exe") },
            "vscode" => new[] { Path.Combine(local, "Programs", "Microsoft VS Code", "Code.exe"), Path.Combine(program, "Microsoft VS Code", "Code.exe") },
            "claude" => Directory.Exists(Path.Combine(local, "AnthropicClaude")) ? Directory.GetFiles(Path.Combine(local, "AnthropicClaude"), "claude.exe", SearchOption.AllDirectories) : Array.Empty<string>(),
            _ => Array.Empty<string>()
        };
        var path = candidates.FirstOrDefault(File.Exists);
        if (path == null) throw new InvalidOperationException("앱 설치 위치를 찾지 못했어요: " + app);
        return Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
}

public static class VolumeControl
{
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class DeviceEnumerator { }
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDeviceEnumerator
    {
        int EnumAudioEndpoints(int flow, uint state, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IDevice device);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDevice
    {
        [PreserveSig] int Activate(ref Guid id, uint context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(uint access, out IntPtr properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IEndpoint
    {
        int RegisterControlChangeNotify(IntPtr notify); int UnregisterControlChangeNotify(IntPtr notify); int GetChannelCount(out uint count); int SetMasterVolumeLevel(float level, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context); int GetMasterVolumeLevel(out float level);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
    }
    public static int Change(int value, bool relative = false)
    {
        var enumerator = (IDeviceEnumerator)new DeviceEnumerator(); IDevice? device = null; object? endpoint = null;
        try
        {
            Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(0, 1, out device)); var iid = typeof(IEndpoint).GUID;
            Marshal.ThrowExceptionForHR(device.Activate(ref iid, 23, IntPtr.Zero, out endpoint)); var volume = (IEndpoint)endpoint;
            Marshal.ThrowExceptionForHR(volume.GetMasterVolumeLevelScalar(out var current)); var result = Math.Clamp(relative ? (int)Math.Round(current * 100) + value : value, 0, 100); var context = Guid.Empty;
            Marshal.ThrowExceptionForHR(volume.SetMasterVolumeLevelScalar(result / 100f, ref context)); return result;
        }
        finally { if (endpoint != null) Marshal.ReleaseComObject(endpoint); if (device != null) Marshal.ReleaseComObject(device); Marshal.ReleaseComObject(enumerator); }
    }
    public sealed record Snapshot(string DeviceId, float Level);
    static T WithEndpoint<T>(Func<IDevice, IEndpoint, T> action)
    {
        var enumerator = (IDeviceEnumerator)new DeviceEnumerator(); IDevice? device = null; object? endpoint = null;
        try
        {
            Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(0, 1, out device)); var iid = typeof(IEndpoint).GUID;
            Marshal.ThrowExceptionForHR(device.Activate(ref iid, 23, IntPtr.Zero, out endpoint)); return action(device, (IEndpoint)endpoint);
        }
        finally { if (endpoint != null) Marshal.ReleaseComObject(endpoint); if (device != null) Marshal.ReleaseComObject(device); Marshal.ReleaseComObject(enumerator); }
    }
    public static Snapshot Capture() => WithEndpoint((device, endpoint) =>
    {
        Marshal.ThrowExceptionForHR(device.GetId(out var id)); Marshal.ThrowExceptionForHR(endpoint.GetMasterVolumeLevelScalar(out var level)); return new Snapshot(id, level);
    });
    public static void Restore(Snapshot previous, Snapshot expected) => WithEndpoint((device, endpoint) =>
    {
        Marshal.ThrowExceptionForHR(device.GetId(out var id)); Marshal.ThrowExceptionForHR(endpoint.GetMasterVolumeLevelScalar(out var current));
        if (id != previous.DeviceId || id != expected.DeviceId || Math.Abs(current - expected.Level) > 0.005f) throw new InvalidOperationException("출력 장치나 음량이 이후에 바뀌어 덮어쓰지 않았어요. 원하는 음량을 다시 말해 주세요.");
        var context = Guid.Empty; Marshal.ThrowExceptionForHR(endpoint.SetMasterVolumeLevelScalar(previous.Level, ref context));
        Marshal.ThrowExceptionForHR(endpoint.GetMasterVolumeLevelScalar(out var restored));
        if (Math.Abs(restored - previous.Level) > 0.005f) throw new InvalidOperationException("장치에서 음량 복원을 확인하지 못했어요.");
        return true;
    });
}
