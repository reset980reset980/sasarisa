using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VoiceAssistant.Desktop;

internal static class AiUsageAppChecks
{
    static IEnumerable<T> Children<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T item) yield return item;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()) foreach (var result in Children<T>(child)) yield return result;
    }
    internal static async Task<int> RunAsync()
    {
        if (Path.GetFullPath(AppPaths.Home).Equals(Path.GetFullPath(AppPaths.StandardHome), StringComparison.OrdinalIgnoreCase)) return 2;
        var rows = new List<object>(); var views = new List<Window>(); int failed = 0;
        var root = Path.Combine(AppPaths.Home, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        SettingsStore.Save(new() { Speak = false, Desktop = new() { SetupComplete = true, StartupListening = "off" } });
        var settingsBefore = File.ReadAllBytes(AppPaths.ConfigFile); var listening = Path.Combine(AppPaths.Home, "listening-state.json"); File.WriteAllText(listening, "{\"Requested\":false}"); var listeningBefore = File.ReadAllBytes(listening);
        var at = new DateTimeOffset(2026,10,29,12,0,0,TimeSpan.FromHours(9));
        void Require(bool value, string reason) { if (!value) throw new Exception(reason); }
        T Track<T>(T view) where T : Window { views.Add(view); return view; }
        AiUsageStore Store(string name) => new(Path.Combine(root, name + ".json"));
        AiUsageWindow View(AiUsageStore store, string language = "ko") => Track(new AiUsageWindow(store, "자비스", language, Path.Combine(root, "empty-sessions"), () => at));
        void Hidden() { foreach (var view in views) Require(!view.IsVisible && PresentationSource.FromVisual(view) == null, "Native presentation surface was created"); Require(File.ReadAllBytes(AppPaths.ConfigFile).SequenceEqual(settingsBefore) && File.ReadAllBytes(listening).SequenceEqual(listeningBefore) && WhisperEngine.Shared.ProcessId == 0, "Usage UI changed settings, listening or engine state"); }
        async Task Check(string name, Func<Task> run) { try { await run(); Hidden(); rows.Add(new { name, pass = true }); } catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.ToString() }); } }
        Task Sync(Action run) { run(); return Task.CompletedTask; }
        string Text(AiUsageWindow view) => string.Join("\n", Children<TextBlock>((DependencyObject)view.Content).Select(x => x.Text));
        AiUsageStore Fixture(string name)
        {
            var store = Store(name);
            for (int i = 0; i < 7; i++) store.Record(new(Guid.NewGuid().ToString("N"), "claude", at.AddDays(-i), 123, "response", new(100+i*20,20,1000,30,true)));
            store.Record(new(Guid.NewGuid().ToString("N"), "codex", at, 45,"failed",new()));
            store.Record(null, new[] { new AiQuotaWindow("claude","five_hour","5시간",.42,at.AddHours(2),at.AddMinutes(-2)), new AiQuotaWindow("codex","primary","5시간",.64,at.AddMinutes(-1),at.AddHours(-6)), new AiQuotaWindow("codex","secondary","7일",.28,null,at.AddMinutes(-5)) }); return store;
        }
        void Render(AiUsageWindow view, string file)
        {
            var content = (FrameworkElement)view.Content; view.Content = null; var canvas = new Grid { Background = (Brush)Application.Current.FindResource("CanvasBrush") }; canvas.Children.Add(content);
            canvas.Measure(new Size(940,900)); canvas.Arrange(new Rect(0,0,940,900)); canvas.UpdateLayout();
            var bitmap = new RenderTargetBitmap(940,900,96,96,PixelFormats.Pbgra32); bitmap.Render(canvas); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var output = File.Create(Path.Combine(AppPaths.Home,file)); encoder.Save(output); Require(output.Length > 5000, "Empty offscreen image");
        }
        try
        {
            await Check("Hidden AI usage entry and empty state distinguish absent history and account quota", () => Sync(() =>
            {
                var main = Track(new MainWindow()); Require(main.FindName("AiUsageButton") is Button, "Settings entry missing"); var view = View(Store("empty"));
                Require(view.Summary.Contains("기록된 AI 요청이 없어요") && view.QuotaCount == 2 && Text(view).Contains("아직 보고된 한도가 없어요"), "Empty state implies zero plan usage");
            }));
            await Check("Hidden quota UI preserves expired percentages, unknown reset and observed timestamps", () => Sync(() =>
            {
                var view = View(Fixture("quota")); view.LoadSnapshot(Fixture("quota-display").Snapshot()); var text = Text(view);
                Require(view.QuotaCount == 3 && text.Contains("64% 사용") && text.Contains("새 보고 필요") && text.Contains("초기화 시각 미제공") && text.Contains("관측 "), "Stale or unknown quota misrepresented");
                Require(Children<ProgressBar>((DependencyObject)view.Content).Any(x => x.Value == 64 && x.Foreground == Brushes.SlateGray), "Expired bar reset or still appears current");
            }));
            await Check("Hidden usage filters and metrics separate known tokens, missing counts and call attempts", () => Sync(() =>
            {
                var store = Fixture("filter"); var view = View(store); view.LoadSnapshot(store.Snapshot());
                Require(view.Summary.Contains("2회") && view.Summary.Contains("토큰 수 보고 1회") && view.Summary.Contains("정보 미제공 1회") && view.Summary.Contains("토큰 막대"), "Partial count omitted");
                view.Select("codex",0); Require(view.Summary.Contains("전체 토큰 미제공") && view.QuotaCount == 2 && !view.Summary.Contains("전체 토큰 0"), "Missing tokens became zero");
                view.Select("claude",1); Require(view.QuotaCount == 1 && view.Summary.Contains("1.15K"), "Raw token/provider filter incorrect");
                view.Select("all",2); Require(!view.Summary.Contains("* 토큰") && view.Summary.Contains("2회"), "Request metric hides attempts");
            }));
            await Check("Hidden quota-only history remains selectable without inventing app requests", () => Sync(() =>
            {
                var store = Store("quota-only"); store.Record(null,new[] { new AiQuotaWindow("codex","primary","5시간",.3,null,at) }); var view = View(store); view.LoadSnapshot(store.Snapshot()); view.Select("codex",0);
                Require(view.QuotaCount == 1 && view.Summary.Contains("기록된 AI 요청이 없어요"), "Quota-only provider absent from filter or fabricated history");
            }));
            await Check("Hidden refresh reads local fixture quota without model requests or microphone changes", async () =>
            {
                var sessions = Path.Combine(root,"sessions"); var day = Path.Combine(sessions,at.LocalDateTime.ToString("yyyy/MM/dd",System.Globalization.CultureInfo.InvariantCulture)); Directory.CreateDirectory(day);
                File.WriteAllText(Path.Combine(day,"fixture.jsonl"), JsonSerializer.Serialize(new { timestamp = at.ToString("O"), type = "event_msg", payload = new { type = "token_count", rate_limits = new { primary = new { used_percent = 31 } } } }));
                var store = Store("refresh"); var view = Track(new AiUsageWindow(store,"자비스","ko",sessions,()=>at)); await view.RefreshAsync();
                Require(!view.Refreshing && Text(view).Contains("31% 사용") && view.StatusText.Contains("마지막 확인") && store.Snapshot().State.Days.Count == 0, "Refresh did not read quota only");
            });
            await Check("Hidden refresh surfaces corrupt storage errors and preserves both file and backup", async () =>
            {
                var path = Path.Combine(root,"broken.json"); File.WriteAllText(path,"{broken"); File.WriteAllText(path+".backup","previous"); var view = View(new(path)); await view.RefreshAsync();
                Require(view.StatusText.Contains("사용 기록을 읽거나 저장하지 못했어요") && File.ReadAllText(path) == "{broken" && File.ReadAllText(path+".backup") == "previous", "Storage error hidden or overwritten");
            });
            await Check("Closing hidden usage view cancels refresh and ignores late completion", async () =>
            {
                var view = View(Store("close")); var refresh = view.RefreshAsync(); var original = view.StatusText; view.Close(); await refresh; Require(!view.Refreshing && view.StatusText == original, "Closed view accepted late refresh");
            });
            await Check("Korean and English AI usage views render offscreen without presenting windows", () => Sync(() =>
            {
                var store = Fixture("render"); var ko = View(store); ko.LoadSnapshot(store.Snapshot()); var en = View(store,"en"); en.LoadSnapshot(store.Snapshot());
                Require(en.Title == "AI usage" && en.Summary.Contains("counts unavailable") && Text(en).Contains("new report needed"), "English usage text missing"); Render(ko,"ai-usage-ko.png"); Render(en,"ai-usage-en.png");
            }));
        }
        finally { foreach (var view in views) view.Close(); }
        File.WriteAllText(Path.Combine(AppPaths.Home,"ai-usage-app-checks.json"),JsonSerializer.Serialize(new { total=rows.Count,failed,visibleWindows=0,microphoneCapture=false,speakerPlayback=false,checks=rows },SettingsStore.Json)); return failed == 0 ? 0 : 1;
    }
}
