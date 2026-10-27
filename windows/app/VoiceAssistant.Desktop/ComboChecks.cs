using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

internal static class ComboChecks
{
    public static int Run(List<object> rows)
    {
        int failed = 0;
        void Require(bool value, string message) { if (!value) throw new Exception(message); }
        void Check(string name, Action check)
        { try { check(); rows.Add(new { name, pass = true }); } catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.Message }); } }
        var settings = new UiSettings { BrainProvider = "combo", BrainModel = "not-a-shared-model", Speak = false, ProviderProfiles = new() { ["codex"] = new("codex-model"), ["claude"] = new("claude-model"), ["aside"] = new("provider/aside-model"), ["combo"] = new() } };
        Check("Combo follows original Korean and English task categories with coding precedence", () =>
        {
            foreach (var (request, provider) in new[] { ("오늘 할 일을 정리해 줘", "codex"), ("메일에서 배송 상황 찾아줘", "aside"), ("웹사이트의 버그 원인을 설명해 줘", "claude"), ("Compare prices on amazon", "aside"), ("Review this GitHub commit", "claude"), ("Explain gravity", "codex") })
                Require(ComboBrain.Pick(request) == provider, request);
        });
        Check("Combo uses ordered installed fallback and rejects an empty installation", () =>
        {
            foreach (var (request, available, expected) in new[] { ("웹 페이지", new[] { "codex", "claude" }, "codex"), ("버그 수정", new[] { "aside", "codex" }, "codex"), ("안녕", new[] { "aside", "claude" }, "claude"), ("버그 수정", new[] { "aside" }, "aside") })
                Require(ComboBrain.Resolve(settings, request, available).Provider == expected, request);
            try { ComboBrain.Resolve(settings, "안녕", Array.Empty<string>()); throw new Exception("No provider silently accepted"); }
            catch (InvalidOperationException ex) { Require(ex.Message.Contains("설치"), ex.Message); }
        });
        Check("Combo selects each saved model without mutating active settings or using a shared model", () =>
        {
            foreach (var request in new[] { "안녕", "버그", "웹" })
            {
                var selected = ComboBrain.Resolve(settings, request, new[] { "codex", "claude", "aside" });
                Require(selected.Settings.BrainModel == settings.ProviderProfiles[selected.Provider].Model && !selected.Settings.Speak, "Provider model or user preference lost");
            }
            Require(settings.BrainProvider == "combo" && settings.BrainModel == "not-a-shared-model", "Saved selection changed during a request");
            var defaults = ComboBrain.Resolve(new UiSettings { BrainProvider = "combo", BrainModel = "wrong" }, "웹", new[] { "aside" });
            Require(defaults.Settings.BrainModel == "" && defaults.Settings.BrainUrl == "", "Another provider's model leaked");
        });
        Check("Combo and Aside profile settings survive export and explicit import", () =>
        {
            SettingsStore.Validate(settings);
            var restored = SettingsTransfer.Parse(SettingsTransfer.Serialize(settings));
            var imported = SettingsTransfer.Prepare(restored, new UiSettings { BrainProvider = "claude" }, true, false);
            Require(imported.BrainProvider == "combo" && imported.ProviderProfiles["aside"].Model == "provider/aside-model", "Combo profile lost");
            var keep = SettingsTransfer.Prepare(restored, new UiSettings { BrainProvider = "codex" }, false, false);
            Require(keep.BrainProvider == "codex", "Connections changed without selecting import");
        });
        Check("Combo dictation chooses text processing even if the transcript names web tasks", () =>
        {
            var result = ComboBrain.Resolve(settings, "브라우저에서 메일 보내라고 말했다", new[] { "codex", "aside" }, textOnly: true);
            Require(result.Provider == "codex", "Dictation routed to a browser task");
            Require(ComboBrain.Resolve(settings, "웹", new[] { "aside" }, textOnly: true).Provider == "aside", "Aside-only text fallback missing");
        });
        Check("Combo leaves explicitly selected API and CLI providers unchanged", () =>
        {
            foreach (var provider in new[] { "claude", "codex", "aside", "openai", "ollama" })
            {
                var original = new UiSettings { BrainProvider = provider, BrainModel = "explicit" };
                Require(ReferenceEquals(ComboBrain.Resolve(original, "웹사이트 버그", Array.Empty<string>()).Settings, original), "Explicit provider rerouted");
            }
        });
        return failed;
    }
}

public partial class App
{
    async Task<int> RunComboAppChecksAsync(bool native)
    {
        if (!IsolatedPhoneProfile()) return 2;
        InitializeInternetPhoneFixture();
        var rows = new List<object>(); int failed = 0;
        void Require(bool value, string message) { if (!value) throw new Exception(message); }
        async Task Check(string name, Func<Task> action)
        { try { await action(); rows.Add(new { name, pass = true }); } catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.Message }); } }
        try
        {
            await Check("Combo WPF selection shows routing and restores per-provider draft models", () =>
            {
                window.LoadSettings(new UiSettings { BrainProvider = "combo", ProviderProfiles = new() { ["claude"] = new("claude-saved"), ["codex"] = new("codex-saved"), ["aside"] = new("aside/saved") } });
                Require(window.ComboFields.Visibility == System.Windows.Visibility.Visible && !window.ModelInput.IsEnabled && window.ComboStatus.Text.Contains("조합"), "Combo controls unavailable");
                void Choose(string id) => window.ProviderInput.SelectedItem = window.ProviderInput.Items.Cast<System.Windows.Controls.ComboBoxItem>().Single(x => (string)x.Tag == id);
                Choose("claude"); Require(window.ModelInput.Text == "claude-saved" && window.ModelInput.IsEnabled, "Claude model missing"); window.ModelInput.Text = "claude-edited";
                Choose("aside"); Require(window.ModelInput.Text == "aside/saved", "Aside model missing");
                Choose("combo"); Choose("claude"); Require(window.ModelInput.Text == "claude-edited", "Draft model lost when toggling combo");
                return Task.CompletedTask;
            });
            settings = new UiSettings { BrainProvider = "combo", Speak = false }; window.LoadSettings(settings);
            await Check("Combo leaves quick commands in the App without starting any AI", async () =>
            {
                var result = await new RemoteExecution(CancellationToken.None).RunAsync(() => SubmitAsync("지금 몇 시야", false));
                Require(result.Contains("시") && settings.BrainProvider == "combo" && !busy, result);
            });
            await Check("Combo retains remote Aside restriction before starting browser work", async () =>
            {
                var before = settings;
                // Force only the already-selected provider here; classification is checked separately.
                settings = settings with { BrainProvider = "aside" };
                try
                {
                    await new RemoteExecution(CancellationToken.None).RunAsync(async () =>
                    {
                        try { await RunAiAsync("웹 페이지", "", CancellationToken.None); throw new Exception("Remote Aside started"); }
                        catch (AgentStoppedException ex) { Require(ex.Message.Contains("PC에서"), ex.Message); }
                    });
                }
                finally { settings = before; }
            });
            if (native)
            {
                foreach (var (provider, text, expected) in new[] {
                    ("codex", "현재 시각을 current_time 도구로 확인한 뒤 답변에 ‘시각 확인’이라고 적어 줘. 다른 동작은 하지 마.", "시각 확인"),
                    ("claude", "코드 검증용 읽기 시험이야. current_time 도구로 현재 시각을 확인하고 ‘시각 확인’이라고 답해. 파일 작업이나 다른 동작은 하지 마.", "시각 확인"),
                    ("aside", "https://example.com 웹 페이지를 읽고 제목만 답해. 다른 사이트나 로그인, 입력, 전송, 파일 작업은 하지 마.", "Example Domain") })
                    await Check("Combo actual App routes to " + provider, async () =>
                    {
                        var selection = ComboBrain.Resolve(settings, text); Require(selection.Provider == provider, provider + " is not installed");
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(150));
                        var result = await RunAiAsync(text, "", timeout.Token);
                        Require(result.Finished && result.Reply.Contains(expected, StringComparison.OrdinalIgnoreCase), result.Reply);
                        if (provider != "aside") Require(result.Observations.Any(x => x.Action.Kind == "current_time" && x.Status == "succeeded"), "No actual MCP clock observation");
                    });
            }
        }
        finally { brain.ResetAsideSession(); await brain.WaitForAsideStopAsync(); StopTurn(); overlay.Close(); window.Close(); }
        var path = System.IO.Path.Combine(AppPaths.WindowsRoot, "artifacts", "combo-app-checks.json"); System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new { total = rows.Count, failed, checks = rows }, SettingsStore.Json)); return failed == 0 ? 0 : 1;
    }
}
