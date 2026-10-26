using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows;

namespace VoiceAssistant.Desktop;

internal static class DesktopChecks
{
    static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    static void Reject(Action action) { try { action(); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException) { return; } throw new Exception("Unexpected acceptance"); }
    sealed class Backend : IHotkeyBackend
    {
        public readonly Dictionary<int, HotkeySpec> Registered = new();
        public HotkeySpec? Occupied;
        public bool Register(int id, HotkeySpec spec)
        {
            if (spec == Occupied || Registered.Values.Contains(spec)) return false;
            Registered[id] = spec; return true;
        }
        public void Unregister(int id) => Registered.Remove(id);
    }
    public static int Run(List<object> rows)
    {
        int failed = 0;
        void Check(string name, Action test) { try { test(); rows.Add(new { name, pass = true }); } catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.Message }); } }
        Check("Hotkey normalization restrictions and hold release", () =>
        {
            var chord = HotkeySpec.Parse("shift+alt+control+f8")!;
            Require(chord.Label == "Ctrl+Alt+Shift+F8", "Not canonical");
            var down = new HashSet<int> { 0x77, 0x11, 0x12, 0x10 };
            Require(chord.IsPressed(down.Contains), "Held chord missed");
            foreach (var key in down.ToArray()) { down.Remove(key); Require(!chord.IsPressed(down.Contains), "Release missed"); down.Add(key); }
            foreach (var text in new[] { "a", "Shift+Space", "Ctrl+F12", "Ctrl+Ctrl+A", "Ctrl++", "Ctrl+F25" }) Reject(() => HotkeySpec.Parse(text));
            Reject(() => HotkeyManager.ValidatePair("Ctrl+Alt+F8", "Alt+Ctrl+F8", false));
            Reject(() => HotkeyManager.ValidatePair("none", "Ctrl+F8", true));
        });
        Check("Hotkey conflict and save failure restore both previous bindings", () =>
        {
            var backend = new Backend(); using var manager = new HotkeyManager(IntPtr.Zero, backend);
            manager.Initialize("Ctrl+Alt+F8", "Ctrl+Alt+F7"); bool saved = false;
            backend.Occupied = HotkeySpec.Parse("Ctrl+Alt+F6");
            Reject(() => manager.Configure("Ctrl+Alt+F5", "Ctrl+Alt+F6", () => saved = true));
            Require(!saved && backend.Registered.Count == 2 && manager.Wake?.Label == "Ctrl+Alt+F8" && manager.Stop?.Label == "Ctrl+Alt+F7", "Failed registration changed bindings");
            Reject(() => manager.Configure("Ctrl+Alt+F5", "Ctrl+Alt+F4", () => throw new IOException("disk failure")));
            Require(backend.Registered.Count == 2 && manager.Wake?.Label == "Ctrl+Alt+F8" && manager.Stop?.Label == "Ctrl+Alt+F7", "Failed save changed bindings");
            manager.Configure("Ctrl+Alt+F7", "Ctrl+Alt+F8", () => saved = true);
            Require(saved && backend.Registered[1].Label == "Ctrl+Alt+F7", "Swapped bindings not applied");
            manager.Configure("none", "Ctrl+Alt+F8", () => { }); Require(manager.Wake == null && backend.Registered.Count == 1, "Disable failed");
        });
        Check("Hotkey startup collision uses session fallback without overwriting preference", () =>
        {
            var backend = new Backend { Occupied = HotkeySpec.Parse("Ctrl+Alt+F8") }; using var manager = new HotkeyManager(IntPtr.Zero, backend);
            var messages = manager.Initialize("Ctrl+Alt+F8", "none");
            Require(manager.Wake?.Label == "Ctrl+Alt+F9" && manager.Stop == null && messages.Count == 1, "Fallback or notice missing");
        });
        Check("Windows rejects occupied global hotkey and restores old registration", () =>
        {
            using var ready = new ManualResetEventSlim(); using var finish = new ManualResetEventSlim(); bool registered = false;
            var other = new Thread(() => { registered = Native.RegisterHotKey(IntPtr.Zero, 91, 0x4007, 0x84); ready.Set(); try { finish.Wait(TimeSpan.FromSeconds(10)); } finally { if (registered) Native.UnregisterHotKey(IntPtr.Zero, 91); } });
            other.Start();
            try
            {
                Require(ready.Wait(TimeSpan.FromSeconds(3)) && registered, "Could not reserve test chord Ctrl+Alt+Shift+F21");
                using var manager = new HotkeyManager(IntPtr.Zero);
                Require(manager.Initialize("Ctrl+Alt+Shift+F19", "Ctrl+Alt+Shift+F20").Count == 0, "Test chords unavailable");
                bool persisted = false; Reject(() => manager.Configure("Ctrl+Alt+Shift+F21", "Ctrl+Alt+Shift+F20", () => persisted = true));
                Require(!persisted && manager.Wake?.Label == "Ctrl+Alt+Shift+F19" && manager.Stop?.Label == "Ctrl+Alt+Shift+F20", "Native rollback failed");
            }
            finally { finish.Set(); other.Join(3000); }
        });
        Check("Old settings migrate and personalized settings roundtrip", () =>
        {
            var old = JsonSerializer.Deserialize<UiSettings>("{\"Name\":\"자비스\",\"BrainProvider\":\"codex\"}")!;
            Require(!old.Desktop.SetupComplete && old.Desktop.WakeHotkey == "Ctrl+Shift+Space" && old.BrainProvider == "codex", "Old settings lost defaults");
            var original = old with { Desktop = new() { UserName = "선생님", SetupComplete = true, WakeHotkey = "Ctrl+Alt+F8", OverlayPosition = "top", OverlayScreen = "previous-monitor", GreetingStyle = "calm", GreetingPhrases = new[] { "오늘 시작" }, GreetingText = "{user}, {assistant}입니다." } };
            var copy = JsonSerializer.Deserialize<UiSettings>(JsonSerializer.Serialize(original))!; SettingsStore.Validate(copy);
            Require(copy.Desktop.SetupComplete && copy.Desktop.UserName == "선생님" && copy.Desktop.OverlayScreen == "previous-monitor" && copy.Desktop.GreetingPhrases.Single() == "오늘 시작", "Preferences lost on reload");
        });
        Check("Greeting custom phrases replace defaults and preserve command boundaries", () =>
        {
            var settings = new UiSettings { Desktop = new() { UserName = "선생님", GreetingText = "{user}, {assistant}입니다." } };
            Require(Greetings.Parse("자비스 아빠 왔어", settings)?.Actions.Single().Kind == "greet", "Default greeting missing");
            settings.Desktop.GreetingPhrases = new[] { "오늘 시작" };
            Require(Greetings.Parse("자비스, 오늘 시작", settings)?.Reply == "선생님, 자비스입니다.", "Custom greeting or placeholders failed");
            foreach (var text in new[] { "안녕", "오늘 시작하지 마", "오늘 시작이라고 써 줘" }) Require(Greetings.Parse(text, settings) == null, "Greeting swallowed ordinary command");
        });
        Check("Greeting launch targets validate URL scheme and maximum count", () =>
        {
            foreach (var target in new[] { "file:///C:/Windows/notepad.exe", "javascript:alert(1)", "https://user:secret@example.com" }) Reject(() => new DesktopPreferences { GreetingUrls = new[] { target } }.Validate("voice"));
            Reject(() => new DesktopPreferences { GreetingUrls = Enumerable.Repeat("https://example.com", 9).ToArray() }.Validate("voice"));
            new DesktopPreferences { GreetingUrls = new[] { "https://example.com" }, GreetingApps = new[] { "notepad" } }.Validate("voice");
        });
        Check("Overlay positions respect negative monitor origin and small work areas", () =>
        {
            var area = new Rect(-1920, -100, 1920, 1040); var size = new Size(360, 200);
            Require(OverlayWindow.Placement(area, size, "bottomRight") == new Point(-380, 720), "Bottom right offset wrong");
            Require(OverlayWindow.Placement(area, size, "top") == new Point(-1140, -80), "Top offset wrong");
            Require(OverlayWindow.Placement(area, size, "bottomLeft") == new Point(-1900, 720), "Left offset wrong");
            Require(OverlayWindow.Placement(new Rect(-200, 0, 200, 100), size, "center") == new Point(-200, 0), "Oversized overlay escaped work area");
        });
        Check("Setup readiness rejects missing and empty model components", () =>
        {
            var folder = Path.Combine(AppPaths.WindowsRoot, "artifacts", "setup-tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
            Require(!SetupReadiness.HasModel(folder), "Missing model accepted");
            foreach (var file in SetupReadiness.ModelFiles) File.WriteAllText(Path.Combine(folder, file), "fixture");
            Require(SetupReadiness.HasModel(folder), "Complete model files not detected");
            File.WriteAllText(Path.Combine(folder, "tokens.txt"), ""); Require(!SetupReadiness.HasModel(folder), "Empty model component accepted");
        });
        return failed;
    }
}
