using System;
using System.Collections.Generic;
using System.IO;
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
    async Task<int> RunExecutionConsentAppChecksAsync()
    {
        if (!IsolatedPhoneProfile()) return 2;
        InitializeInternetPhoneFixture(); microphonePause.Enabled = false;
        window.FreeModeAnswered += SubmitFreeModeAnswer;
        var rows = new List<object>(); var failed = 0;
        void Require(bool value, string reason) { if (!value) throw new Exception(reason); }
        async Task Check(string name, Func<Task> work)
        {
            try { await work(); rows.Add(new { name, pass = true }); }
            catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.ToString() }); }
        }
        void Reset()
        {
            CancelExecutionOffer(); settings = new UiSettings { Speak = false }; window.LoadSettings(settings); SettingsStore.Save(settings); freeModeOffer = new();
        }
        async Task Complete(ActionEffect effect, bool approved = true)
        {
            using var own = new CancellationTokenSource(); turn = own;
            try { if (approved) ConfirmedExecutionSucceeded(effect, own.Token); await OfferExecutionModeAsync(own.Token); }
            finally { if (turn == own) turn = null; }
        }
        try
        {
            await Check("Actual app offers once only after an approved eligible completion and never starts the microphone", async () =>
            {
                Reset(); await Complete(ActionEffect.Send, approved: false); Require(!freeModeOffer.Pending && !settings.FreeModeAsked, "Unapproved action offered rights");
                await Complete(ActionEffect.Payment); Require(!settings.FreeModeAsked, "Payment offered free mode");
                window.NameInput.Text = "아직 저장하지 않은 이름";
                await Complete(ActionEffect.Send);
                Require(freeModeOffer.Pending && settings.FreeModeAsked && !settings.FreeMode && SettingsStore.Load().FreeModeAsked, "Offer did not persist one-time state");
                Require(File.Exists(AppPaths.ConfigFile + ".backup") && !JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(AppPaths.ConfigFile + ".backup"), SettingsStore.Json)!.FreeModeAsked, "Before-offer backup missing");
                Require(window.NameInput.Text == "아직 저장하지 않은 이름" && SettingsStore.Load().Name == "자비스", "Offer saved or discarded unrelated draft settings");
                Require(!listening && !starting && input.ProcessId == 0, "Offer started speech input");
                var card = (FrameworkElement)window.FindName("FreeModeOfferCard"); Require(card.Visibility == Visibility.Visible && !window.IsVisible && PresentationSource.FromVisual(window) == null, "Offer created a native surface");
                card.Measure(new Size(650, 300)); card.Arrange(new Rect(0, 0, 650, card.DesiredSize.Height)); card.UpdateLayout();
                var bounds = new Rect(0, 0, 650, card.ActualHeight); var drawing = new DrawingVisual();
                using (var context = drawing.RenderOpen()) { context.DrawRectangle(Brushes.White, null, bounds); context.DrawRectangle(new VisualBrush(card), null, bounds); }
                var bitmap = new RenderTargetBitmap(650, Math.Max(1, (int)Math.Ceiling(bounds.Height)), 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); var folder = Path.Combine(AppPaths.WindowsRoot, "artifacts", "execution-consent-0246"); Directory.CreateDirectory(folder);
                using var file = File.Create(Path.Combine(folder, "consent-offer.png")); png.Save(file);
            });
            await Check("Actual local inline answer enables persisted rights without losing unrelated drafts or enabling listening", async () =>
            {
                Reset(); await Complete(ActionEffect.Delete); window.NameInput.Text = "저장 전 이름";
                ((Button)window.FindName("FreeModeYesButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Yield();
                Require(settings.FreeMode && settings.FreeModeAsked && SettingsStore.Load().FreeMode, "Inline yes was not applied through SubmitAsync");
                Require(window.FreeModeInput.IsChecked == true && window.NameInput.Text == "저장 전 이름", "Applying rights overwrote an unrelated settings draft");
                Require(!freeModeOffer.Pending && !listening && !starting && input.ProcessId == 0, "Answer repeated the offer or enabled input");
                await Complete(ActionEffect.Send); Require(!freeModeOffer.Pending, "Accepted offer repeated");
            });
            await Check("Actual text refusal preserves confirmation and one-time state", async () =>
            {
                Reset(); await Complete(ActionEffect.Post); await SubmitAsync("아니", false);
                Require(!settings.FreeMode && settings.FreeModeAsked && !SettingsStore.Load().FreeMode && !freeModeOffer.Pending, "Refusal changed rights");
                await Complete(ActionEffect.Send); Require(!freeModeOffer.Pending, "Declined offer repeated");
            });
            await Check("New commands expired answers and remote answers cannot grant local free mode", async () =>
            {
                Reset(); await Complete(ActionEffect.Send);
                Require(!await AnswerExecutionOfferAsync("네, 삭제해", CancellationToken.None) && !settings.FreeMode && !freeModeOffer.Pending, "New command granted rights");
                Reset(); var now = DateTimeOffset.UtcNow; freeModeOffer = new(() => now); await Complete(ActionEffect.Send); now += TimeSpan.FromSeconds(21);
                Require(!await AnswerExecutionOfferAsync("네", CancellationToken.None) && !settings.FreeMode, "Expired answer granted rights");
                Reset(); await Complete(ActionEffect.Send);
                await new RemoteExecution(CancellationToken.None, freeMode: true).RunAsync(async () => Require(!await AnswerExecutionOfferAsync("네", CancellationToken.None), "Remote answer consumed as local consent"));
                Require(!settings.FreeMode && !SettingsStore.Load().FreeMode, "Remote answer persisted rights");
            });
            await Check("Stop and stale completion tokens cancel offers while leaving explicit draft choices intact", async () =>
            {
                Reset(); await Complete(ActionEffect.Send); StopTurn(preserveExecutionOffer: true);
                Require(freeModeOffer.Pending && !listening && input.ProcessId == 0, "Preparing a deliberate hold answer canceled the offer or started input");
                StopTurn(); Require(!freeModeOffer.Pending && !await AnswerExecutionOfferAsync("네", CancellationToken.None), "Stop did not revoke pending offer");
                Reset(); using var old = new CancellationTokenSource(); using var current = new CancellationTokenSource(); turn = current;
                ConfirmedExecutionSucceeded(ActionEffect.Send, old.Token); await OfferExecutionModeAsync(current.Token); turn = null;
                Require(!freeModeOffer.Pending && !settings.FreeModeAsked, "Old turn completion created a new offer");
                window.FreeModeInput.IsChecked = true; window.UpdateExecutionConsent(settings with { FreeModeAsked = true });
                Require(window.FreeModeInput.IsChecked == true, "One-time state overwrote an explicit unsaved execution choice");
                Require(!listening && !starting && input.ProcessId == 0 && PresentationSource.FromVisual(window) == null && PresentationSource.FromVisual(overlay) == null, "Fixture opened input or a native window");
            });
        }
        finally { CancelExecutionOffer(); StopInput(); StopTurn(); overlay.Close(); window.Close(); }
        var report = Path.Combine(AppPaths.WindowsRoot, "artifacts", "execution-consent-app-checks.json"); Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        File.WriteAllText(report, JsonSerializer.Serialize(new { total = rows.Count, failed, visibleWindows = false, microphoneCapture = false, speakerPlayback = false, checks = rows }, SettingsStore.Json));
        return failed == 0 ? 0 : 1;
    }
}
