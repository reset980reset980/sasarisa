using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace VoiceAssistant.Desktop;

internal static class ConversationLanguageChecks
{
    internal static async Task<int> RunAsync(List<object> rows)
    {
        int failed = 0;
        void Require(bool ok, string reason) { if (!ok) throw new Exception(reason); }
        void Reject(Action work) { try { work(); } catch (ArgumentException) { return; } throw new Exception("Invalid language accepted"); }
        async Task Check(string name, Func<Task> work)
        {
            try { await work(); rows.Add(new { name, pass = true }); }
            catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.ToString() }); }
        }
        await Check("Conversation language keeps Korean defaults and validates the original twelve-language set", () =>
        {
            var old = JsonSerializer.Deserialize<UiSettings>("{}")!;
            Require(old.Language == "ko", "Existing Windows profile changed language");
            Require(ConversationLanguage.All.Select(x => x.Code).SequenceEqual(new[] { "ko", "en", "ja", "zh", "es", "fr", "de", "pt", "it", "vi", "th", "id" }), "Original language set changed");
            foreach (var item in ConversationLanguage.All)
            {
                SettingsStore.Validate(old with { Language = item.Code });
                Require(ConversationLanguage.FromMac(item.Locale) == item.Code && ConversationLanguage.FromMac(item.Locale.Replace('-', '_').ToUpperInvariant()) == item.Code, "Mac regional language not normalized");
                Require(item.AnswerInstruction.Contains(item.EnglishName) && item.MatchesSapi(item.SapiLanguage.ToUpperInvariant()), "Answer or Windows voice locale lost");
            }
            Require(ConversationLanguage.Get("en").MatchesSapi("809;409") && !ConversationLanguage.Get("ko").MatchesSapi("409"), "Regional Windows voice matching changed languages");
            Require(ConversationLanguage.Get("en").SapiPriority("409") > ConversationLanguage.Get("en").SapiPriority("809") && ConversationLanguage.Get("pt").SapiPriority("416") > ConversationLanguage.Get("pt").SapiPriority("816"), "Exact regional voice not preferred");
            foreach (var bad in new string[] { null!, "", "auto", "xx", "EN", "ko-KR", "en\n" }) Reject(() => SettingsStore.Validate(old with { Language = bad }));
            foreach (var bad in new[] { "xx-ZZ", "en/../../ko", "en\n", "english" }) Reject(() => ConversationLanguage.FromMac(bad));
            return Task.CompletedTask;
        });
        await Check("Settings export import and differences preserve language without opting into machine connections", () =>
        {
            var before = new UiSettings { Language = "ko", Device = "1", WhisperMode = "external", WhisperUrl = "http://127.0.0.1:23456" };
            // Use the actual serializable settings envelope used by the exchange UI.
            var exported = SettingsTransfer.Serialize(before with { Language = "ja" });
            var imported = SettingsTransfer.Parse(exported);
            var prepared = SettingsTransfer.Prepare(imported, before, false, false);
            Require(prepared.Language == "ja" && prepared.Device == before.Device && prepared.WhisperUrl == before.WhisperUrl, "Language tied to a machine-connection opt-in");
            Require(SettingsTransfer.Describe(before, prepared).Contains("日本語"), "Language change missing from review");
            return Task.CompletedTask;
        });
        await Check("Non-Korean streaming fails before any worker starts while the multilingual capture path is unfinished", () =>
        {
            using var input = new SpeechInput();
            foreach (var item in ConversationLanguage.All.Where(x => x.Code != "ko"))
            {
                try { input.Start(new UiSettings { Language = item.Code }); throw new Exception("Korean worker started for a different language"); }
                catch (NotSupportedException) { }
                Require(!input.Running && input.ProcessId == 0 && input.Generation == 0, "An unsupported language started capture");
            }
            return Task.CompletedTask;
        });
        await Check("Speech HTTP request bodies carry the selected language and never leak it into credential scopes", async () =>
        {
            var custom = new VoicePreferences { Provider = "http", Url = "http://127.0.0.1:5002/tts" };
            foreach (var item in ConversationLanguage.All)
            {
                using var request = ExternalVoice.Request("Sample <text>", custom, null, item.Code);
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                Require(json.RootElement.GetProperty("language").GetString() == item.Code, "Custom TTS used fixed Korean");
                var eleven = new VoicePreferences { Provider = "elevenlabs", Voice = "test-voice" };
                using var voice = ExternalVoice.Request("Sample", eleven, "test-key", item.Code);
                using var fields = JsonDocument.Parse(await voice.Content!.ReadAsStringAsync());
                Require(fields.RootElement.GetProperty("language_code").GetString() == item.Code, "ElevenLabs used fixed Korean");
                var azure = new VoicePreferences { Provider = "azure", Region = "koreacentral", Voice = "explicit-voice" };
                using var speech = ExternalVoice.Request("Sample <text>", azure, "test-key", item.Code);
                var xml = XDocument.Parse(await speech.Content!.ReadAsStringAsync());
                Require(xml.Root!.Attribute(XNamespace.Xml + "lang")!.Value == item.Locale && xml.Root.Value == "Sample <text>", "Azure locale or XML escaping changed");
            }
            Reject(() => ExternalVoice.Request("Sample", custom, null, "invalid"));
            Reject(() => ExternalVoice.Request("Sample", new VoicePreferences { Provider = "azure", Region = "koreacentral" }, "test-key", "ja"));
        });
        await Check("Actual Whisper HTTP multipart requests use each selected language and preserve compatible text replies", async () =>
        {
            using var listener = new HttpListener();
            var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start(); var port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
            var prefix = $"http://127.0.0.1:{port}/"; listener.Prefixes.Add(prefix); listener.Start();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            foreach (var item in ConversationLanguage.All)
            {
                var pending = listener.GetContextAsync().WaitAsync(timeout.Token);
                var call = SpeechInput.RefineAsync(prefix, Convert.ToBase64String(VoiceChecks.Silence()), timeout.Token, ["custom-word"], item.Code);
                var request = await pending;
                string body; using (var reader = new StreamReader(request.Request.InputStream, Encoding.UTF8)) body = await reader.ReadToEndAsync(timeout.Token);
                var response = Encoding.UTF8.GetBytes("{\"text\":\"recognized sentence\"}"); request.Response.ContentType = "application/json"; request.Response.ContentLength64 = response.Length;
                await request.Response.OutputStream.WriteAsync(response, timeout.Token); request.Response.Close();
                Require((await call).Text == "recognized sentence", "Compatible response no longer parsed");
                Require(request.Request.RawUrl == "/inference" && body.Contains("name=language\r\n\r\n" + item.Code + "\r\n") && body.Contains("custom-word"), "Whisper language or prompt was not transmitted");
            }
            listener.Stop();
        });
        await Check("Actual Brain Ollama transport and Aside rules carry all selected response languages", async () =>
        {
            using var listener = new HttpListener();
            var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start(); var port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
            var prefix = $"http://127.0.0.1:{port}/"; listener.Prefixes.Add(prefix); listener.Start();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            foreach (var item in ConversationLanguage.All)
            {
                var pending = listener.GetContextAsync().WaitAsync(timeout.Token);
                var call = new Brain().AskAsync("Answer the test question", "", new UiSettings { Language = item.Code, BrainProvider = "ollama", BrainUrl = prefix, BrainModel = "test-model" }, timeout.Token);
                var request = await pending;
                string body; using (var reader = new StreamReader(request.Request.InputStream, Encoding.UTF8)) body = await reader.ReadToEndAsync(timeout.Token);
                using var sent = JsonDocument.Parse(body); var prompt = sent.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
                var response = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { message = new { content = "{\"reply\":\"Test response\",\"actions\":[]}" } }));
                request.Response.ContentType = "application/json"; request.Response.ContentLength64 = response.Length;
                await request.Response.OutputStream.WriteAsync(response, timeout.Token); request.Response.Close();
                var answer = await call;
                Require(answer.Actions.Count == 0 && answer.Reply == "Test response" && prompt.Contains(item.AnswerInstruction) && !prompt.Contains("한국어로 짧고 자연스럽게 답한다"), "Actual Brain ignored configured language");
                Require(AsideClient.Prompt("Test request", "Reference text", false, language: item.Code).Contains(item.AnswerInstruction), "Aside kept a Korean-only instruction");
            }
            listener.Stop();
        });
        return failed;
    }
}
