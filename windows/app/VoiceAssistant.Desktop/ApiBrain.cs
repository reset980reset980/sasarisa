using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

public sealed partial class Brain
{
    internal static HttpClient NewApiClient() => new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(120) };
    static string? ApiKey(UiSettings settings, string? keyOverride)
    {
        var provider = ApiProviders.Find(settings.BrainProvider) ?? throw new ArgumentException("API 제공자를 확인하세요.");
        var key = keyOverride ?? CredentialStore.Read(ApiProviders.CredentialTarget(provider.Id, settings.BrainUrl));
        if (provider.RequiresKey && string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException(provider.Name + " API 키를 설정에서 저장하세요.");
        if (key != null && key.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0) throw new ArgumentException("API 키는 한 줄이어야 해요.");
        return key;
    }
    static HttpRequestMessage ApiRequest(UiSettings settings, string route, string? key, object? body = null)
    {
        var request = new HttpRequestMessage(body == null ? HttpMethod.Get : HttpMethod.Post, new Uri(ApiProviders.Endpoint(settings.BrainProvider, settings.BrainUrl), route));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (settings.BrainProvider == "anthropic")
        {
            if (!string.IsNullOrWhiteSpace(key)) request.Headers.Add("x-api-key", key);
            request.Headers.Add("anthropic-version", "2023-06-01");
        }
        else if (!string.IsNullOrWhiteSpace(key)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (settings.BrainProvider == "openrouter") request.Headers.Add("X-Title", "Sasarisa Windows");
        if (body != null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return request;
    }
    static async Task<JsonDocument> ApiResponse(HttpClient client, HttpRequestMessage request, CancellationToken token, string? usageProvider = null)
    {
        using var usage = usageProvider == null ? null : new AiUsageAttempt(usageProvider, cancellation: token);
        HttpResponseMessage response;
        try { response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token); }
        catch (OperationCanceledException) { usage?.Canceled(); throw; }
        catch (HttpRequestException) { throw new InvalidOperationException("AI 서버에 연결하지 못했어요. 서버 실행 상태와 주소, 네트워크를 확인하세요."); }
        using var responseLifetime = response;
        if (!response.IsSuccessStatusCode)
        {
            // Error bodies may echo credentials or private prompt text; never display or log them.
            var detail = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "API 키와 해당 모델의 사용 권한을 확인하세요.",
                HttpStatusCode.TooManyRequests => "요청 한도나 잔액을 확인하고 잠시 뒤 다시 시도하세요.",
                HttpStatusCode.NotFound => "서버 주소와 모델 이름을 확인하세요.",
                HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity => "모델이 함수 호출을 지원하는지, 모델 이름이 맞는지 확인하세요.",
                _ when (int)response.StatusCode is >= 300 and < 400 => "서버가 다른 주소로 이동을 요청했어요. 설정의 최종 서버 주소를 확인하세요.",
                _ => "서버 상태를 확인하고 다시 시도하세요."
            };
            throw new InvalidOperationException($"AI 연결 실패 (HTTP {(int)response.StatusCode}). {detail}");
        }
        const int limit = 2_000_000;
        if (response.Content.Headers.ContentLength > limit) throw new InvalidOperationException("AI 응답이 너무 커요.");
        using var stream = await response.Content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        int length;
        while ((length = await stream.ReadAsync(bytes, token)) > 0)
        {
            if (buffer.Length + length > limit) throw new InvalidOperationException("AI 응답이 너무 커요.");
            buffer.Write(bytes, 0, length);
        }
        try { var document = JsonDocument.Parse(buffer.ToArray()); usage?.ObserveApi(document.RootElement); usage?.Complete(); return document; }
        catch (JsonException) { throw new InvalidOperationException("서버가 올바른 JSON 응답을 보내지 않았어요."); }
    }
    public static async Task<IReadOnlyList<string>> ModelsAsync(UiSettings settings, CancellationToken token, string? keyOverride = null, HttpClient? transport = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(120)); token = deadline.Token;
        var key = ApiKey(settings, keyOverride);
        using var owned = transport == null ? NewApiClient() : null;
        using var request = ApiRequest(settings, "models", key);
        using var document = await ApiResponse(transport ?? owned!, request, token);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("이 서버의 모델 목록을 읽지 못했어요. 모델 이름을 직접 입력하세요.");
        var models = data.EnumerateArray().Where(x => x.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
            .Select(x => x.GetProperty("id").GetString()!).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().OrderBy(x => x, StringComparer.Ordinal).Take(1000).ToArray();
        if (models.Length == 0) throw new InvalidOperationException("사용 가능한 모델이 없어요. 서버에서 모델을 설치하거나 사용 권한을 확인하세요.");
        return models;
    }
    public static async Task<CommandPlan> ApiAsync(string prompt, UiSettings settings, CancellationToken token, string? keyOverride = null, HttpClient? transport = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(120)); token = deadline.Token;
        if (string.IsNullOrWhiteSpace(settings.BrainModel)) throw new InvalidOperationException("설정에서 사용할 모델을 선택하거나 모델 이름을 입력하세요.");
        var key = ApiKey(settings, keyOverride);
        using var owned = transport == null ? NewApiClient() : null;
        using var schema = JsonDocument.Parse(Schema);
        var tool = new { name = "assistant_plan", description = "사용자에게 답하고 검증 가능한 Windows 동작 계획을 제안합니다.", parameters = schema.RootElement };
        var anthropic = settings.BrainProvider == "anthropic";
        var responses = settings.BrainProvider == "openai";
        object body = anthropic ? new
        {
            model = settings.BrainModel, max_tokens = 4096,
            messages = new[] { new { role = "user", content = prompt } },
            tools = new[] { new { tool.name, tool.description, input_schema = schema.RootElement } },
            tool_choice = new { type = "tool", name = "assistant_plan", disable_parallel_tool_use = true }
        } : responses ? new
        {
            model = settings.BrainModel, store = false,
            input = new[] { new { role = "user", content = prompt } },
            tools = new[] { new { type = "function", tool.name, tool.description, parameters = schema.RootElement, strict = false } },
            tool_choice = new { type = "function", name = "assistant_plan" }
        } : new
        {
            model = settings.BrainModel, stream = false,
            messages = new[] { new { role = "user", content = prompt } },
            tools = new[] { new { type = "function", function = tool } },
            tool_choice = new { type = "function", function = new { name = "assistant_plan" } }
        };
        using var request = ApiRequest(settings, anthropic ? "messages" : responses ? "responses" : "chat/completions", key, body);
        using var document = await ApiResponse(transport ?? owned!, request, token, settings.BrainProvider);
        try { return DecodeApi(document.RootElement, anthropic, responses); }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException or IndexOutOfRangeException)
        { throw new InvalidOperationException("AI 응답의 작업 계획을 확인할 수 없어 실행하지 않았어요. 함수 호출을 지원하는 대화 모델을 선택하세요."); }
    }
    internal static CommandPlan DecodeApi(JsonElement root, bool anthropic, bool responses = false)
    {
        JsonElement input;
        JsonDocument? nested = null;
        try
        {
            if (anthropic)
            {
                if (root.GetProperty("stop_reason").GetString() != "tool_use") throw new InvalidOperationException("Incomplete tool output");
                var calls = root.GetProperty("content").EnumerateArray().Where(x => x.GetProperty("type").GetString() == "tool_use").ToArray();
                if (calls.Length != 1 || calls[0].GetProperty("name").GetString() != "assistant_plan") throw new InvalidOperationException("Unexpected tool output");
                input = calls[0].GetProperty("input");
            }
            else if (responses)
            {
                if (root.GetProperty("status").GetString() != "completed") throw new InvalidOperationException("Incomplete response");
                var calls = root.GetProperty("output").EnumerateArray().Where(x => x.GetProperty("type").GetString() == "function_call").ToArray();
                if (calls.Length != 1 || calls[0].GetProperty("name").GetString() != "assistant_plan") throw new InvalidOperationException("Unexpected tool output");
                nested = JsonDocument.Parse(calls[0].GetProperty("arguments").GetString()!); input = nested.RootElement;
            }
            else
            {
                var choice = root.GetProperty("choices")[0];
                if (choice.GetProperty("finish_reason").GetString() is not ("tool_calls" or "stop")) throw new InvalidOperationException("Incomplete response");
                var calls = choice.GetProperty("message").GetProperty("tool_calls");
                if (calls.GetArrayLength() != 1 || calls[0].GetProperty("type").GetString() != "function" || calls[0].GetProperty("function").GetProperty("name").GetString() != "assistant_plan") throw new InvalidOperationException("Unexpected tool output");
                nested = JsonDocument.Parse(calls[0].GetProperty("function").GetProperty("arguments").GetString()!);
                input = nested.RootElement;
            }
            var plan = Decode(input);
            if (plan.Actions.Count > 6 || plan.Reply.Length > 12000) throw new InvalidOperationException("Response limit exceeded");
            foreach (var action in plan.Actions) ActionPolicy.ValidateAi(action);
            return plan;
        }
        finally { nested?.Dispose(); }
    }
}
