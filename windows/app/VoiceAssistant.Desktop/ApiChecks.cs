using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

internal static class ApiChecks
{
    sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
    static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    static void Require(bool condition, string why) { if (!condition) throw new Exception(why); }
    static object Plan => new { reply = "연결 성공", actions = new[] { new { kind = "volume", value = "", number = 35 } } };
    static object Chat(object? plan = null, string finish = "tool_calls") => new { choices = new[] { new { finish_reason = finish, message = new { tool_calls = new[] { new { type = "function", function = new { name = "assistant_plan", arguments = JsonSerializer.Serialize(plan ?? Plan) } } } } } } };
    public static async Task<int> RunAsync(List<object> rows)
    {
        int failed = 0;
        async Task Check(string name, Func<Task> test)
        {
            try { await test(); rows.Add(new { name, pass = true }); }
            catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.Message }); }
        }
        await Check("API endpoint and credential scope", () =>
        {
            Require(ApiProviders.Endpoint("openai", "https://untrusted.invalid/v1").Host == "api.openai.com", "Fixed provider accepted another host");
            foreach (var url in new[] { "http://example.com/v1", "https://key@example.com/v1", "https://example.com?api_key=test", "https://example.com/#key", "file:///C:/model" })
            {
                bool rejected = false; try { ApiProviders.Endpoint("compatible", url); } catch (ArgumentException) { rejected = true; }
                Require(rejected, "Unsafe endpoint accepted");
            }
            Require(ApiProviders.CredentialTarget("compatible", "https://one.example/v1") != ApiProviders.CredentialTarget("compatible", "https://two.example/v1"), "Different hosts share credentials");
            Require(ApiProviders.CredentialTarget("compatible", "https://one.example/v1") == ApiProviders.CredentialTarget("compatible", "https://ONE.example:443/v1/"), "Equivalent endpoints have different credentials");
            return Task.CompletedTask;
        });
        await Check("Windows credential vault isolated roundtrip and delete", () =>
        {
            string target = "VoiceAssistantWindows:api:selftest:" + Guid.NewGuid().ToString("N");
            try
            {
                Require(CredentialStore.Read(target) == null, "Fixture already exists");
                CredentialStore.Save(target, "dummy-not-a-secret-한글");
                Require(CredentialStore.Read(target) == "dummy-not-a-secret-한글", "Credential roundtrip failed");
                CredentialStore.Delete(target); Require(CredentialStore.Read(target) == null, "Credential not deleted");
            }
            finally { CredentialStore.Delete(target); }
            return Task.CompletedTask;
        });
        await Check("Provider model profiles survive settings serialization", () =>
        {
            var source = new UiSettings { BrainProvider = "gemini", BrainModel = "fixture-gemini", ProviderProfiles = new() { ["gemini"] = new("fixture-gemini", ApiProviders.DefaultUrl("gemini")), ["codex"] = new("fixture-codex", "") } };
            var copy = JsonSerializer.Deserialize<UiSettings>(JsonSerializer.Serialize(source))!;
            SettingsStore.Validate(copy); Require(copy.ProviderProfiles["codex"].Model == "fixture-codex" && copy.BrainModel == "fixture-gemini", "Models mixed across providers");
            Require(!JsonSerializer.Serialize(copy).Contains("ApiKey", StringComparison.OrdinalIgnoreCase), "Key in plain settings");
            return Task.CompletedTask;
        });
        foreach (var provider in ApiProviders.All)
        {
            await Check(provider.Name + " fixture request and decoded plan", async () =>
            {
                using var client = new HttpClient(new Handler(async (request, token) =>
                {
                    var anthropic = provider.Id == "anthropic";
                    Require(request.RequestUri == new Uri(ApiProviders.Endpoint(provider.Id, provider.BaseUrl), anthropic ? "messages" : provider.Id == "openai" ? "responses" : "chat/completions"), "Wrong API route");
                    Require(anthropic ? request.Headers.GetValues("x-api-key").Single() == "test-key" : request.Headers.Authorization?.ToString() == "Bearer test-key", "Missing authentication");
                    using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                    Require(body.RootElement.GetProperty("model").GetString() == "fixture-model", "Wrong model");
                    Require(body.RootElement.GetProperty("tools").GetArrayLength() == 1, "Unexpected toolset");
                    if (anthropic)
                    {
                        Require(request.Headers.GetValues("anthropic-version").Single() == "2023-06-01", "Missing version");
                        Require(body.RootElement.GetProperty("tools")[0].TryGetProperty("input_schema", out _), "Missing tool schema");
                        return Json(new { stop_reason = "tool_use", content = new[] { new { type = "tool_use", name = "assistant_plan", input = Plan } } });
                    }
                    if (provider.Id == "openai")
                    {
                        Require(body.RootElement.GetProperty("tools")[0].GetProperty("name").GetString() == "assistant_plan" && !body.RootElement.GetProperty("store").GetBoolean(), "Wrong Responses plan schema/storage");
                        return Json(new { status = "completed", output = new[] { new { type = "function_call", name = "assistant_plan", arguments = JsonSerializer.Serialize(Plan) } } });
                    }
                    Require(body.RootElement.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString() == "assistant_plan", "Missing plan function");
                    return Json(Chat());
                }));
                var plan = await Brain.ApiAsync("test", new() { BrainProvider = provider.Id, BrainModel = "fixture-model", BrainUrl = provider.BaseUrl }, CancellationToken.None, "test-key", client);
                Require(plan.Reply == "연결 성공" && plan.Actions.Single().Number == 35, "Response decoded incorrectly");
            });
        }
        await Check("API models list and optional local authentication", async () =>
        {
            using var client = new HttpClient(new Handler((request, _) =>
            {
                Require(request.RequestUri!.AbsolutePath == "/v1/models" && request.Method == HttpMethod.Get, "Wrong models route");
                Require(request.Headers.Authorization == null, "Local server got an invented credential");
                return Task.FromResult(Json(new { data = new[] { new { id = "model-b" }, new { id = "model-a" }, new { id = "model-b" } } }));
            }));
            var models = await Brain.ModelsAsync(new() { BrainProvider = "lmstudio", BrainUrl = "http://127.0.0.1:1234/v1" }, CancellationToken.None, "", client);
            Require(models.SequenceEqual(new[] { "model-a", "model-b" }), "Models missing or duplicated");
        });
        await Check("API refuses truncated, unsafe and malformed plans", async () =>
        {
            var unsafePlan = new { reply = "oops", actions = new[] { new { kind = "shell", value = "notepad", number = 0 } } };
            var excessive = new { reply = "oops", actions = Enumerable.Range(0, 7).Select(_ => new { kind = "answer", value = "", number = 0 }).ToArray() };
            foreach (var response in new object[] { Chat(finish: "length"), Chat(unsafePlan), Chat(excessive), Chat(new { reply = "incomplete" }), new { choices = Array.Empty<object>() } })
            {
                using var client = new HttpClient(new Handler((_, _) => Task.FromResult(Json(response))));
                bool rejected = false;
                try { await Brain.ApiAsync("test", new() { BrainProvider = "compatible", BrainUrl = "http://127.0.0.1:1234/v1", BrainModel = "fixture" }, CancellationToken.None, "test-key", client); }
                catch (InvalidOperationException) { rejected = true; }
                Require(rejected, "Invalid plan accepted");
            }
        });
        await Check("OpenAI Responses plan rejects incomplete, unknown and unsafe function output", () =>
        {
            foreach (var response in new object[]
            {
                new { status = "incomplete", output = Array.Empty<object>() },
                new { status = "completed", output = Array.Empty<object>() },
                new { status = "completed", output = new[] { new { type = "function_call", name = "other", arguments = JsonSerializer.Serialize(Plan) } } },
                new { status = "completed", output = new[] { new { type = "function_call", name = "assistant_plan", arguments = "{\"reply\":\"bad\",\"actions\":[{\"kind\":\"shell\",\"value\":\"bad\",\"number\":0}]}" } } }
            })
            {
                var rejected = false; try { Brain.DecodeApi(JsonSerializer.SerializeToElement(response), false, true); } catch (InvalidOperationException) { rejected = true; }
                Require(rejected, "Unsafe Responses plan accepted");
            }
            return Task.CompletedTask;
        });
        await Check("API HTTP errors never expose server body or key", async () =>
        {
            foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests, HttpStatusCode.Redirect })
            {
                using var client = new HttpClient(new Handler((_, _) => Task.FromResult(Json(new { error = "echo-test-key-private-prompt" }, status))));
                string message = "";
                try { await Brain.ApiAsync("private-prompt", new() { BrainProvider = "openai", BrainModel = "fixture" }, CancellationToken.None, "test-key", client); }
                catch (InvalidOperationException ex) { message = ex.Message; }
                Require(message.Contains(((int)status).ToString()) && !message.Contains("test-key") && !message.Contains("private-prompt"), "HTTP failure leaked or was swallowed");
            }
        });
        await Check("API request cancellation", async () =>
        {
            using var client = new HttpClient(new Handler(async (_, token) => { await Task.Delay(5000, token); return Json(Chat()); }));
            using var cancellation = new CancellationTokenSource(40);
            bool canceled = false;
            try { await Brain.ApiAsync("test", new() { BrainProvider = "openai", BrainModel = "fixture" }, cancellation.Token, "test-key", client); }
            catch (OperationCanceledException) { canceled = true; }
            Require(canceled, "Cancellation ignored");
        });
        return failed;
    }
}
