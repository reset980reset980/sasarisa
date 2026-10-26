using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using NAudio.Wave;

namespace VoiceAssistant.Desktop;

internal static class VoiceChecks
{
    sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token); }
    sealed class NonSeekable(byte[] data) : MemoryStream(data) { public override bool CanSeek => false; }
    static void Require(bool condition, string why) { if (!condition) throw new Exception(why); }
    static void Reject(Action action) { try { action(); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return; } throw new Exception("Unexpected acceptance"); }
    public static byte[] Silence(int milliseconds = 160)
    {
        using var bytes = new MemoryStream(); using var writer = new BinaryWriter(bytes);
        var count = 24000 * milliseconds / 1000 * 2;
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(count + 36); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(24000); writer.Write(48000); writer.Write((short)2); writer.Write((short)16); writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(count); writer.Write(new byte[count]); return bytes.ToArray();
    }
    static HttpResponseMessage Audio(byte[] data, string media = "audio/wav") { var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) }; response.Content.Headers.ContentType = new(media); return response; }
    public static async Task<int> RunAsync(List<object> rows, bool devices)
    {
        int failed = 0;
        async Task Check(string name, Func<Task> check) { try { await check(); rows.Add(new { name, pass = true }); } catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.Message }); } }
        var local = new VoicePreferences { Provider = "http", Url = "http://127.0.0.1:5002/tts" };
        await Check("Voice settings retain old Windows defaults and validate provider limits", () =>
        {
            var old = JsonSerializer.Deserialize<UiSettings>("{}")!; SettingsStore.Validate(old); Require(old.Tts.Provider == "windows" && old.Tts.Fallback, "Old settings changed voice behavior");
            foreach (var prefs in new[] { new VoicePreferences { Provider = "bad" }, local with { Speed = double.NaN }, new VoicePreferences { Provider = "elevenlabs", Voice = "abc/def" }, new VoicePreferences { Provider = "elevenlabs", Voice = "abc", Speed = 1.3 }, new VoicePreferences { Provider = "azure", Region = "korea.central/../x" }, new VoicePreferences { Provider = "azure", Region = "koreacentral", Speed = 0.4 } }) Reject(prefs.Validate);
            var dialog = new VoiceSettingsWindow(old); dialog.Close(); return Task.CompletedTask;
        });
        await Check("Voice custom endpoints and credential scopes reject unsafe destinations", () =>
        {
            foreach (var address in new[] { "http://example.org/tts", "https://user:secret@example.org/tts", "https://example.org/tts?key=x", "https://example.org/tts#x", "file:///C:/tts", "http://localhost.example.org/tts" }) Reject(() => ExternalVoice.Endpoint(local with { Url = address }));
            foreach (var address in new[] { "https://example.org/tts", "http://localhost:5002/tts", "http://[::1]:5002/tts" }) _ = ExternalVoice.Endpoint(local with { Url = address });
            Require(ExternalVoice.CredentialTarget(local) != ExternalVoice.CredentialTarget(local with { Url = "http://127.0.0.1:5002/other" }), "Custom paths share a key");
            Require(ExternalVoice.CredentialTarget(new() { Provider = "openai" }) != ApiProviders.CredentialTarget("openai", ""), "Voice reused an unrelated AI credential");
            Require(ExternalVoice.CredentialTarget(new() { Provider = "elevenlabs", Voice = "first" }) == ExternalVoice.CredentialTarget(new() { Provider = "elevenlabs", Voice = "second" }), "Voice selection lost service key");
            return Task.CompletedTask;
        });
        await Check("OpenAI voice request preserves model text speed and authorization", async () =>
        {
            using var request = ExternalVoice.Request("<안녕> & hello", new() { Provider = "openai", Speed = 1.2 }, "fixture-key");
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()); var body = json.RootElement;
            Require(request.RequestUri!.AbsoluteUri == "https://api.openai.com/v1/audio/speech" && request.Headers.Authorization?.Parameter == "fixture-key", "Destination or authorization changed");
            Require(body.GetProperty("model").GetString() == "gpt-4o-mini-tts" && body.GetProperty("voice").GetString() == "alloy" && body.GetProperty("response_format").GetString() == "wav", "Speech API defaults changed");
            Require(body.GetProperty("input").GetString() == "<안녕> & hello" && body.GetProperty("speed").GetDouble() == 1.2, "Text or speed changed");
            Reject(() => ExternalVoice.Request("hello", new() { Provider = "openai" }, null));
        });
        await Check("ElevenLabs request keeps voice model language and rate settings", async () =>
        {
            using var request = ExternalVoice.Request("안녕하세요", new() { Provider = "elevenlabs", Voice = "voice_123", Speed = 0.8 }, "fixture-key");
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()); var body = json.RootElement;
            Require(request.RequestUri!.AbsolutePath.EndsWith("/voice_123") && request.RequestUri.Query == "?output_format=mp3_44100_128" && request.Headers.GetValues("xi-api-key").Single() == "fixture-key", "ElevenLabs request destination changed");
            Require(body.GetProperty("model_id").GetString() == "eleven_flash_v2_5" && body.GetProperty("language_code").GetString() == "ko" && body.GetProperty("voice_settings").GetProperty("speed").GetDouble() == 0.8, "ElevenLabs model options changed");
            using var newer = ExternalVoice.Request("hello", new() { Provider = "elevenlabs", Voice = "voice_123", Model = "eleven_v3" }, "fixture-key");
            Require(!(await newer.Content!.ReadAsStringAsync()).Contains("language_code"), "Unsupported model got forced language");
        });
        await Check("Azure SSML escapes text and voice attributes and selects its resource", async () =>
        {
            var phrase = "<audio src='https://example.org'> & 말하기"; var voiceName = "test'\"><break/>";
            using var request = ExternalVoice.Request(phrase, new() { Provider = "azure", Region = "koreacentral", Voice = voiceName, Speed = 1.2 }, "fixture-key");
            var xml = XDocument.Parse(await request.Content!.ReadAsStringAsync()); XNamespace ns = "http://www.w3.org/2001/10/synthesis";
            Require(request.RequestUri!.Host == "koreacentral.tts.speech.microsoft.com" && request.Headers.GetValues("X-Microsoft-OutputFormat").Single() == "riff-24khz-16bit-mono-pcm", "Azure resource or output changed");
            Require(xml.Descendants(ns + "voice").Single().Attribute("name")!.Value == voiceName && xml.Descendants(ns + "prosody").Single().Value == phrase && xml.Descendants(ns + "prosody").Single().Attribute("rate")!.Value == "+20%", "SSML text, voice, or rate changed");
            Require(!xml.Descendants().Any(x => x.Name.LocalName is "audio" or "break"), "Text injected SSML");
        });
        await Check("Custom voice server protocol remains text voice language with optional key", async () =>
        {
            using var request = ExternalVoice.Request("시험", local with { Voice = "local-ko" }, null); using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Require(request.RequestUri!.AbsolutePath == "/tts" && request.Headers.Authorization == null && json.RootElement.EnumerateObject().Count() == 3 && json.RootElement.GetProperty("language").GetString() == "ko", "Custom protocol changed");
        });
        await Check("Voice download accepts WAV and MP3 and rejects nonaudio error and oversized bodies", async () =>
        {
            foreach (var data in new[] { Silence(), Encoding.ASCII.GetBytes("ID3\0\0\0fixture") })
            {
                using var client = new HttpClient(new Handler((_,_) => Task.FromResult(Audio(data))));
                var result = await ExternalVoice.FetchAsync("시험", local, CancellationToken.None, client, _ => null);
                Require(result.Bytes.SequenceEqual(data), "Audio body changed");
            }
            foreach (var response in new[] { Audio(Encoding.UTF8.GetBytes("<html>bad</html>"), "text/html"), Audio(Encoding.UTF8.GetBytes("not an audio file")), new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("fixture-key-secret") }, Audio(new byte[ExternalVoice.MaxBytes + 1]) })
            {
                using var client = new HttpClient(new Handler((_,_) => Task.FromResult(response))); bool rejected = false;
                try { await ExternalVoice.FetchAsync("시험", local, CancellationToken.None, client, _ => null); }
                catch (InvalidOperationException ex) { rejected = true; Require(!ex.Message.Contains("fixture-key-secret"), "Server error body leaked"); }
                Require(rejected, "Invalid response accepted");
            }
            using var streamed = new HttpClient(new Handler((_,_) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new NonSeekable(new byte[ExternalVoice.MaxBytes + 1])) })));
            bool capped = false; try { await ExternalVoice.FetchAsync("시험", local, CancellationToken.None, streamed, _ => null); } catch (InvalidOperationException) { capped = true; }
            Require(capped, "Unknown-length body bypassed size limit");
        });
        await Check("Voice HTTP redirects do not forward text or credentials", async () =>
        {
            using var source = new TcpListener(IPAddress.Loopback, 0); using var destination = new TcpListener(IPAddress.Loopback, 0); source.Start(); destination.Start();
            var sourcePort = ((IPEndPoint)source.LocalEndpoint).Port; var destinationPort = ((IPEndPoint)destination.LocalEndpoint).Port;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            var serve = Task.Run(async () =>
            {
                using var connection = await source.AcceptTcpClientAsync(timeout.Token); using var stream = connection.GetStream();
                using var headers = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true); bool authorization = false;
                for (var lineCount = 0; lineCount < 100; lineCount++)
                {
                    var line = await headers.ReadLineAsync(timeout.Token) ?? throw new IOException("Missing request headers");
                    if (line.Length == 0) break;
                    if (line.Equals("Authorization: Bearer fixture-key", StringComparison.OrdinalIgnoreCase)) authorization = true;
                }
                Require(authorization, "Source did not receive the fixture credential");
                var redirect = Encoding.ASCII.GetBytes($"HTTP/1.1 307 Temporary Redirect\r\nLocation: http://127.0.0.1:{destinationPort}/stolen\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"); await stream.WriteAsync(redirect, timeout.Token);
            }, timeout.Token);
            bool rejected = false;
            try { await ExternalVoice.FetchAsync("fixture text", local with { Url = $"http://127.0.0.1:{sourcePort}/tts" }, timeout.Token, readKey: _ => "fixture-key"); }
            catch (InvalidOperationException) { rejected = true; }
            await serve; Require(rejected && !destination.Pending(), "Redirect received a second request");
        });
        await Check("Voice download timeout and user cancellation have distinct outcomes", async () =>
        {
            using var client = new HttpClient(new Handler(async (_,ct) => { await Task.Delay(2000, ct); return Audio(Silence()); }));
            bool timedOut = false; try { await ExternalVoice.FetchAsync("시험", local, CancellationToken.None, client, _ => null, TimeSpan.FromMilliseconds(20)); } catch (TimeoutException) { timedOut = true; }
            using var cancelled = new CancellationTokenSource(20); bool stopped = false; try { await ExternalVoice.FetchAsync("시험", local, cancelled.Token, client, _ => null); } catch (OperationCanceledException) { stopped = true; }
            Require(timedOut && stopped, "Cancellation became a timeout/fallback");
        });
        await Check("Voice fallback reports actual provider and disabled fallback propagates failure", async () =>
        {
            int native = 0; var settings = new UiSettings { Tts = local, OutputId = "selected", SpeechVolume = 37 };
            Task Windows(string text, UiSettings value, CancellationToken ct) { Require(text == "시험" && value.OutputId == "selected" && value.SpeechVolume == 37, "Fallback lost text/output settings"); native++; return Task.CompletedTask; }
            var result = await SpeechPipeline.RunAsync("시험", settings, CancellationToken.None, (_,_,_) => throw new InvalidOperationException("fixture failure"), (_,_,_) => throw new Exception("No audio should play"), Windows);
            Require(result.Provider == "windows" && result.UsedFallback && result.Notice!.Contains("fixture failure") && native == 1, "Fallback was hidden");
            bool rejected = false; try { await SpeechPipeline.RunAsync("시험", settings with { Tts = local with { Fallback = false } }, CancellationToken.None, (_,_,_) => throw new IOException("fixture"), (_,_,_) => Task.CompletedTask, Windows); } catch (IOException) { rejected = true; }
            Require(rejected && native == 1, "Disabled fallback still spoke");
        });
        await Check("Cancelled voice fetch or playback never starts Windows fallback", async () =>
        {
            foreach (var duringPlay in new[] { false, true })
            {
                using var stop = new CancellationTokenSource(); int played = 0, native = 0; bool cancelled = false;
                try
                {
                    await SpeechPipeline.RunAsync("시험", new() { Tts = local }, stop.Token,
                        (_,_,_) => { if (!duringPlay) stop.Cancel(); return Task.FromResult(new VoiceAudio(Silence(), ".wav")); },
                        (_,_,ct) => { played++; stop.Cancel(); ct.ThrowIfCancellationRequested(); return Task.CompletedTask; }, (_,_,_) => { native++; return Task.CompletedTask; });
                }
                catch (OperationCanceledException) { cancelled = true; }
                Require(cancelled && native == 0 && played == (duringPlay ? 1 : 0), "Cancelled output resumed through fallback");
            }
        });
        await Check("Voice settings transfer keeps destinations opt in and excludes keys", () =>
        {
            var current = new UiSettings(); var imported = new UiSettings { Tts = local with { Voice = "local-ko", Fallback = false } };
            var serialized = SettingsTransfer.Serialize(imported); var read = SettingsTransfer.Parse(serialized);
            Require(read.Tts == imported.Tts && !serialized.Contains("ApiKey"), "Voice settings were lost or keys included");
            Require(SettingsTransfer.Prepare(read, current, false, false).Tts.Provider == "windows", "Import changed network voice without connection selection");
            var accepted = SettingsTransfer.Prepare(read, current, true, false); Require(accepted.Tts == imported.Tts && SettingsTransfer.Describe(current, accepted).Contains("127.0.0.1:5002/tts"), "Connection review hid voice destination");
            Reject(() => SettingsTransfer.Serialize(new() { Tts = new() { Url = "https://example.org/tts?key=secret" } })); return Task.CompletedTask;
        });
        await Check("SAPI output tokens match exact WASAPI endpoints", () =>
        {
            const string endpoint = "{0.0.0.00000000}.{12345678-1234-1234-1234-123456789abc}";
            Require(VoicePlayback.MatchesDevice(endpoint, endpoint) && VoicePlayback.MatchesDevice("HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Speech\\AudioOutput\\TokenEnums\\MMDevices\\" + endpoint, endpoint), "SAPI token did not match endpoint");
            Require(!VoicePlayback.MatchesDevice("prefix" + endpoint, endpoint), "Partial token matched"); return Task.CompletedTask;
        });
        if (devices)
        {
            await Check("Native external audio plays WAV on default and selected output and decodes MP3", async () =>
            {
                var settings = new UiSettings { Tts = local, SpeechVolume = 0 }; var bytes = Silence();
                await VoicePlayback.PlayAsync(new(bytes, ".wav"), settings, CancellationToken.None);
                var output = SpeechOutput.Options(true).Skip(1).FirstOrDefault() ?? throw new Exception("No named output available");
                await VoicePlayback.PlayAsync(new(bytes, ".wav"), settings with { OutputId = output.Id }, CancellationToken.None);
                using var wav = new WaveFileReader(new MemoryStream(bytes)); using var mp3 = new MemoryStream(); MediaFoundationEncoder.EncodeToMp3(wav, mp3, 128000);
                await VoicePlayback.PlayAsync(new(mp3.ToArray(), ".mp3"), settings, CancellationToken.None);
            });
            await Check("Native audio cancellation stops promptly and removes temporary files", async () =>
            {
                var folder = Path.Combine(AppPaths.Home, "voice-temp"); Directory.CreateDirectory(folder); var before = Directory.GetFiles(folder).Order().ToArray();
                using var stop = new CancellationTokenSource(250); var clock = Stopwatch.StartNew(); bool cancelled = false;
                try { await VoicePlayback.PlayAsync(new(Silence(20000), ".wav"), new UiSettings { Tts = local, SpeechVolume = 0 }, stop.Token); } catch (OperationCanceledException) { cancelled = true; }
                Require(cancelled && clock.Elapsed < TimeSpan.FromSeconds(3), "Audio did not stop promptly");
                Require(before.SequenceEqual(Directory.GetFiles(folder).Order()), "Temporary speech file retained");
            });
        }
        return failed;
    }
}
