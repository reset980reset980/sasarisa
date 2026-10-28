using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace VoiceAssistant.Desktop;

public sealed class NameTrainingWindow : Window
{
    readonly UiSettings preferences;
    readonly NameLearning learning;
    readonly Func<UiSettings, string?> save;
    readonly SpeechInput input = new();
    readonly CancellationTokenSource lifetime = new();
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    readonly MicrophoneHealth health = new();
    readonly TextBlock prompt = Text("", 24), status = Text("마이크를 준비하고 있어요."), result = Text(""), heard = Text("");
    readonly ProgressBar meter = new() { Minimum = 0, Maximum = 1, Height = 6, Margin = new Thickness(0, 8, 0, 12) };
    readonly ListBox samples = new() { MinHeight = 100, MaxHeight = 190 };
    readonly Button start = Button("말하기 시작"), finish = Button("다 말했어요"), retry = Button("마지막 다시 하기"), apply = Button("이름과 마이크에 적용");
    readonly Button clear = Button("이 이름의 기존 보정 삭제");
    bool ready, recording, pendingCapture, finishing, refining, closed;
    DateTimeOffset started, captured, flushed, lastFrame;
    string finalText = "", finalWav = "";
    bool hadWake;

    static TextBlock Text(string value, double size = 13) => new() { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) };
    static Button Button(string title) => new() { Content = title, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 6, 8, 6) };
    public NameTrainingWindow(UiSettings settings, DeviceOption device, Func<UiSettings, string?> saveSettings)
    {
        preferences = SettingsTransfer.Copy(settings); preferences.Device = device.Id; preferences.DeviceLabel = device.Label;
        learning = new(preferences, preferences.Name); save = saveSettings;
        Title = "내 목소리로 이름 맞추기"; Width = 780; Height = 720; MinWidth = 650; MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = Brushes.WhiteSmoke;
        var panel = new StackPanel { Margin = new Thickness(24) };
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(Text("부르는 이름을 이 마이크에 맞춰 볼게요", 22));
        panel.Children.Add(Text("이름만 8번, 이름과 짧은 문장 4번을 읽어 주세요. 한 번에 최대 15초입니다. 시험 문장은 실행하지 않습니다. 음성 파일은 저장하지 않으며, 적용하면 이름·선택한 마이크·보정 발음과 시험 글을 저장합니다."));
        panel.Children.Add(Text("마이크: " + device.Label));
        if (preferences.WakeWords.Length > 0) panel.Children.Add(Text("별도 호출어를 사용 중입니다. 여기서는 이름 발음만 시험하며, 결과는 설정에서 별도 호출어 목록을 비운 뒤 호출에 사용합니다."));
        if (WhisperEngine.Enabled(preferences)) panel.Children.Add(Text("설정된 로컬 Whisper 엔진에서도 시험 발음을 확인합니다."));
        panel.Children.Add(prompt); panel.Children.Add(meter); panel.Children.Add(status); panel.Children.Add(heard);
        var controls = new WrapPanel(); controls.Children.Add(start); controls.Children.Add(finish); controls.Children.Add(retry); panel.Children.Add(controls);
        panel.Children.Add(samples); panel.Children.Add(result);
        var actions = new WrapPanel(); var close = Button("닫기");
        actions.Children.Add(apply); actions.Children.Add(clear); actions.Children.Add(close); panel.Children.Add(actions);
        clear.IsEnabled = LearnedNames.For(preferences).Length > 0;
        start.Click += (_, _) => { if (!ready) Connect(); else BeginTake(); };
        finish.Click += (_, _) => FinishTake(); retry.Click += (_, _) => { learning.RetryLast(); ShowProgress(); };
        close.Click += (_, _) => Close(); apply.Click += (_, _) => Apply();
        clear.Click += (_, _) =>
        {
            CancelCapture(); var next = SettingsTransfer.Copy(preferences); next.LearnedWakes.Remove(next.Name);
            var error = save(next); if (error != null) { status.Text = error; return; } DialogResult = true;
        };
        input.Message += (generation, message) => Dispatcher.BeginInvoke(() => { if (!closed && generation == input.Generation) OnSpeech(message); });
        input.Error += (generation, message) => Dispatcher.BeginInvoke(() => { if (!closed && generation == input.Generation) Fail(message); });
        timer.Tick += (_, _) => Tick(); Loaded += (_, _) => { Connect(); timer.Start(); };
        Closed += (_, _) => { closed = true; timer.Stop(); lifetime.Cancel(); input.Dispose(); lifetime.Dispose(); };
        ShowProgress();
    }
    void Connect()
    {
        input.Stop(); ready = recording = pendingCapture = finishing = refining = false;
        started = lastFrame = DateTimeOffset.UtcNow; health.Begin(manual: true);
        status.Text = "마이크를 준비하고 있어요. 준비가 끝나면 ‘말하기 시작’을 누르세요.";
        try { input.Start(preferences with { Activation = "hold", WakeWords = Array.Empty<string>() }); } catch (Exception ex) { Fail(ex.Message); }
        RefreshButtons();
    }
    void RefreshButtons()
    {
        var occupied = recording || finishing || refining;
        start.Content = ready ? "말하기 시작" : input.Running ? "마이크 준비 중" : "마이크 다시 연결";
        start.IsEnabled = !occupied && !learning.Finished && (ready || !input.Running);
        finish.IsEnabled = recording && !pendingCapture;
        retry.IsEnabled = !occupied && learning.Takes.Count > 0;
        apply.IsEnabled = !occupied && learning.Finished;
        clear.IsEnabled = !occupied && LearnedNames.For(preferences).Length > 0;
    }
    void ShowProgress()
    {
        prompt.Text = learning.Finished ? "12번의 시험을 마쳤어요" : $"{learning.Takes.Count + 1} / 12 · “{learning.Prompt}”";
        samples.ItemsSource = learning.Takes.Select((t, i) => $"{i + 1}. 들은 말: {t.Heard}" + (t.Refined.Length > 0 ? "\n    로컬 보정: " + t.Refined : "")).ToArray();
        if (learning.Finished)
        {
            var r = learning.Result();
            result.Text = $"이번 시험 글에서 이름 감지: {r.Before}/{r.Total} → 보정 적용 시 {r.After}/{r.Total}\n보정 발음: " +
                (r.Learned.Length == 0 ? "추가할 발음이 없어요." : string.Join(" / ", r.Learned)) +
                "\n같은 시험 글로 계산한 수치입니다. 저장 후 새로운 발화로 다시 확인해야 합니다." +
                (r.After < r.Total * .75 ? "\n결과가 낮아요. 마이크 입력을 확인하거나 더 부르기 쉬운 이름으로 다시 시험해 보세요." : "");
        }
        else result.Text = "완료한 뒤 보정 전후 결과를 확인하고 적용할 수 있어요.";
        RefreshButtons();
    }
    void BeginTake()
    {
        if (!ready || recording || finishing || refining || learning.Finished) return;
        finalText = finalWav = ""; hadWake = false; heard.Text = "";
        recording = pendingCapture = true; captured = DateTimeOffset.UtcNow;
        health.Begin(manual: true); input.Capture(true); status.Text = "입력을 준비하고 있어요."; RefreshButtons();
    }
    void FinishTake()
    {
        if (!recording || pendingCapture) return;
        recording = false; finishing = true; flushed = DateTimeOffset.UtcNow;
        input.EndUtterance(); status.Text = "마지막 음절까지 확인하고 있어요."; RefreshButtons();
    }
    public void CancelCapture()
    {
        if (closed) return;
        if (refining) { Close(); return; }
        recording = pendingCapture = finishing = false; finalText = finalWav = "";
        input.Capture(false); status.Text = "이번 회차를 취소했어요. 다시 시작할 수 있습니다."; heard.Text = ""; RefreshButtons();
    }
    void Fail(string text)
    {
        input.Stop(); ready = recording = pendingCapture = finishing = false;
        status.Text = text; meter.Value = 0; RefreshButtons();
    }
    void Tick()
    {
        if (closed) return;
        var now = DateTimeOffset.UtcNow;
        if (input.Running && !ready && now - started > TimeSpan.FromSeconds(30)) { Fail("마이크 준비 시간이 초과됐어요. 장치를 확인하고 다시 연결하세요."); return; }
        if (ready && now - lastFrame > TimeSpan.FromSeconds(8)) { Fail("마이크 입력이 끊겼어요. 장치를 확인하고 다시 연결하세요."); return; }
        if (finishing && now - flushed > TimeSpan.FromSeconds(8)) { Fail("발음 확인이 끝나지 않았어요. 다시 연결한 뒤 이 회차를 시도하세요."); return; }
        if (recording)
        {
            if (pendingCapture && now - captured > TimeSpan.FromSeconds(3)) { Fail("입력 시작 응답이 없어요. 마이크를 다시 연결하세요."); return; }
            health.Evaluate(!pendingCapture, manual: true);
            if (health.Quiet) status.Text = "소리가 거의 들어오지 않아요. 선택한 마이크와 헤드셋 음소거를 확인하세요.";
            if (now - captured > TimeSpan.FromSeconds(15)) FinishTake();
        }
    }
    void OnSpeech(JsonElement message)
    {
        var type = message.GetProperty("type").GetString();
        if (type == "ready") { ready = true; lastFrame = DateTimeOffset.UtcNow; status.Text = "준비됐어요. ‘말하기 시작’을 누른 뒤 위 문장을 읽어 주세요."; RefreshButtons(); }
        else if (type == "capture" && message.GetProperty("request").GetInt32() == input.CaptureRequest && message.GetProperty("enabled").GetBoolean() && recording)
        { pendingCapture = false; captured = DateTimeOffset.UtcNow; status.Text = "말씀하세요. 다 읽은 뒤 ‘다 말했어요’를 누르세요."; RefreshButtons(); }
        else if (type == "level")
        {
            lastFrame = DateTimeOffset.UtcNow; meter.Value = message.GetProperty("value").GetDouble();
            health.Observe(message.GetProperty("rms").GetDouble(), message.GetProperty("peak").GetDouble(), message.GetProperty("recognizing").GetBoolean());
        }
        else if (type == "partial" && recording && !pendingCapture) heard.Text = "들은 말: " + message.GetProperty("text").GetString();
        else if (type == "final" && (recording || finishing) && !pendingCapture)
        {
            finalText = message.GetProperty("text").GetString() ?? ""; hadWake = message.GetProperty("has_wake").GetBoolean();
            finalWav = message.TryGetProperty("wav", out var audio) ? audio.GetString() ?? "" : "";
        }
        else if (type == "flushed" && finishing) { finishing = false; _ = CompleteTake(); }
        else if (type == "error") Fail(message.GetProperty("message").GetString() ?? "마이크 오류");
        else if (type == "warning") status.Text = message.GetProperty("message").GetString() ?? "";
    }
    async Task CompleteTake()
    {
        refining = true; RefreshButtons(); var refined = ""; var notice = "";
        try
        {
            if (finalWav.Length > 0 && WhisperEngine.Enabled(preferences))
            {
                status.Text = "설정된 로컬 서버에서 발음을 확인하고 있어요.";
                try { refined = (await WhisperEngine.Shared.RefineAsync(preferences, finalWav, lifetime.Token)).Text; }
                catch (Exception ex) when (ex is not OperationCanceledException) { notice = "로컬 보정을 사용하지 못해 실시간 인식 결과로 확인했어요. "; }
            }
            if (closed) return;
            var accepted = learning.Add(finalText, refined, hadWake);
            status.Text = accepted ? notice + "이 회차를 확인했어요." : "읽을 수 있는 말이 없어서 횟수에 넣지 않았어요. 마이크 입력을 확인하고 다시 시도하세요.";
            heard.Text = finalText.Length > 0 ? "들은 말: " + finalText : "";
        }
        catch (OperationCanceledException) { }
        finally { finalText = finalWav = ""; refining = false; if (!closed) ShowProgress(); }
    }
    void Apply()
    {
        if (!learning.Finished || recording || finishing || refining) return;
        UiSettings next;
        try { next = learning.ApplyTo(preferences); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { status.Text = ex.Message; return; }
        var error = save(next);
        if (error != null) { status.Text = error; return; }
        try
        {
            var path = Path.Combine(AppPaths.Home, "name-training.json"); var temporary = path + ".new";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new { at = DateTimeOffset.Now, result = learning.Result(), takes = learning.Takes }, SettingsStore.Json));
            File.Move(temporary, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppPaths.Log("name-training", "Settings applied; result record could not be saved: " + ex.Message); }
        DialogResult = true;
    }
}

public partial class App
{
    NameTrainingWindow? nameTrainingWindow;
    void ShowNameTraining(string name, DeviceOption device) => WithInputPaused(() =>
    {
        var preferences = SettingsTransfer.Copy(settings); preferences.Name = name;
        var training = new NameTrainingWindow(preferences, device, SaveSettings) { Owner = window };
        nameTrainingWindow = training;
        try { training.ShowDialog(); }
        finally { nameTrainingWindow = null; }
    });
}
