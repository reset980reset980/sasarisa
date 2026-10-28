using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace VoiceAssistant.Desktop;

public sealed record MacSettingsImport(UiSettings Settings, string[] Notices);

public static class MacSettingsTransfer
{
    static readonly HashSet<string> Known = new(("language activation userName assistantName wakeWords learnedWakes greetPhrases greetingText bootTitle bootStyle bootSubtitle greetOpen " +
        "brainProvider brainModel brainBaseURL brainEffort claudePath claudeModel confirmDelegate freeMode freeModeAsked polishDictation previewBeforeSend proRemote growTogether brainAccess whisperModel whisperServer " +
        "tts ttsVoice ttsModel ttsRegion ttsURL sayVoice sayRate elevenLabs inputDevice outputDevice bargeIn browserApp apps customWords fillerWords silenceSec followUpSec maxUtteranceSec chime pauseInCalls router keepAudio showIdleOrb hudPosition hotkey").Split(' '), StringComparer.Ordinal);
    static ArgumentException Invalid(string field) => new("macOS 설정의 ‘" + field + "’ 값 형식을 확인하세요.");
    public static MacSettingsImport Read(string path, UiSettings current)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > SettingsTransfer.MaxBytes) throw new ArgumentException("설정 파일은 512 KB 이내여야 합니다.");
        try { using var reader = new StreamReader(file, new UTF8Encoding(false, true)); return Parse(reader.ReadToEnd(), current); }
        catch (DecoderFallbackException) { throw new ArgumentException("macOS 설정은 UTF-8 JSON 파일로 선택하세요."); }
    }
    public static MacSettingsImport Parse(string text, UiSettings current)
    {
        if (Encoding.UTF8.GetByteCount(text) > SettingsTransfer.MaxBytes) throw new ArgumentException("설정 파일은 512 KB 이내여야 합니다.");
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 }); var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new ArgumentException("원본 macOS 비서의 config.json을 선택하세요.");
            SettingsTransfer.CheckTree(root);
            if (!root.EnumerateObject().Any(p => Known.Contains(p.Name)) || root.TryGetProperty("Format", out _)) throw new ArgumentException("원본 macOS 비서의 config.json을 선택하세요.");
            var next = SettingsTransfer.Copy(current);
            var notes = new List<string> { "파일에 없는 항목은 현재 Windows 설정을 유지합니다.", "Mac의 장치·단축키·앱 실행 경로·계정 키는 가져오지 않습니다. Windows에서 선택한 값을 유지합니다." };
            string Text(JsonElement owner, string key, string fallback = "")
            { if (!owner.TryGetProperty(key, out var value)) return fallback; if (value.ValueKind != JsonValueKind.String) throw Invalid(key); return value.GetString()!; }
            string[] Words(string key)
            {
                var value = root.GetProperty(key); if (value.ValueKind != JsonValueKind.Array) throw Invalid(key);
                return value.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : throw Invalid(key)).Distinct(StringComparer.Ordinal).ToArray();
            }
            void String(string key, Action<string> set) { if (root.TryGetProperty(key, out _)) set(Text(root, key)); }
            void Flag(string key, Action<bool> set)
            { if (!root.TryGetProperty(key, out var v)) return; if (v.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Invalid(key); set(v.GetBoolean()); }
            void Number(string key, Action<double> set)
            { if (!root.TryGetProperty(key, out var v)) return; if (v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out var number) || !double.IsFinite(number)) throw Invalid(key); set(number); }
            bool Any(params string[] keys) => keys.Any(key => root.TryGetProperty(key, out _));
            String("assistantName", v => next.Name = v.Trim()); String("activation", v => next.Activation = v);
            String("userName", v => next.Desktop.UserName = v); String("greetingText", v => next.Desktop.GreetingText = v);
            String("bootTitle", v => next.Desktop.GreetingTitle = v == "M A C   B U T L E R" ? "" : v);
            String("bootSubtitle", v => next.Desktop.GreetingSubtitle = v); String("bootStyle", v => next.Desktop.GreetingStyle = v);
            String("hudPosition", v => next.Desktop.OverlayPosition = v); Flag("showIdleOrb", v => next.Desktop.ShowIdleOrb = v);
            Flag("pauseInCalls", v => next.PauseInCalls = v); String("bargeIn", v => next.BargeIn = v);
            Number("silenceSec", v => next.SilenceSeconds = v); Number("followUpSec", v => next.FollowUpSeconds = v);
            if (Any("greetPhrases")) next.Desktop.GreetingPhrases = Words("greetPhrases");
            if (Any("greetOpen")) next.Desktop.GreetingUrls = Words("greetOpen");
            if (root.EnumerateObject().Any(p => p.Name is "greetingText" or "bootTitle" or "bootSubtitle" && p.Value.ValueKind == JsonValueKind.String && p.Value.GetString()!.Length == 0) || Any("greetPhrases") && next.Desktop.GreetingPhrases.Length == 0 || next.Desktop.GreetingTitle.Length == 0 && Text(root, "bootTitle") == "M A C   B U T L E R")
                notes.Add("비어 있는 인사 문장·화면 제목·부제는 Windows 기본값을 사용합니다.");
            if (Any("customWords")) next.Text.CustomWords = Words("customWords");
            var language = Text(root, "language", "ko");
            if (Any("fillerWords"))
            {
                var words = Words("fillerWords"); next.Text.FillerWords = words.Length > 0 ? words : language.StartsWith("ko", StringComparison.Ordinal) ? ["음", "으음", "어어", "음음"] : ["um", "uh", "erm"];
                next.Text.RemoveFillers = true;
            }
            Flag("polishDictation", v => next.Text.PolishDictation = v);
            if (next.Text.PolishDictation && Any("polishDictation")) notes.Add("받아쓰기 다듬기는 Apple Intelligence 대신 Windows에서 선택한 AI 연결을 사용합니다.");
            if (!language.StartsWith("ko", StringComparison.Ordinal)) notes.Add("macOS의 언어 선택은 가져오지 않습니다. 현재 Windows 인식 모델은 한국어입니다.");
            if (Any("learnedWakes"))
            {
                var value = root.GetProperty("learnedWakes"); if (value.ValueKind != JsonValueKind.Object) throw Invalid("learnedWakes");
                next.LearnedWakes = JsonSerializer.Deserialize<Dictionary<string, string[]>>(value.GetRawText()) ?? throw Invalid("learnedWakes");
            }
            var aliases = LearnedNames.For(next).ToList();
            var wakes = Any("wakeWords") ? Words("wakeWords") : Array.Empty<string>();
            if (wakes.Length == 0 && Any("assistantName", "wakeWords") && language.StartsWith("ko", StringComparison.Ordinal) && next.Name != "자비스") aliases.Add("자비스");
            if (wakes.Length > 0)
            {
                var usable = wakes.Where(w => NameLearning.Usable(w, 2)).ToArray(); aliases.AddRange(usable);
                notes.Add("별도 호출 문장은 이름 발음 보정으로 합칩니다. Windows에서는 설정한 호출 이름도 계속 사용할 수 있습니다.");
                if (usable.Length != wakes.Length) notes.Add("Windows 발음 보정 범위를 벗어난 호출 문장 " + (wakes.Length - usable.Length) + "개는 제외했습니다.");
            }
            if (aliases.Count > 0) next.LearnedWakes[next.Name] = aliases.Distinct(StringComparer.Ordinal).ToArray();
            if (Any("brainProvider", "brainModel", "brainBaseURL", "claudeModel"))
            {
                var provider = Text(root, "brainProvider", current.BrainProvider) switch { "claude-code" => "claude", "openai-compatible" => "compatible", var id => id };
                if (provider is not ("claude" or "codex" or "aside" or "combo" or "ollama") && ApiProviders.Find(provider) == null) throw Invalid("brainProvider");
                var model = Text(root, "brainModel", provider == current.BrainProvider ? current.BrainModel : "");
                if (provider == "claude" && Any("claudeModel")) model = Text(root, "claudeModel");
                var url = Text(root, "brainBaseURL", ApiProviders.DefaultUrl(provider)); if (url.Length == 0) url = ApiProviders.DefaultUrl(provider);
                if (url.Length > 0) _ = ApiProviders.Endpoint("compatible", url); // Validate before any review can show a URL.
                if (ApiProviders.Find(provider) is { EditableUrl: false } api && url.TrimEnd('/') != api.BaseUrl.TrimEnd('/'))
                { notes.Add("선택한 API 제공자는 Windows의 공식 서버 주소를 사용합니다. Mac의 사용자 지정 주소는 제외했습니다."); url = api.BaseUrl; }
                next.BrainProvider = provider; next.BrainModel = model; next.BrainUrl = url;
                next.ProviderProfiles[provider] = new(model, url);
                if (provider != "claude" && Any("claudeModel")) next.ProviderProfiles["claude"] = new(Text(root, "claudeModel"), "");
            }
            if (Any("tts"))
            {
                var provider = Text(root, "tts") is "say" ? "windows" : Text(root, "tts");
                var voice = new VoicePreferences { Provider = provider };
                if (provider == "elevenlabs")
                {
                    if (!root.TryGetProperty("elevenLabs", out var eleven) || eleven.ValueKind != JsonValueKind.Object) throw Invalid("elevenLabs");
                    voice.Voice = Text(eleven, "voiceId"); voice.Model = Text(eleven, "model", "eleven_flash_v2_5");
                    if (eleven.TryGetProperty("speed", out var speed)) { if (speed.ValueKind != JsonValueKind.Number || !speed.TryGetDouble(out var n) || !double.IsFinite(n)) throw Invalid("elevenLabs.speed"); voice.Speed = n; }
                    notes.Add("ElevenLabs의 API 키와 키 파일 경로는 제외했습니다. 필요한 키는 Windows에서 직접 설정하세요.");
                }
                else if (provider != "windows")
                {
                    voice.Voice = Text(root, "ttsVoice");
                    if (provider == "openai") voice.Model = Text(root, "ttsModel");
                    if (provider == "azure") voice.Region = Text(root, "ttsRegion");
                    if (provider == "http") voice.Url = Text(root, "ttsURL");
                }
                next.Tts = voice;
            }
            else if (Any("ttsVoice", "ttsModel", "ttsRegion", "ttsURL", "elevenLabs")) notes.Add("음성 제공자 항목이 없어 외부 목소리 설정은 현재 Windows 값을 유지합니다.");
            if (Any("whisperModel", "whisperServer")) notes.Add("Mac의 Whisper 모델·실행 경로는 제외했습니다. 현재 Windows 보정 엔진 선택을 유지합니다.");
            if (Any("freeMode", "freeModeAsked", "brainAccess", "confirmDelegate", "previewBeforeSend", "brainEffort")) notes.Add("작업 권한·자유 모드·위임 확인·사고 강도는 현재 Windows 동작을 유지합니다.");
            if (Any("proRemote", "growTogether")) notes.Add("휴대폰 원격 접근과 표현 공유 참여 여부는 현재 Windows 선택을 유지합니다.");
            if (Any("sayVoice", "sayRate", "browserApp", "apps")) notes.Add("Apple 목소리·읽기 속도·Mac 브라우저·앱 별칭은 제외했습니다. Windows에서 다시 선택하세요.");
            if (Any("maxUtteranceSec", "chime", "router", "keepAudio")) notes.Add("연속 발화 길이·알림음·빠른 분류기·음성 보관 설정은 현재 Windows 동작을 유지합니다.");
            var unknown = root.EnumerateObject().Count(p => !Known.Contains(p.Name)); if (unknown > 0) notes.Add("현재 변환기에서 알 수 없는 항목 " + unknown + "개는 제외했습니다.");
            SettingsTransfer.Validate(next);
            return new(next, notes.ToArray());
        }
        catch (JsonException) { throw new ArgumentException("macOS 설정 JSON 형식이 올바르지 않아요. 원본 config.json을 선택하세요."); }
    }
}
