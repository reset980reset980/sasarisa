using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

public partial class App
{
    async Task<int> RunTextEditAppChecksAsync()
    {
        var rows = new List<object>(); int failed = 0;
        settings = new UiSettings { Speak = false }; settings.Text.PolishDictation = false; window = new MainWindow();
        var pipeName = "sasarisa-undo-test-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        info.ArgumentList.Add("--self-test"); info.ArgumentList.Add("--text-edit-fixture"); info.ArgumentList.Add(pipeName);
        using var child = Process.Start(info) ?? throw new IOException("Could not start owned fixture");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3)); var token = timeout.Token;
        StreamReader? reader = null; StreamWriter? writer = null;
        void Require(bool value, string message) { if (!value) throw new Exception(message); }
        async Task Check(string name, Func<Task> test) { try { await test(); rows.Add(new { name, pass = true }); } catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.ToString() }); } }
        async Task<JsonElement> Peer(string kind, string text = "", int start = 0, int length = 0)
        {
            await writer!.WriteLineAsync(JsonSerializer.Serialize(new { kind, text, start, length }));
            using var json = JsonDocument.Parse(await reader!.ReadLineAsync(token) ?? throw new IOException("Fixture closed")); return json.RootElement.Clone();
        }
        async Task<WindowsActions.InputTarget> Set(string text, int start, int length = 0)
        {
            var peer = await Peer("set", text, start, length); hands.TrackForeground(); var target = hands.CaptureInputTarget();
            Require(target.Window.ToInt64() == peer.GetProperty("handle").GetInt64() && target.ProcessId == child.Id, "Target is not owned fixture"); return target;
        }
        async Task AssertState(string text, int start, int length = 0)
        {
            var state = await Peer("read"); Require(state.GetProperty("text").GetString() == text && state.GetProperty("start").GetInt32() == start && state.GetProperty("length").GetInt32() == length && state.GetProperty("untouched").GetString() == "다른 칸 유지", "Wrong fixture text, selection or unrelated field: " + state);
        }
        async Task RejectUndo()
        {
            try { await ExecuteTrackedAsync(new("undo"), token); } catch (Exception ex) when (ex is InvalidOperationException or System.Windows.Automation.ElementNotAvailableException) { return; }
            throw new Exception("Unsafe undo accepted");
        }
        try
        {
            await pipe.WaitForConnectionAsync(token); reader = new StreamReader(pipe); writer = new StreamWriter(pipe) { AutoFlush = true };
            await Check("Native type and undo restore Korean/emoji selection and surrounding lines", async () =>
            {
                var bound = await Set("앞🎵 바꿀말\n뒤", 4, 3);
                await ExecuteTrackedAsync(new("type", "새 글"), token, bound);
                await AssertState("앞🎵 새 글\n뒤", 7);
                await ExecuteTrackedAsync(new("undo"), token); await AssertState("앞🎵 바꿀말\n뒤", 4, 3);
            });
            await Check("Native sequential edits undo in reverse order despite caret movement", async () =>
            {
                var bound = await Set("앞뒤", 1); await ExecuteTrackedAsync(new("type", "하나"), token, bound); await ExecuteTrackedAsync(new("type", "둘"), token, bound);
                await Peer("set", "앞하나둘뒤", 0); await ExecuteTrackedAsync(new("undo"), token); await AssertState("앞하나뒤", 3);
                await ExecuteTrackedAsync(new("undo"), token); await AssertState("앞뒤", 1);
            });
            await Check("Native undo refuses intervening user edits and preserves their contents", async () =>
            {
                var bound = await Set("원문", 2); await ExecuteTrackedAsync(new("type", " 추가"), token, bound);
                await Peer("set", "사용자가 고친 글", 3); await RejectUndo(); await AssertState("사용자가 고친 글", 3);
            });
            await Check("Native undo refuses another window then works after returning to its original window", async () =>
            {
                var bound = await Set("원문", 1); await ExecuteTrackedAsync(new("type", "추가"), token, bound);
                await Peer("other"); hands.TrackForeground(); await RejectUndo(); await Peer("focus"); hands.TrackForeground();
                await ExecuteTrackedAsync(new("undo"), token); await AssertState("원문", 1);
            });
            await Check("Native undo refuses a field that became read-only", async () =>
            {
                var bound = await Set("원문", 1); await ExecuteTrackedAsync(new("type", "추가"), token, bound); await Peer("readonly"); await RejectUndo(); await AssertState("원추가문", 3);
            });
            await Check("Native observed fill and insertion each register exact document undo", async () =>
            {
                var bound = await Set("앞 선택 뒤", 2, 2); var controls = new DesktopControls(hands.DesktopControlDriver(bound));
                var page = await controls.ListAsync("되돌리기 시험칸", 0, token); await controls.FillAsync(page.Controls.Single().Id, "교체한 전체 글", token); await actionHistory.UndoAsync(token); await AssertState("앞 선택 뒤", 2, 2);
                page = await controls.ListAsync("되돌리기 시험칸", 0, token); await controls.InsertAsync(page.Controls.Single().Id, "추가🎵", token); await actionHistory.UndoAsync(token); await AssertState("앞 선택 뒤", 2, 2);
            });
            await Check("Native selected-text rewrite restores the original selection", async () =>
            {
                await Set("보존 바꿀말 보존", 3, 3); var selection = await hands.ReadSelectionAsync(token); await hands.ReplaceSelectionAsync(selection, "수정", token); await actionHistory.UndoAsync(token); await AssertState("보존 바꿀말 보존", 3, 3);
            });
            await Check("Native dictation undo uses the verified history instead of Ctrl+Z", async () =>
            {
                var bound = await Set("앞뒤", 1); await ApplyDictationAsync("받아쓴 글", token, bound); await ApplyDictationAsync("되돌려", token, bound); await AssertState("앞뒤", 1);
                actionHistory.Record("전송 요청"); await Peer("set", "사용자 수정", 2); await ApplyDictationAsync("되돌려", token, bound); await AssertState("사용자 수정", 2);
            });
            await Check("Native cancelled undo writes nothing", async () =>
            {
                var bound = await Set("앞뒤", 1); await ExecuteTrackedAsync(new("type", "추가"), token, bound);
                using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); try { await actionHistory.UndoAsync(cancelled.Token); throw new Exception("Ignored cancellation"); } catch (OperationCanceledException) { }
                await AssertState("앞추가뒤", 3);
            });
            await Check("Native undo refuses replacement of the original control", async () =>
            {
                var bound = await Set("원문", 1); await ExecuteTrackedAsync(new("type", "추가"), token, bound); await Peer("replace"); await RejectUndo(); await AssertState("새 입력칸", 0);
            });
        }
        finally
        {
            if (writer != null) try { await writer.WriteLineAsync("{\"kind\":\"close\"}"); } catch (IOException) { }
            if (!child.HasExited) { using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(5)); try { await child.WaitForExitAsync(grace.Token); } catch (OperationCanceledException) { child.Kill(true); } }
            writer?.Dispose(); reader?.Dispose(); window.Close();
        }
        var report = Path.Combine(AppPaths.WindowsRoot, "artifacts", "text-undo-app-checks.json"); Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        File.WriteAllText(report, JsonSerializer.Serialize(new { total = rows.Count, failed, checks = rows }, SettingsStore.Json)); return failed == 0 ? 0 : 1;
    }
}
