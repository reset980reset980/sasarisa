using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoiceAssistant.Desktop;

public sealed record SettingsPackage(string Format, int Version, UiSettings Settings);
public static class SettingsTransfer
{
    const string Format = "Sasarisa.Windows.Settings";
    public const int MaxBytes = 512 * 1024;
    static readonly JsonSerializerOptions Json = new(SettingsStore.Json) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 16 };
    public static UiSettings Copy(UiSettings settings) => JsonSerializer.Deserialize<UiSettings>(JsonSerializer.Serialize(settings), Json)!;
    static void CheckTree(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null) throw new ArgumentException("빈 설정 값이 있어요. 이 앱에서 내보낸 설정 파일을 선택하세요.");
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject()) { if (!names.Add(property.Name)) throw new ArgumentException("중복된 설정 이름이 있어요."); CheckTree(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) CheckTree(item);
    }
    static void CheckUrl(string url)
    {
        if (url.Length == 0) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var address) || address.Scheme is not ("https" or "http") || address.UserInfo.Length > 0 || address.Query.Length > 0 || address.Fragment.Length > 0)
            throw new ArgumentException("AI 서버 주소에 로그인 정보나 쿼리를 넣을 수 없어요.");
    }
    static void Validate(UiSettings settings)
    {
        CheckTree(JsonSerializer.SerializeToElement(settings)); SettingsStore.Validate(settings);
        if (settings.Name.Any(char.IsControl) || settings.BrainModel.Length > 300 || settings.ProjectDirectory.Length > 4000 || settings.ProviderProfiles.Count > 16) throw new ArgumentException("설정 값의 길이나 개수를 확인하세요.");
        CheckUrl(settings.BrainUrl); CheckUrl(settings.WhisperUrl);
        CheckUrl(settings.Tts.Url);
        foreach (var profile in settings.ProviderProfiles)
        {
            if (profile.Key is not ("claude" or "codex" or "ollama" or "aside" or "combo") && ApiProviders.Find(profile.Key) == null) throw new ArgumentException("지원하지 않는 AI 연결 설정이 있어요.");
            if (profile.Value.Model.Length > 300) throw new ArgumentException("AI 모델 이름이 너무 길어요.");
            CheckUrl(profile.Value.Url);
            if (profile.Key == "ollama" && profile.Value.Url.Length > 0) SettingsStore.ValidateLocalUrl(profile.Value.Url);
            if (ApiProviders.Find(profile.Key) != null && profile.Value.Url.Length > 0) ApiProviders.Endpoint(profile.Key, profile.Value.Url);
        }
    }
    public static string Serialize(UiSettings settings)
    {
        var copy = Copy(settings); Validate(copy); copy.Text.WordCandidates = new();
        var text = JsonSerializer.Serialize(new SettingsPackage(Format, 1, copy), Json);
        if (Encoding.UTF8.GetByteCount(text) > MaxBytes) throw new ArgumentException("설정 파일은 512 KB 이내여야 합니다.");
        return text;
    }
    public static UiSettings Parse(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > MaxBytes) throw new ArgumentException("설정 파일은 512 KB 이내여야 합니다.");
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 }); CheckTree(document.RootElement);
            var package = JsonSerializer.Deserialize<SettingsPackage>(text, Json);
            if (package == null || package.Format != Format || package.Version != 1 || package.Settings == null) throw new ArgumentException("이 버전의 Windows 비서에서 내보낸 설정 파일이 아니에요.");
            Validate(package.Settings); return package.Settings;
        }
        catch (JsonException) { throw new ArgumentException("설정 파일 형식이 올바르지 않아요. Windows 비서에서 내보낸 파일을 선택하세요."); }
    }
    public static UiSettings Read(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > MaxBytes) throw new ArgumentException("설정 파일은 512 KB 이내여야 합니다.");
        using var reader = new StreamReader(file, new UTF8Encoding(false, true)); return Parse(reader.ReadToEnd());
    }
    public static void Write(string path, UiSettings settings)
    {
        var text = Serialize(settings); var fullPath = Path.GetFullPath(path); var temp = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, text, new UTF8Encoding(false)); if (File.Exists(fullPath)) File.Replace(temp, fullPath, fullPath + ".previous"); else File.Move(temp, fullPath); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static UiSettings Prepare(UiSettings imported, UiSettings current, bool connections, bool devices)
    {
        var next = Copy(imported); next.Desktop.SetupComplete = current.Desktop.SetupComplete;
        next.Text.WordCandidates = new(current.Text.WordCandidates);
        // Importing someone else's preferences must not grant access from a phone.
        next.ProRemote = current.ProRemote;
        next.GrowTogether = current.GrowTogether;
        if (!connections)
        {
            next.BrainProvider = current.BrainProvider; next.BrainModel = current.BrainModel; next.BrainUrl = current.BrainUrl;
            next.ProviderProfiles = new(current.ProviderProfiles); next.WhisperUrl = current.WhisperUrl; next.ProjectDirectory = current.ProjectDirectory;
            next.Tts = current.Tts with { };
        }
        if (!devices) { next.Device = current.Device; next.VoiceId = current.VoiceId; next.OutputId = current.OutputId; next.Desktop.OverlayScreen = current.Desktop.OverlayScreen; next.Desktop.StartupListening = current.Desktop.StartupListening; }
        Validate(next); return next;
    }
    public static string Describe(UiSettings before, UiSettings after)
    {
        var lines = new List<string>();
        string Display(object? value) => value switch { bool flag => flag ? "켜짐" : "꺼짐", string[] words => words.Length == 0 ? "비어 있음" : string.Join(" / ", words), string str => str.Length == 0 ? "기본값 / 비어 있음" : str, _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "" };
        void Item(string label, object? oldValue, object? newValue) { var oldText = Display(oldValue); var newText = Display(newValue); if (oldText != newText) lines.Add($"{label}\n  {oldText}\n  → {newText}"); }
        Item("호출 이름", before.Name, after.Name); Item("호출 방식", Mode(before.Activation), Mode(after.Activation)); Item("입력 마이크", before.Device, after.Device);
        string Startup(string mode) => mode switch { "always" => "항상 듣기 시작", "off" => "항상 듣기 중지", _ => "마지막 듣기 상태 복원" };
        Item("앱 실행 시 듣기", Startup(before.Desktop.StartupListening), Startup(after.Desktop.StartupListening));
        foreach (var name in before.LearnedWakes.Keys.Union(after.LearnedWakes.Keys).Order())
            Item("이름 발음 보정 · " + name, LearnedNames.For(before, name), LearnedNames.For(after, name));
        Item("말 끝 대기 (초)", before.SilenceSeconds, after.SilenceSeconds); Item("후속 대화 (초)", before.FollowUpSeconds, after.FollowUpSeconds);
        Item("처리 스레드", before.Threads, after.Threads); Item("빠른 호출어 감지", before.Kws, after.Kws); Item("호출어 감지 기준", before.KeywordThreshold, after.KeywordThreshold);
        Item("다른 앱 마이크 사용 시 자동 대기", before.PauseInCalls, after.PauseInCalls);
        Item("답변 중 끼어들기", before.BargeIn, after.BargeIn);
        Item("유튜브 재생 방식", before.YouTubePlayer, after.YouTubePlayer);
        Item("답변 읽기", before.Speak, after.Speak); Item("목소리", before.VoiceId, after.VoiceId); Item("출력 장치", before.OutputId, after.OutputId); Item("읽기 속도", before.SpeechRate, after.SpeechRate); Item("답변 음량", before.SpeechVolume, after.SpeechVolume);
        Item("음성 제공자", ExternalVoice.Name(before.Tts.Provider), ExternalVoice.Name(after.Tts.Provider)); Item("외부 목소리", before.Tts.Voice, after.Tts.Voice); Item("음성 모델", before.Tts.Model, after.Tts.Model); Item("음성 지역", before.Tts.Region, after.Tts.Region); Item("음성 서버", before.Tts.Url, after.Tts.Url); Item("외부 읽기 속도", before.Tts.Speed, after.Tts.Speed); Item("음성 실패 시 기본 목소리", before.Tts.Fallback, after.Tts.Fallback);
        Item("AI 제공자", Brain.NameFor(before.BrainProvider), Brain.NameFor(after.BrainProvider)); Item("AI 모델", before.BrainModel, after.BrainModel); Item("AI 서버 주소", before.BrainUrl, after.BrainUrl); Item("Whisper 서버", before.WhisperUrl, after.WhisperUrl); Item("코딩 프로젝트 폴더", before.ProjectDirectory, after.ProjectDirectory);
        foreach (var id in before.ProviderProfiles.Keys.Union(after.ProviderProfiles.Keys).Order())
        { before.ProviderProfiles.TryGetValue(id, out var oldProfile); after.ProviderProfiles.TryGetValue(id, out var newProfile); Item(Brain.NameFor(id) + " 저장 모델", oldProfile?.Model ?? "", newProfile?.Model ?? ""); Item(Brain.NameFor(id) + " 저장 주소", oldProfile?.Url ?? "", newProfile?.Url ?? ""); }
        var a = before.Desktop; var b = after.Desktop;
        Item("사용자 호칭", a.UserName, b.UserName); Item("말하기 단축키", a.WakeHotkey, b.WakeHotkey); Item("중지 단축키", a.StopHotkey, b.StopHotkey);
        Item("알림 위치", Position(a.OverlayPosition), Position(b.OverlayPosition)); Item("알림 화면", a.OverlayScreen, b.OverlayScreen); Item("작은 대기 표시", a.ShowIdleOrb, b.ShowIdleOrb);
        Item("인사 화면", Style(a.GreetingStyle), Style(b.GreetingStyle)); Item("인사말", a.GreetingText, b.GreetingText); Item("인사 제목", a.GreetingTitle, b.GreetingTitle); Item("인사 부제", a.GreetingSubtitle, b.GreetingSubtitle); Item("인사 호출 문장", a.GreetingPhrases, b.GreetingPhrases); Item("인사 웹사이트", a.GreetingUrls, b.GreetingUrls); Item("인사 앱", a.GreetingApps, b.GreetingApps);
        Item("자주 쓰는 단어", before.Text.CustomWords, after.Text.CustomWords); Item("말버릇 제거", before.Text.RemoveFillers, after.Text.RemoveFillers); Item("제거할 말버릇", before.Text.FillerWords, after.Text.FillerWords); Item("AI 받아쓰기 다듬기", before.Text.PolishDictation, after.Text.PolishDictation);
        Item("반복 보정 단어 자동 학습", before.Text.AutoLearnWords, after.Text.AutoLearnWords);
        return lines.Count == 0 ? "바뀌는 설정이 없습니다." : string.Join("\n\n", lines);
    }
    static string Mode(string id) => id == "voice" ? "이름 호출 또는 단축키" : id == "hold" ? "누르는 동안 듣기" : "단축키로 호출";
    static string Style(string id) => id == "reactor" ? "원형" : id == "calm" ? "차분한 화면" : "작은 알림";
    static string Position(string id) => id switch { "center" => "가운데", "bottomCenter" => "아래 가운데", "bottomLeft" => "아래 왼쪽", "top" => "위쪽", _ => "아래 오른쪽" };
}
