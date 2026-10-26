using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

public sealed partial class Brain
{
    public static string? CodexBinary()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var paths = new List<string> { Path.Combine(home, ".local", "bin", "codex.exe") };
        var search = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));
        var architecture = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        var triple = architecture == "arm64" ? "aarch64-pc-windows-msvc" : "x86_64-pc-windows-msvc";
        foreach (var directory in search.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct())
        {
            paths.Add(Path.Combine(directory, "codex.exe"));
            foreach (var prefix in new[] { Path.Combine(directory, "node_modules", "@openai"), Path.Combine(directory, "node_modules", "@openai", "codex", "node_modules", "@openai") })
                foreach (var folder in new[] { "bin", "codex" }) paths.Add(Path.Combine(prefix, "codex-win32-" + architecture, "vendor", triple, folder, "codex.exe"));
        }
        return paths.FirstOrDefault(File.Exists);
    }

    internal static ProcessStartInfo CodexInfo(string binary, string schemaFile, UiSettings settings)
    {
        var info = new ProcessStartInfo(binary) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = Path.Combine(AppPaths.Home, "work") };
        foreach (var arg in new[] { "exec", "--json", "--ephemeral", "--skip-git-repo-check", "--ignore-user-config", "--ignore-rules", "--sandbox", "read-only", "--color", "never", "--output-schema", schemaFile, "-c", "approval_policy=\"never\"", "-c", "model_reasoning_effort=\"low\"", "-c", "project_doc_max_bytes=0", "-c", "web_search=\"disabled\"" }) info.ArgumentList.Add(arg);
        foreach (var feature in new[] { "shell_tool", "unified_exec", "apps", "browser_use", "browser_use_external", "computer_use", "image_generation", "plugins", "multi_agent", "hooks", "goals", "tool_suggest", "skill_mcp_dependency_install", "skill_search", "memories", "view_image" }) { info.ArgumentList.Add("--disable"); info.ArgumentList.Add(feature); }
        info.ArgumentList.Add("--enable"); info.ArgumentList.Add("skip_host_skill_discovery");
        if (!string.IsNullOrWhiteSpace(settings.BrainModel)) { info.ArgumentList.Add("--model"); info.ArgumentList.Add(settings.BrainModel); }
        info.ArgumentList.Add("-");
        foreach (var name in new[] { "CODEX_THREAD_ID", "CODEX_SESSION_ID", "CLAUDECODE" }) info.Environment.Remove(name);
        info.Environment["NO_COLOR"] = "1";
        return info;
    }

    static async Task<CommandPlan> CodexAsync(string prompt, UiSettings settings, CancellationToken token, McpEndpoint? mcp = null)
    {
        var binary = CodexBinary() ?? throw new InvalidOperationException("Codex가 없어요. Codex CLI를 설치하고 로그인한 뒤 다시 시도하세요.");
        var schemaFile = Path.Combine(AppPaths.Home, "work", "response-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(schemaFile, Schema, token);
        try
        {
            var info = CodexInfo(binary, schemaFile, settings); mcp?.ConfigureCodex(info);
            using var process = new Process { StartInfo = info }; process.Start();
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(120));
            try { await process.StandardInput.WriteAsync(prompt); process.StandardInput.Close(); await process.WaitForExitAsync(timeout.Token); }
            catch { if (!process.HasExited) process.Kill(true); throw; }
            var stdout = await output; var stderr = await error;
            if (process.ExitCode != 0) throw new InvalidOperationException("Codex 연결 실패: " + Short(stderr.Length > 0 ? stderr : stdout));
            return DecodeCodexEvents(stdout);
        }
        finally { if (File.Exists(schemaFile)) File.Delete(schemaFile); }
    }

    internal static CommandPlan DecodeCodexEvents(string stream)
    {
        string? answer = null; var completed = false;
        foreach (var line in stream.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var document = JsonDocument.Parse(line); var root = document.RootElement;
            var type = root.GetProperty("type").GetString();
            if (type is "error" or "turn.failed") throw new InvalidOperationException("Codex 응답 실패: " + Short(line));
            if (type == "turn.completed") completed = true;
            if (type == "item.completed" && root.TryGetProperty("item", out var item) && item.GetProperty("type").GetString() == "agent_message") answer = item.GetProperty("text").GetString();
        }
        if (!completed || string.IsNullOrWhiteSpace(answer)) throw new InvalidOperationException("Codex가 완료된 답변을 반환하지 않았어요.");
        using var result = JsonDocument.Parse(answer); return Decode(result.RootElement);
    }
}
