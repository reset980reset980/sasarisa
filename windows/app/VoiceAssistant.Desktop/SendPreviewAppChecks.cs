using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VoiceAssistant.Desktop;

public partial class App
{
    sealed class PreviewDriver : IClaudeSessionDriver
    {
        internal SelectedClaudeSession Current = new("검사 세션", "검사 프로젝트", "https://claude.ai/epitaxy/session_fixture");
        internal string Draft = "", OwnedText = "";
        internal SelectedClaudeSession? OwnedSession;
        internal bool Lease, FailStage, FailSend;
        internal int Sends, Clears, CleanupCalls;
        public Task<ClaudeSessionSnapshot> ReadAsync(CancellationToken token) => Task.FromResult(new ClaudeSessionSnapshot(new[] { new ClaudeSession("session-key", Current.Title, Current.Project) }));
        public Task<SelectedClaudeSession> SelectAsync(ClaudeSession session, CancellationToken token) => Task.FromResult(Current);
        public Task<SelectedClaudeSession> PrepareAsync(ClaudeSession session, CancellationToken token) { token.ThrowIfCancellationRequested(); if (Draft.Length != 0) throw new AgentStoppedException("Existing draft"); return Task.FromResult(Current); }
        public Task SendAsync(SelectedClaudeSession session, string text, CancellationToken token) => throw new Exception("Unreviewed send path used");
        public Task StagePreviewAsync(SelectedClaudeSession session, string text, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); if (session != Current || Draft.Length != 0) throw new AgentStoppedException("Changed composer");
            Lease = true; OwnedSession = session; Draft = OwnedText = text;
            if (FailStage) throw new AgentStoppedException("Stage failed after writing"); return Task.CompletedTask;
        }
        public Task SendPreviewAsync(SelectedClaudeSession session, string text, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); var valid = Lease && Current == session && session == OwnedSession && Draft == text && text == OwnedText; Lease = false;
            if (!valid) throw new AgentStoppedException("Changed staged draft"); Sends++; Draft = "";
            if (FailSend) throw new AgentStoppedException("Send acceptance unknown"); return Task.CompletedTask;
        }
        public Task<bool> CancelPreviewAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); CleanupCalls++;
            if (!Lease) return Task.FromResult(true); Lease = false;
            if (Current != OwnedSession || Draft != OwnedText) return Task.FromResult(false);
            Draft = ""; Clears++; return Task.FromResult(true);
        }
    }
    async Task<int> RunSendPreviewAppChecksAsync(List<object> rows)
    {
        int failed = 0;
        window.SendPreviewAnswered += yes => AnswerSendPreview(yes ? "네" : "아니", false);
        void Require(bool ok, string reason) { if (!ok) throw new Exception(reason); }
        async Task Check(string name, Func<Task> test)
        {
            try { await test(); Require(!input.Running && input.ProcessId == 0 && PresentationSource.FromVisual(window) == null && PresentationSource.FromVisual(overlay) == null, "Preview test opened a microphone or native surface"); rows.Add(new { name, pass = true }); }
            catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.ToString() }); }
            finally
            {
                var previous = turn; turn = null; try { previous?.Cancel(); } catch (ObjectDisposedException) { }
                StopTurn(); StopInput();
            }
        }
        async Task<(PreviewDriver Driver, ClaudeSessions Sessions)> Prepare(bool failStage = false, bool failSend = false)
        {
            StopTurn(); StopInput(); settings = new UiSettings { Speak = false, PreviewBeforeSend = true }; window.LoadSettings(settings);
            var driver = new PreviewDriver { FailStage = failStage, FailSend = failSend }; var sessions = new ClaudeSessions(driver); var page = await sessions.ListAsync("", 0, default);
            await sessions.PrepareAsync(page.Sessions.Single().Id, "승인할 작업", default); return (driver, sessions);
        }
        async Task FinishCanceled(Task<SelectedClaudeSession?> review)
        { try { Require(await review == null, "Canceled preview returned a sent session"); } catch (OperationCanceledException) { } }
        await Check("Actual inline send preview stages exact text then sends once through its dedicated reviewed path", async () =>
        {
            var fixture = await Prepare(); using var own = new CancellationTokenSource(); turn = own; busy = true;
            var review = ReviewClaudeDraftAsync(fixture.Sessions, "승인할 작업", "검사 프로젝트 / 검사 세션\n\n승인할 작업", own.Token);
            Require(AwaitingSendPreview && fixture.Driver.Draft == "승인할 작업" && fixture.Driver.Sends == 0, "Preview sent before an answer");
            var card = (FrameworkElement)window.FindName("SendPreviewCard"); card.Measure(new Size(650, double.PositiveInfinity)); card.Arrange(new Rect(0, 0, 650, card.DesiredSize.Height)); card.UpdateLayout();
            var drawing = new DrawingVisual(); var bounds = new Rect(0, 0, 650, card.ActualHeight);
            using (var context = drawing.RenderOpen()) { context.DrawRectangle(Brushes.White, null, bounds); context.DrawRectangle(new VisualBrush(card), null, bounds); }
            var bitmap = new RenderTargetBitmap(650, Math.Max(1, (int)Math.Ceiling(bounds.Height)), 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using (var stream = File.Create(Path.Combine(AppPaths.WindowsRoot, "artifacts", "execution-consent-0246", "send-preview.png"))) png.Save(stream);
            ((Button)window.FindName("SendPreviewYesButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(await review == fixture.Driver.Current && fixture.Driver.Sends == 1 && fixture.Driver.Clears == 0 && !AwaitingSendPreview && !listening, "Inline review did not send exactly once");
            ((Button)window.FindName("SendPreviewYesButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Require(fixture.Driver.Sends == 1, "Repeated click sent twice"); turn = null;
        });
        await Check("Actual cancel clears only the assistant-owned unchanged draft and preserves edits or another session", async () =>
        {
            foreach (var change in new[] { "none", "draft", "session" })
            {
                var fixture = await Prepare(); using var own = new CancellationTokenSource(); turn = own; busy = true;
                var review = ReviewClaudeDraftAsync(fixture.Sessions, "승인할 작업", "초안 확인", own.Token);
                if (change == "draft") fixture.Driver.Draft = "사용자가 고친 글";
                if (change == "session") fixture.Driver.Current = fixture.Driver.Current with { Address = "https://claude.ai/epitaxy/session_other" };
                ((Button)window.FindName("SendPreviewNoButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(await review == null && fixture.Driver.Sends == 0 && fixture.Driver.Clears == (change == "none" ? 1 : 0), "Cancel erased a changed draft or sent"); turn = null;
            }
        });
        await Check("Actual Korean final-transcript answer can finish a busy preview without replacing its owning turn", async () =>
        {
            var fixture = await Prepare(); using var own = new CancellationTokenSource(); turn = own; busy = true; listening = true;
            var review = ReviewClaudeDraftAsync(fixture.Sessions, "승인할 작업", "초안 확인", own.Token);
            speechCapturePending = waitingBarge = false; speechTailUntil = DateTimeOffset.MinValue; awakeUntil = DateTimeOffset.Now.AddSeconds(20);
            OnSpeech(JsonSerializer.SerializeToElement(new { type = "final", utterance = lastUtterance + 1, text = "네", command = "네", has_wake = false }));
            Require(await review != null && fixture.Driver.Sends == 1 && turn == own && !own.IsCancellationRequested, "Transcript canceled its own preview instead of answering"); turn = null;
        });
        await Check("A real twenty-second unanswered preview expires without sending and clears its unchanged staged draft", async () =>
        {
            var fixture = await Prepare(); using var own = new CancellationTokenSource(); turn = own; busy = true;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var result = await ReviewClaudeDraftAsync(fixture.Sessions, "승인할 작업", "응답 없는 초안 확인", own.Token).WaitAsync(TimeSpan.FromSeconds(25));
            Require(result == null && watch.Elapsed >= TimeSpan.FromSeconds(19) && fixture.Driver.Sends == 0 && fixture.Driver.Clears == 1 && !AwaitingSendPreview && !listening, "Unanswered preview failed to expire or started listening"); turn = null;
        });
        await Check("Stop and a different real fast command cancel preview and remove only its unchanged draft", async () =>
        {
            foreach (var command in new[] { "stop", "new" })
            {
                var fixture = await Prepare(); using var own = new CancellationTokenSource(); turn = own; busy = true;
                var review = ReviewClaudeDraftAsync(fixture.Sessions, "승인할 작업", "초안 확인", own.Token); var before = timers.Items.Count;
                if (command == "stop") StopTurn(); else await SubmitAsync("5분 타이머", false);
                await FinishCanceled(review); Require(fixture.Driver.Sends == 0 && fixture.Driver.Clears == 1 && !AwaitingSendPreview, "Canceled preview sent or left its unchanged draft");
                if (command == "new") Require(timers.Items.Count == before + 1, "New command was swallowed as a preview answer"); turn = null;
            }
        });
        await Check("Partially staged failures are cleaned up and uncertain sends cannot be replayed", async () =>
        {
            foreach (var stage in new[] { true, false })
            {
                var fixture = await Prepare(failStage: stage, failSend: !stage); using var own = new CancellationTokenSource(); turn = own; busy = true;
                var review = ReviewClaudeDraftAsync(fixture.Sessions, "승인할 작업", "초안 확인", own.Token); if (!stage) AnswerSendPreview("네", false);
                bool rejected = false; try { await review; } catch (AgentStoppedException) { rejected = true; }
                Require(rejected && fixture.Driver.Sends == (stage ? 0 : 1) && fixture.Driver.Clears == (stage ? 1 : 0), "Partial failure cleanup or uncertain send handling incorrect");
                try { await fixture.Sessions.SendAsync("승인할 작업", default); throw new Exception("Consumed send replayed"); } catch (AgentStoppedException) { }
                Require(fixture.Driver.Sends <= 1, "Uncertain send retried"); turn = null;
            }
        });
        await Check("Remote responses cannot approve a pending local staged preview", async () =>
        {
            var fixture = await Prepare(); using var own = new CancellationTokenSource(); turn = own; busy = true;
            var review = ReviewClaudeDraftAsync(fixture.Sessions, "승인할 작업", "초안 확인", own.Token);
            await new RemoteExecution(CancellationToken.None, freeMode: true).RunAsync(() => { Require(!AnswerSendPreview("네", true), "Remote answer became local approval"); return Task.CompletedTask; });
            Require(await review == null && fixture.Driver.Sends == 0 && fixture.Driver.Clears == 1, "Remote answer sent a preview"); turn = null;
        });
        return failed;
    }
}
