using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

internal static class CodexEffortChecks
{
    static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    static void Reject(Action action) { try { action(); } catch (ArgumentException) { return; } throw new Exception("Invalid effort accepted"); }
    internal static int Run(List<object> rows)
    {
        int failed = 0;
        void Check(string name, Action action) { try { action(); rows.Add(new { name, pass = true }); } catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.ToString() }); } }
        Check("Each Codex effort reaches exactly one CLI override while preserving isolated read-only execution", () =>
        {
            foreach (var effort in new[] { "low", "medium", "high" })
            {
                var info = Brain.CodexInfo("fixture.exe", "fixture-schema.json", new UiSettings { BrainEffort = effort, BrainModel = "fixture-model" });
                new McpEndpoint("fixture-pipe", new string('a', 64)).ConfigureCodex(info);
                var overrides = info.ArgumentList.Where(x => x.StartsWith("model_reasoning_effort=", StringComparison.Ordinal)).ToArray();
                Require(overrides.SequenceEqual(new[] { "model_reasoning_effort=\"" + effort + "\"" }), "Selected effort omitted or overwritten");
                Require(info.CreateNoWindow && !info.UseShellExecute && info.ArgumentList.Contains("read-only") && info.ArgumentList.Contains("approval_policy=\"never\"") && info.ArgumentList.Contains("--ignore-user-config") && info.ArgumentList.Contains("web_search=\"disabled\"") && info.ArgumentList.Last() == "-" && info.ArgumentList.Contains("fixture-model"), "Execution boundaries or model changed");
                foreach (var feature in new[] { "shell_tool", "unified_exec", "browser_use", "computer_use", "multi_agent", "hooks" })
                { var index = info.ArgumentList.IndexOf(feature); Require(index > 0 && info.ArgumentList[index - 1] == "--disable", "Effort enabled another capability"); }
            }
        });
        Check("Unknown null and command-shaped efforts fail validation before a CLI can start", () =>
        {
            foreach (var effort in new[] { null, "", "LOW", "xhigh", "high\nother=true", "high\" -c shell_tool=true" })
            {
                var settings = new UiSettings { BrainEffort = effort! };
                Reject(() => SettingsStore.Validate(settings)); Reject(() => Brain.CodexInfo("fixture.exe", "fixture.json", settings));
            }
        });
        Check("Legacy settings keep low and transfer imports thinking only with AI connection selection", () =>
        {
            Require(JsonSerializer.Deserialize<UiSettings>("{}")!.BrainEffort == "low", "Legacy requests changed");
            var current = new UiSettings { BrainEffort = "medium" }; var imported = SettingsTransfer.Parse(SettingsTransfer.Serialize(current with { BrainEffort = "high" }));
            Require(imported.BrainEffort == "high" && SettingsTransfer.Copy(imported).BrainEffort == "high", "Export/copy lost thinking");
            Require(SettingsTransfer.Prepare(imported, current, false, false).BrainEffort == "medium" && SettingsTransfer.Prepare(imported, current, true, false).BrainEffort == "high", "Connection opt-in ignored");
            Require(SettingsTransfer.Describe(current, imported).Contains("Codex 생각 깊이") && SettingsTransfer.Describe(current, imported).Contains("높음"), "Import review hid change");
        });
        Check("Mac thinking imports map all supported levels preserve omitted values and reject bad data", () =>
        {
            var current = new UiSettings { BrainEffort = "medium" }; var before = JsonSerializer.Serialize(current);
            foreach (var effort in new[] { "low", "medium", "high" })
            {
                var imported = MacSettingsTransfer.Parse(JsonSerializer.Serialize(new { brainEffort = effort }), current);
                Require(imported.Settings.BrainEffort == effort && imported.Notices.Any(x => x.Contains("Codex")), "Mac level or scope lost");
            }
            Require(MacSettingsTransfer.Parse("{\"assistantName\":\"코코\"}", current).Settings.BrainEffort == "medium" && JsonSerializer.Serialize(current) == before, "Partial import mutated current thinking");
            foreach (var text in new[] { "{\"brainEffort\":\"xhigh\"}", "{\"brainEffort\":7}", "{\"brainEffort\":null}" }) Reject(() => MacSettingsTransfer.Parse(text, current));
        });
        Check("Combo selection and text-only fallback preserve the chosen Codex thinking and model", () =>
        {
            var settings = new UiSettings { BrainProvider = "combo", BrainEffort = "high", ProviderProfiles = new() { ["codex"] = new("selected-codex", "") } };
            foreach (var command in new[] { "시간 설명해", "코딩 도와줘", "사이트를 살펴봐" })
            {
                var selected = ComboBrain.Resolve(settings, command, new[] { "codex" });
                Require(selected.Settings.BrainEffort == "high" && selected.Settings.BrainModel == "selected-codex", "Combo lost thinking/model on fallback");
            }
            Require(ComboBrain.Resolve(settings, "웹사이트 글 다듬기", new[] { "codex", "aside" }, true).Settings.BrainEffort == "high", "Text-only path changed thinking");
        });
        return failed;
    }

    internal static async Task<int> RunNativeAsync(List<object> rows, bool mediumOnly, CancellationToken token)
    {
        int failed = 0;
        foreach (var effort in mediumOnly ? new[] { "medium" } : new[] { "low", "medium", "high" })
        {
            var name = "Actual Codex accepts selected " + effort + " thinking and returns a completed answer without actions";
            try
            {
                var settings = new UiSettings { BrainProvider = "codex", BrainEffort = effort, Speak = false };
                var plan = await new Brain().AskAsync("연결 시험입니다. 도구나 작업 없이 '연결 성공'이라고만 답하고 actions는 빈 배열로 반환하세요.", "", settings, token);
                Require(plan.Reply.Contains("연결 성공") && plan.Actions.Count == 0, "Expected text-only completed reply missing");
                rows.Add(new { name, pass = true, effort, reply = plan.Reply });
            }
            catch (Exception ex) { failed++; rows.Add(new { name, pass = false, effort, error = ex.Message }); }
        }
        return failed;
    }

    internal static async Task<int> RunProcessAsync(List<object> rows)
    {
        int failed = 0;
        async Task Check(string name, Func<Task> action) { try { await action(); rows.Add(new { name, pass = true }); } catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.ToString() }); } }
        Process Child(string script)
        {
            var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command", script }) info.ArgumentList.Add(arg);
            return new Process { StartInfo = info };
        }
        await Check("Codex process transport reads completed stdout and stderr without a console window", async () =>
        {
            using var process = Child("$text = [Console]::In.ReadToEnd(); [Console]::Out.Write($text); [Console]::Error.Write('fixture error')");
            var result = await Brain.RunCodexProcessAsync(process, "fixture input", CancellationToken.None, TimeSpan.FromSeconds(10));
            Require(result.Output == "fixture input" && result.Error == "fixture error" && process.HasExited && process.ExitCode == 0, "Completed process or pipe output lost");
            Require(Brain.CodexResponseLimit == TimeSpan.FromSeconds(180), "Original Mac 180-second response allowance changed");
        });
        await Check("Codex response deadline terminates the child and reports a timeout rather than user cancellation", async () =>
        {
            using var process = Child("[Console]::In.ReadToEnd() | Out-Null; Start-Sleep -Seconds 30"); bool timedOut = false;
            try { await Brain.RunCodexProcessAsync(process, "fixture", CancellationToken.None, TimeSpan.FromMilliseconds(200)); }
            catch (TimeoutException ex) { timedOut = ex.Message.Contains("생각 깊이"); }
            Require(timedOut && process.HasExited, "Timeout hid its reason or left a child alive");
        });
        await Check("Explicit cancellation stays cancellation and joins the Codex child before returning", async () =>
        {
            using var process = Child("[Console]::In.ReadToEnd() | Out-Null; Start-Sleep -Seconds 30"); using var cancel = new CancellationTokenSource(250); bool cancelled = false;
            try { await Brain.RunCodexProcessAsync(process, "fixture", cancel.Token, TimeSpan.FromSeconds(30)); }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { cancelled = true; }
            Require(cancelled && process.HasExited, "User stop became a timeout or left a child alive");
        });
        return failed;
    }
}
