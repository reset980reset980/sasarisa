using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VoiceAssistant.Desktop;

internal static class ExecutionConsentChecks
{
    sealed class Driver : IDesktopControlDriver
    {
        internal DesktopControl Current = new("button", "Button", "전송", "일반 메시지", "invoke", false, "Enabled", "r1");
        internal int Mutations;
        internal int Copies = 1;
        internal bool Truncated;
        public Task<DesktopControlSnapshot> ReadAsync(CancellationToken token) => Task.FromResult(new DesktopControlSnapshot("fixture", "fixture window", Enumerable.Repeat(Current, Copies).ToArray(), Truncated));
        public Task PressAsync(DesktopControl control, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); if (control != Current) throw new AgentStoppedException("Changed target"); Mutations++; return Task.CompletedTask;
        }
        public Task FillAsync(DesktopControl control, string text, CancellationToken token) => throw new Exception("Unexpected fill");
        public Task InsertAsync(DesktopControl control, string text, CancellationToken token) => throw new Exception("Unexpected insert");
    }
    sealed class ImageDriver : IVisualDriver
    {
        internal readonly VisualFrame Frame = VisualChecks.Fixture();
        internal VisualTarget? Target = new("button", "Send", "Message", "Button", "Enabled", "r1");
        internal int Clicks;
        public Task<VisualFrame> CaptureAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(Frame); }
        public Task<VisualTarget?> TargetAsync(VisualFrame frame, int x, int y, CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(Target); }
        public Task ClickAsync(VisualFrame frame, int x, int y, CancellationToken token) { token.ThrowIfCancellationRequested(); Clicks++; return Task.CompletedTask; }
    }
    internal static async Task<int> RunAsync(List<object> rows)
    {
        int failed = 0;
        void Require(bool value, string reason) { if (!value) throw new Exception(reason); }
        void Reject(Action action) { try { action(); } catch (AgentStoppedException) { return; } throw new Exception("Missing permission was accepted"); }
        async Task RejectAsync(Func<Task> action) { try { await action(); } catch (AgentStoppedException) { return; } throw new Exception("Changed or ambiguous target accepted"); }
        async Task Check(string name, Func<Task> work) { try { await work(); rows.Add(new { name, pass = true }); } catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.ToString() }); } }
        await Check("Execution defaults preserve confirmation and payment always requires PC review", () =>
        {
            var old = JsonSerializer.Deserialize<UiSettings>("{}")!; Require(!old.FreeMode && !old.FreeModeAsked, "Existing profile enabled free mode");
            foreach (var enabled in new[] { false, true }) foreach (var remote in new[] { false, true })
            {
                var action = new AssistantAction("press_control", Guid.NewGuid().ToString("N"));
                Require(ExecutionConsent.Decide(enabled, action, true, ActionEffect.Payment, remote) == (remote ? ConsentDecision.HoldRemote : ConsentDecision.Ask), "Payment bypassed consent");
                Require(ExecutionConsent.Decide(enabled, action, true, ActionEffect.Unknown, remote) == (remote ? ConsentDecision.HoldRemote : ConsentDecision.Ask), "Unknown target bypassed review");
                Require(ExecutionConsent.Decide(enabled, action, true, ActionEffect.Send, remote) == (enabled ? ConsentDecision.Run : remote ? ConsentDecision.HoldRemote : ConsentDecision.Ask), "Free mode did not preserve the default boundary");
            }
            return Task.CompletedTask;
        });
        await Check("Observed payment labels outrank sending and browsing exemptions cannot hide a payment", () =>
        {
            foreach (var label in new[] { "결제하기", "주문 확정", "Send money", "Pay now", "Confirm order", "購入する", "付款", "Pagar", "Acheter", "Bezahlen", "Comprar", "Pagare", "Thanh toán", "ชำระเงิน", "Bayar" })
                Require(ExecutionConsent.LabelEffect(label) == ActionEffect.Payment, "Payment label missed: " + label);
            Require(ExecutionConsent.LabelEffect("Continue", "Checkout") == ActionEffect.Payment, "Observed payment context was ignored");
            Require(ExecutionConsent.LabelEffect("Payment history") == ActionEffect.None && ExecutionConsent.LabelEffect("결제내역 조회 후 결제") == ActionEffect.Payment, "Browsing exemption hid an execution");
            Require(ExecutionConsent.LabelEffect("Continue") == ActionEffect.Unknown, "Unclassified Continue auto-approved");
            Require(ExecutionConsent.Effect(new("type", "pay now")) == ActionEffect.None, "Model text became a target label");
            return Task.CompletedTask;
        });
        await Check("Settings exchanges require explicit permission opt-in independently of connections and devices", () =>
        {
            var current = new UiSettings(); var imported = SettingsTransfer.Parse(SettingsTransfer.Serialize(current with { FreeMode = true, FreeModeAsked = true }));
            Require(!SettingsTransfer.Prepare(imported, current, true, true).FreeMode, "Connection or device opt-in enabled execution rights");
            var enabled = SettingsTransfer.Prepare(imported, current, false, false, permissions: true);
            Require(enabled.FreeMode && enabled.FreeModeAsked && SettingsTransfer.Describe(current, enabled).Contains("자유 모드"), "Permission selection or review lost");
            Require(SettingsTransfer.Prepare(current, enabled, false, false).FreeMode, "Unselected import disabled the current choice");
            var mac = MacSettingsTransfer.Parse("{\"freeMode\":true,\"freeModeAsked\":true}", current);
            Require(mac.Settings.FreeMode && mac.Settings.FreeModeAsked && mac.Notices.Any(x => x.Contains("선택한 경우")), "Mac rights did not convert with a notice");
            Require(!SettingsTransfer.Prepare(mac.Settings, current, false, false).FreeMode, "Mac import silently raised permissions");
            return Task.CompletedTask;
        });
        await Check("Actual hidden settings and import review apply free mode only after explicit Save and opt-in", () =>
        {
            var current = new UiSettings { Speak = false }; var view = new MainWindow(); view.LoadSettings(current);
            try
            {
                view.SettingsSaved += next => { current = next; view.LoadSettings(next); return null; };
                var checkbox = (CheckBox)view.FindName("FreeModeInput"); checkbox.IsChecked = true;
                Require(!current.FreeMode, "Draft changed execution rights"); view.SaveSettingsForCheck();
                Require(current.FreeMode && current.FreeModeAsked, "Actual Save did not capture explicit choice");
                checkbox.IsChecked = false; view.SaveSettingsForCheck(); Require(!current.FreeMode && current.FreeModeAsked, "Turning off reset one-time choice");
                var review = new SettingsImportWindow(current with { FreeMode = true }, current);
                try
                {
                    review.SelectConnections(true); review.SelectDevices(true); Require(!review.AcceptSelection().FreeMode, "Unrelated import checkbox granted rights");
                    review.SelectPermissions(true); Require(review.AcceptSelection().FreeMode && review.ChangeDescription.Contains("자유 모드"), "Permission import did not appear in review");
                    Require(!review.IsVisible && PresentationSource.FromVisual(review) == null, "Import window was presented");
                }
                finally { review.Close(); }
                Require(!view.IsVisible && PresentationSource.FromVisual(view) == null, "Settings window was presented");
                var section = (FrameworkElement)view.FindName("ExecutionConsentSection"); section.Measure(new Size(650, double.PositiveInfinity)); section.Arrange(new Rect(0, 0, 650, section.DesiredSize.Height)); section.UpdateLayout();
                var bounds = new Rect(0, 0, 650, section.ActualHeight); var drawing = new DrawingVisual();
                using (var context = drawing.RenderOpen()) { context.DrawRectangle(Brushes.White, null, bounds); context.DrawRectangle(new VisualBrush(section), null, bounds); }
                var bitmap = new RenderTargetBitmap(650, Math.Max(1, (int)Math.Ceiling(bounds.Height)), 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                var folder = Path.Combine(AppPaths.WindowsRoot, "artifacts", "execution-consent-0246"); Directory.CreateDirectory(folder);
                using var file = File.Create(Path.Combine(folder, "consent-settings.png")); png.Save(file);
            }
            finally { view.Close(); }
            return Task.CompletedTask;
        });
        await Check("Remote mode is captured per request and requires prepared nonpayment permission for each action", async () =>
        {
            var send = new AssistantAction("kakao_send", "Fixture message", Target: Guid.NewGuid().ToString("N"));
            await new RemoteExecution(CancellationToken.None).RunAsync(() => { Reject(() => RemoteExecution.Guard(send, true, preparing: true)); return Task.CompletedTask; });
            var scope = new RemoteExecution(CancellationToken.None, freeMode: true);
            await scope.RunAsync(() =>
            {
                RemoteExecution.Guard(send, true, preparing: true); Reject(() => RemoteExecution.Guard(send, true));
                Reject(() => scope.GrantPrepared(send, ActionEffect.Payment)); Reject(() => scope.GrantPrepared(send, ActionEffect.Unknown));
                scope.GrantPrepared(send, ActionEffect.Send); RemoteExecution.Guard(send, true);
                Reject(() => RemoteExecution.Guard(send with { Value = "Changed message" }, true));
                RemoteExecution.Guard(send, true, preparing: true); Reject(() => RemoteExecution.Guard(send, true));
                return Task.CompletedTask;
            });
            Require(RemoteExecution.Current == null, "Remote permission scope leaked");
        });
        await Check("Free-mode controller permission cannot bypass a changed observed target", async () =>
        {
            var driver = new Driver(); var controls = new DesktopControls(driver);
            var page = await controls.ListAsync("", 0, CancellationToken.None); var action = new AssistantAction("press_control", page.Controls.Single().Id);
            Require(controls.Effect(action) == ActionEffect.Send && ExecutionConsent.Decide(true, action, true, controls.Effect(action), false) == ConsentDecision.Run, "Known send not eligible");
            driver.Current = driver.Current with { Name = "결제", Revision = "r2" };
            try { await controls.PressAsync(action.Value, CancellationToken.None); throw new Exception("Changed payment target pressed"); } catch (AgentStoppedException) { }
            Require(driver.Mutations == 0, "Permission bypassed the native driver contract");
        });
        await Check("Actual MCP runner declines payments while prepared nonpayment actions retain lifecycle checks", async () =>
        {
            var driver = new Driver(); var controls = new DesktopControls(driver); var page = await controls.ListAsync("", 0, CancellationToken.None);
            var action = new AssistantAction("press_control", page.Controls.Single().Id); var scope = new RemoteExecution(CancellationToken.None, freeMode: true);
            await scope.RunAsync(async () =>
            {
                var runner = new McpAgentRun((candidate, token) =>
                {
                    RemoteExecution.Guard(candidate, true, preparing: true); var effect = controls.Effect(candidate);
                    Require(ExecutionConsent.Decide(true, candidate, true, effect, true) == ConsentDecision.Run, "Prepared action held"); scope.GrantPrepared(candidate, effect); return Task.FromResult(true);
                }, async (candidate, token) => { RemoteExecution.Guard(candidate, true); await controls.PressAsync(candidate.Value, token); return new ActionOutcome("Fixture applied"); }, null, null);
                Require((await runner.DispatchAsync(action, CancellationToken.None)).Status == "succeeded" && driver.Mutations == 1, "Prepared MCP action failed");
            });
            driver.Current = driver.Current with { Name = "Pay now", Revision = "r3" }; page = await controls.ListAsync("", 0, CancellationToken.None); action = new("press_control", page.Controls.Single().Id);
            var payment = new McpAgentRun((candidate, token) => Task.FromResult(ExecutionConsent.Decide(true, candidate, true, controls.Effect(candidate), false) == ConsentDecision.Run),
                (_, _) => throw new Exception("Payment executor reached"), null, null);
            Require((await payment.DispatchAsync(action, CancellationToken.None)).Status == "declined", "Payment consent skipped");
        });
        await Check("Named buttons bind one unique complete observation and can only be invoked once", async () =>
        {
            var driver = new Driver(); var now = DateTimeOffset.UtcNow; var controls = new DesktopControls(driver, () => now); var action = new AssistantAction("tap", "전송");
            var button = await controls.PrepareButtonAsync(action, default); Require(button.Effect == ActionEffect.Send, "Observed send label lost");
            await RejectAsync(() => button.ApplyAsync(action with { Value = "삭제" }, default));
            await button.ApplyAsync(action, default); await RejectAsync(() => button.ApplyAsync(action, default));
            button = await controls.PrepareButtonAsync(action, default); now += TimeSpan.FromMinutes(3); await RejectAsync(() => button.ApplyAsync(action, default));
            Require(driver.Mutations == 1, "Unbound, duplicate or expired request mutated the target");
        });
        await Check("Duplicate truncated and changed named targets never inherit free-mode approval", async () =>
        {
            var driver = new Driver { Copies = 2 }; var controls = new DesktopControls(driver); var action = new AssistantAction("tap", "전송");
            await RejectAsync(() => controls.PrepareButtonAsync(action, default)); driver.Copies = 1; driver.Truncated = true;
            await RejectAsync(() => controls.PrepareButtonAsync(action, default)); driver.Truncated = false;
            foreach (var change in new Func<DesktopControl, DesktopControl>[] { c => c with { Name = "결제" }, c => c with { Context = "Checkout" }, c => c with { State = "Disabled" }, c => c with { Revision = "r2" } })
            {
                driver.Current = new("button", "Button", "전송", "일반 메시지", "invoke", false, "Enabled", "r1");
                var button = await controls.PrepareButtonAsync(action, default); driver.Current = change(driver.Current);
                await RejectAsync(() => button.ApplyAsync(action, default));
            }
            driver.Current = driver.Current with { Name = "전송", Context = "Checkout" };
            Require((await controls.PrepareButtonAsync(action, default)).Effect == ActionEffect.Payment, "Named payment context bypassed review");
            Require(driver.Mutations == 0, "An ambiguous or changed button was invoked");
        });
        await Check("Image clicks classify the observed target and recheck identity context state and revision despite identical pixels", async () =>
        {
            var driver = new ImageDriver(); var visual = new VisualControls(driver); var original = driver.Target!;
            foreach (var change in new Func<VisualTarget, VisualTarget>[] { t => t with { Identity = "other" }, t => t with { Name = "Pay" }, t => t with { Context = "Checkout" }, t => t with { State = "Disabled" }, t => t with { Revision = "r2" } })
            {
                driver.Target = original; var image = await visual.CaptureAsync(default); var review = await visual.PrepareClickAsync(image.Id, 480, 175, default);
                Require(review.Effect == ActionEffect.Send, "Observed image target not classified"); driver.Target = change(original);
                await RejectAsync(() => visual.ClickAsync(image.Id, 480, 175, default));
            }
            Require(driver.Clicks == 0, "Identical pixels hid a changed action target");
        });
        await Check("Image permission binds exact coordinates while inaccessible targets retain local manual review", async () =>
        {
            var driver = new ImageDriver(); var visual = new VisualControls(driver); var image = await visual.CaptureAsync(default);
            await visual.PrepareClickAsync(image.Id, 480, 175, default); await RejectAsync(() => visual.ClickAsync(image.Id, 481, 175, default));
            driver.Target = null; image = await visual.CaptureAsync(default); var review = await visual.PrepareClickAsync(image.Id, 480, 175, default);
            var action = new AssistantAction("click", image.Id, X: 480, Y: 175);
            Require(review.Effect == ActionEffect.Unknown && ExecutionConsent.Decide(true, action, true, review.Effect, false) == ConsentDecision.Ask && ExecutionConsent.Decide(true, action, true, review.Effect, true) == ConsentDecision.HoldRemote, "Unknown image auto-approved");
            await visual.ClickAsync(image.Id, 480, 175, default); Require(driver.Clicks == 1, "Reviewed inaccessible image path was removed");
            driver.Target = new("button", "Continue", "Checkout", "Button", "Enabled", "r1"); image = await visual.CaptureAsync(default);
            Require((await visual.PrepareClickAsync(image.Id, 480, 175, default)).Effect == ActionEffect.Payment, "Payment image context lost");
        });
        await Check("One-time free-mode offers require a local eligible action and an exact timely answer", () =>
        {
            var now = DateTimeOffset.UtcNow; var offer = new FreeModeOffer(() => now); var settings = new UiSettings();
            Require(!offer.Begin(settings, ActionEffect.Payment, false) && !offer.Begin(settings, ActionEffect.Send, true), "Ineligible action offered rights");
            Require(offer.Begin(settings, ActionEffect.Send, false) && offer.Answer("yes, delete it", false) == FreeModeAnswer.None, "A new command enabled rights");
            Require(offer.Begin(settings, ActionEffect.Delete, false) && offer.Answer("응", true) == FreeModeAnswer.None, "Remote answer enabled local rights");
            offer.Begin(settings, ActionEffect.Send, false); now = now.AddSeconds(21); Require(offer.Answer("yes", false) == FreeModeAnswer.None, "Expired answer enabled rights");
            Require(!offer.Begin(settings with { FreeModeAsked = true }, ActionEffect.Send, false), "Offer repeated after owner choice");
            offer.Begin(settings, ActionEffect.Send, false); Require(offer.Answer("네!", false) == FreeModeAnswer.Yes && !offer.Pending, "Explicit local yes was lost");
            return Task.CompletedTask;
        });
        await Check("Execution styles distinguish direct and AI delegation without weakening payments unknown targets or preview", () =>
        {
            Require(new UiSettings().ConfirmDelegate == "always", "Old Windows profile lost its confirmation default");
            var delegateAction = new AssistantAction("delegate", "Fix the project"); var aiDelegate = new AssistantAction("delegate_session", "Fix the project", Target: Guid.NewGuid().ToString("N"));
            foreach (var mode in new[] { "never", "brain", "always" })
            {
                var options = new UiSettings { ConfirmDelegate = mode };
                Require(ExecutionConsent.Decide(options, delegateAction, false, ActionEffect.Delegate, false) == (mode == "always" ? ConsentDecision.Ask : ConsentDecision.Run), "Direct delegation style ignored");
                Require(ExecutionConsent.Decide(options, aiDelegate, true, ActionEffect.Delegate, false) == ConsentDecision.Ask, "AI delegated without consent");
                Require(ExecutionConsent.Decide(options, new("press_control", "id"), true, ActionEffect.None, false) == (mode == "always" ? ConsentDecision.Ask : ConsentDecision.Run), "Observed ordinary target style ignored");
                foreach (var effect in new[] { ActionEffect.Send, ActionEffect.Post, ActionEffect.Delete, ActionEffect.Payment, ActionEffect.Unknown })
                    Require(ExecutionConsent.Decide(options, new("press_control", "id"), true, effect, false) == ConsentDecision.Ask, "Execution style bypassed irreversible or unknown action");
                Require(ExecutionConsent.Decide(options, delegateAction, false, ActionEffect.Delegate, true) == ConsentDecision.HoldRemote, "Execution style granted remote rights");
                options = options with { FreeMode = true, PreviewBeforeSend = true };
                Require(ExecutionConsent.Decide(options, aiDelegate, true, ActionEffect.Delegate, true) == ConsentDecision.HoldRemote, "Free mode bypassed remote preview");
            }
            bool invalid = false; try { SettingsStore.Validate(new UiSettings { ConfirmDelegate = "never\n" }); } catch (ArgumentException) { invalid = true; }
            Require(invalid, "Invalid execution style accepted"); return Task.CompletedTask;
        });
        await Check("Execution style and send preview survive explicit Save and require independent import permission", () =>
        {
            var current = new UiSettings(); var view = new MainWindow(); view.LoadSettings(current);
            try
            {
                view.SettingsSaved += next => { current = next; view.LoadSettings(next); return null; };
                var modes = (ComboBox)view.FindName("ConfirmDelegateInput"); modes.SelectedItem = modes.Items.Cast<ComboBoxItem>().Single(x => (string)x.Tag == "brain");
                ((CheckBox)view.FindName("PreviewBeforeSendInput")).IsChecked = true;
                Require(current.ConfirmDelegate == "always" && !current.PreviewBeforeSend, "Draft changed live consent"); view.SaveSettingsForCheck();
                Require(current.ConfirmDelegate == "brain" && current.PreviewBeforeSend, "Actual Save lost new preferences");
                var imported = SettingsTransfer.Parse(SettingsTransfer.Serialize(current with { ConfirmDelegate = "never", PreviewBeforeSend = false }));
                Require(SettingsTransfer.Prepare(imported, current, true, true).ConfirmDelegate == "brain" && SettingsTransfer.Prepare(imported, current, true, true).PreviewBeforeSend, "Unrelated import lowered confirmation");
                var next = SettingsTransfer.Prepare(imported, current, false, false, permissions: true);
                Require(next.ConfirmDelegate == "never" && !next.PreviewBeforeSend && SettingsTransfer.Describe(current, next).Contains("전송 전 미리 보기"), "Explicit permissions omitted from import review");
                var mac = MacSettingsTransfer.Parse("{\"confirmDelegate\":\"brain\",\"previewBeforeSend\":true}", new UiSettings());
                Require(mac.Settings.ConfirmDelegate == "brain" && mac.Settings.PreviewBeforeSend && !SettingsTransfer.Prepare(mac.Settings, new UiSettings(), false, false).PreviewBeforeSend, "Mac preview rights were dropped or auto-applied");
                Require(PresentationSource.FromVisual(view) == null && !view.IsVisible, "Settings native window created");
            }
            finally { view.Close(); }
            return Task.CompletedTask;
        });
        await Check("Send previews expire and accept only a timely exact local answer once", async () =>
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var answer in new[] { "네", "아니", "네, 다른 사람에게 보내", "yes please delete", "YES!" })
            {
                var preview = new SendPreview(() => now); preview.Begin(); var pending = preview.WaitAsync(default);
                var accepted = preview.Answer(answer, false); var sent = await pending;
                Require(sent == (answer is "네" or "YES!") && accepted == (answer is "네" or "아니" or "YES!"), "A new command became a send approval");
                Require(!preview.Pending && !preview.Answer("네", false), "Send permission replayed");
            }
            var expired = new SendPreview(() => now); expired.Begin(); now += TimeSpan.FromSeconds(21); Require(!expired.Answer("네", false) && !await expired.WaitAsync(default), "Expired preview sent");
            var remote = new SendPreview(); remote.Begin(); Require(!remote.Answer("yes", true) && !await remote.WaitAsync(default), "Remote answer approved local send");
            var stopped = new SendPreview(); stopped.Begin(); stopped.Cancel(); Require(!await stopped.WaitAsync(default), "Stopped preview sent");
        });
        return failed;
    }
}
