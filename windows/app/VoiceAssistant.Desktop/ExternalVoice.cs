using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace VoiceAssistant.Desktop;

public sealed record VoicePreferences
{
    public string Provider { get; set; } = "windows";
    public string Voice { get; set; } = "";
    public string Model { get; set; } = "";
    public string Region { get; set; } = "";
    public string Url { get; set; } = "";
    public double Speed { get; set; } = 1;
    public bool Fallback { get; set; } = true;
    public void Validate()
    {
        if (Provider is not ("windows" or "pro" or "elevenlabs" or "openai" or "azure" or "http")) throw new ArgumentException("음성 제공자를 확인하세요.");
        foreach (var value in new[] { Voice, Model, Region, Url }) if (value == null || value.Length > 2000 || value.Any(char.IsControl)) throw new ArgumentException("음성 설정의 길이나 문자를 확인하세요.");
        if (Voice.Length > 200 || Model.Length > 200 || !double.IsFinite(Speed) || Speed < 0.25 || Speed > 4) throw new ArgumentException("음성 이름은 200자 이내, 외부 읽기 속도는 0.25~4배로 입력하세요.");
        if (Provider == "elevenlabs" && (Speed < 0.7 || Speed > 1.2)) throw new ArgumentException("ElevenLabs 읽기 속도는 0.7~1.2배입니다.");
        if (Provider == "azure" && (Speed < 0.5 || Speed > 2)) throw new ArgumentException("Azure 읽기 속도는 0.5~2배입니다.");
        if (Provider == "elevenlabs" && !Regex.IsMatch(Voice, @"^[a-zA-Z0-9_-]{1,200}$")) throw new ArgumentException("ElevenLabs의 Voice ID를 입력하세요.");
        if (Provider == "azure" && !Regex.IsMatch(Region, @"^[a-z0-9]{2,40}$")) throw new ArgumentException("Azure 리소스 지역을 입력하세요. 예: koreacentral");
        if (Provider == "http") _ = ExternalVoice.Endpoint(this);
        if (Provider == "pro" && Voice.Length > 0 && !ProAccountClient.Voices.Any(v => v.Id == Voice)) throw new ArgumentException("Pro 목소리 목록에서 선택하세요.");
    }
}
public sealed record SpeechPlaybackResult(string Provider, bool UsedFallback = false, string? Notice = null);
public sealed record VoiceAudio(byte[] Bytes, string Extension);

public static class SpeechPipeline
{
    public static async Task<SpeechPlaybackResult> RunAsync(string text, UiSettings settings, CancellationToken token,
        Func<string, VoicePreferences, CancellationToken, Task<VoiceAudio>> fetch,
        Func<VoiceAudio, UiSettings, CancellationToken, Task> play,
        Func<string, UiSettings, CancellationToken, Task> windows)
    {
        token.ThrowIfCancellationRequested();
        if (settings.Tts.Provider == "windows") { await windows(text, settings, token); token.ThrowIfCancellationRequested(); return new("windows"); }
        try
        {
            var audio = await fetch(text, settings.Tts, token); token.ThrowIfCancellationRequested();
            await play(audio, settings, token); token.ThrowIfCancellationRequested(); return new(settings.Tts.Provider);
        }
        catch (Exception ex) when (!token.IsCancellationRequested && (settings.Tts.Fallback || settings.Tts.Provider == "pro"))
        {
            token.ThrowIfCancellationRequested();
            var reason = ex is InvalidOperationException or TimeoutException or ArgumentException ? ex.Message : "외부 음성 응답을 처리하지 못했어요.";
            AppPaths.Log("tts-fallback", settings.Tts.Provider + ": " + reason);
            await windows(text, settings, token); token.ThrowIfCancellationRequested();
            return new("windows", true, reason + " Windows 기본 음성으로 읽었어요.");
        }
    }
}

public static class ExternalVoice
{
    public const int MaxBytes = 16 * 1024 * 1024;
    public static string Name(string id) => id switch { "pro" => "Sasarisa Pro", "elevenlabs" => "ElevenLabs", "openai" => "OpenAI", "azure" => "Azure Speech", "http" => "사용자 음성 서버", _ => "Windows 기본 음성" };
    public static Uri Endpoint(VoicePreferences options)
    {
        if (options.Provider == "openai") return new("https://api.openai.com/v1/audio/speech");
        if (options.Provider == "elevenlabs") return new("https://api.elevenlabs.io/v1/text-to-speech/" + Uri.EscapeDataString(options.Voice) + "?output_format=mp3_44100_128");
        if (options.Provider == "azure")
        {
            if (!Regex.IsMatch(options.Region, @"^[a-z0-9]{2,40}$")) throw new ArgumentException("Azure 리소스 지역을 확인하세요.");
            return new("https://" + options.Region + ".tts.speech.microsoft.com/cognitiveservices/v1");
        }
        if (options.Provider != "http" || !Uri.TryCreate(options.Url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.Scheme == "http" && !uri.IsLoopback || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("음성 서버는 HTTPS 또는 이 PC의 HTTP 주소를 입력하세요. 키는 주소에 넣지 마세요.");
        return uri;
    }
    public static string CredentialTarget(VoicePreferences options)
    {
        // ElevenLabs keys belong to the service, not an individual voice. Custom
        // servers and Azure resources remain scoped to their exact destination.
        var destination = options.Provider == "elevenlabs" ? "https://api.elevenlabs.io/v1/text-to-speech/" : Endpoint(options).AbsoluteUri;
        return "VoiceAssistantWindows:api:tts:" + options.Provider + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(destination)));
    }
    public static HttpRequestMessage Request(string text, VoicePreferences options, string? key)
    {
        options.Validate();
        if (text.Length is < 1 or > 1500) throw new ArgumentException("한 번의 음성 출력은 1~1,500자로 나눠 요청하세요.");
        if (key != null && (key.Length > 1280 || key.Any(char.IsControl))) throw new ArgumentException("음성 API 키는 한 줄로 입력하세요.");
        if (options.Provider != "http" && string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException(Name(options.Provider) + " 음성 API 키를 설정에서 저장하세요.");
        var request = new HttpRequestMessage(HttpMethod.Post, Endpoint(options));
        object body;
        switch (options.Provider)
        {
            case "openai":
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                body = new { model = options.Model.Length == 0 ? "gpt-4o-mini-tts" : options.Model, voice = options.Voice.Length == 0 ? "alloy" : options.Voice, input = text, response_format = "wav", speed = options.Speed }; break;
            case "elevenlabs":
                request.Headers.Add("xi-api-key", key);
                var model = options.Model.Length == 0 ? "eleven_flash_v2_5" : options.Model;
                var fields = new Dictionary<string, object> { ["text"] = text, ["model_id"] = model, ["voice_settings"] = new { stability = 0.45, similarity_boost = 0.8, style = 0.3, speed = options.Speed } };
                if (model.Contains("v2_5", StringComparison.Ordinal)) fields["language_code"] = "ko";
                body = fields; break;
            case "azure":
                request.Headers.Add("Ocp-Apim-Subscription-Key", key);
                request.Headers.Add("X-Microsoft-OutputFormat", "riff-24khz-16bit-mono-pcm"); request.Headers.UserAgent.ParseAdd("SasarisaWindows/0.2");
                XNamespace ns = "http://www.w3.org/2001/10/synthesis";
                var xml = new XElement(ns + "speak", new XAttribute("version", "1.0"), new XAttribute(XNamespace.Xml + "lang", "ko-KR"),
                    new XElement(ns + "voice", new XAttribute("name", options.Voice.Length == 0 ? "ko-KR-SunHiNeural" : options.Voice),
                        new XElement(ns + "prosody", new XAttribute("rate", ((options.Speed - 1) * 100).ToString("+0.##;-0.##;0", CultureInfo.InvariantCulture) + "%"), text)));
                request.Content = new StringContent(xml.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "application/ssml+xml"); return request;
            default:
                if (!string.IsNullOrWhiteSpace(key)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                body = new { text, voice = options.Voice, language = "ko" }; break;
        }
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"); return request;
    }
    public static HttpClient CreateClient() => new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    public static async Task<VoiceAudio> FetchAsync(string text, VoicePreferences options, CancellationToken token,
        HttpClient? client = null, Func<string, string?>? readKey = null, TimeSpan? timeout = null)
    {
        options.Validate(); token.ThrowIfCancellationRequested();
        if (options.Provider == "pro") return await ProAccountClient.Shared.FetchVoiceAsync(text, options, token);
        var key = (readKey ?? CredentialStore.Read)(CredentialTarget(options));
        using var request = Request(text, options, key);
        using var ownClient = client == null ? CreateClient() : null; var http = client ?? ownClient!;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(timeout ?? TimeSpan.FromSeconds(20));
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException(Name(options.Provider) + " 음성 요청에 실패했어요 (HTTP " + (int)response.StatusCode + "). 키·사용 한도·설정을 확인하세요.");
            return await ReadAudioAsync(response, limit.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException("외부 음성 생성 시간이 초과됐어요."); }
        catch (HttpRequestException) { throw new InvalidOperationException("외부 음성 서버에 연결하지 못했어요. 주소와 네트워크를 확인하세요."); }
    }
    internal static async Task<VoiceAudio> ReadAudioAsync(HttpResponseMessage response, CancellationToken token)
    {
            if (response.Content.Headers.ContentLength > MaxBytes) throw new InvalidOperationException("음성 응답이 16 MB를 넘었어요.");
            var media = response.Content.Headers.ContentType?.MediaType;
            if (media != null && !media.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) && media != "application/octet-stream") throw new InvalidOperationException("서버가 음성 파일을 반환하지 않았어요.");
            await using var stream = await response.Content.ReadAsStreamAsync(token); using var buffer = new MemoryStream(); var chunk = new byte[16384];
            while (true) { var count = await stream.ReadAsync(chunk, token); if (count == 0) break; if (buffer.Length + count > MaxBytes) throw new InvalidOperationException("음성 응답이 16 MB를 넘었어요."); await buffer.WriteAsync(chunk.AsMemory(0, count), token); }
            token.ThrowIfCancellationRequested(); var data = buffer.ToArray();
            var wav = data.Length >= 44 && Encoding.ASCII.GetString(data, 0, 4) == "RIFF" && Encoding.ASCII.GetString(data, 8, 4) == "WAVE";
            var mp3 = data.Length >= 4 && (Encoding.ASCII.GetString(data, 0, 3) == "ID3" || data[0] == 0xff && (data[1] & 0xe0) == 0xe0);
            if (!wav && !mp3) throw new InvalidOperationException("음성 파일을 확인하지 못했어요. WAV 또는 MP3 응답이 필요합니다.");
            return new(data, wav ? ".wav" : ".mp3");
    }
}
