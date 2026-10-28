using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

public sealed partial class Brain
{
    internal static async Task<CommandPlan> ApiToolsAsync(string prompt, UiSettings settings,
        Func<AssistantAction, CancellationToken, Task<McpReply>> dispatch, CancellationToken token,
        string? keyOverride = null, HttpClient? transport = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(120)); token = deadline.Token;
        var connection = settings; var model = settings.BrainModel;
        if (settings.BrainProvider == "ollama")
        {
            var local = SettingsStore.ValidateLocalUrl(settings.BrainUrl);
            connection = new UiSettings { BrainProvider = "compatible", BrainUrl = local.GetLeftPart(UriPartial.Authority) + "/v1", BrainModel = model };
            keyOverride = ""; // Ollama is a local connection, never reuse another provider's stored credential.
        }
        var key = ApiKey(connection, keyOverride);
        using var owned = transport == null ? NewApiClient() : null; var client = transport ?? owned!;
        if (string.IsNullOrWhiteSpace(model))
        {
            if (settings.BrainProvider != "ollama") throw new InvalidOperationException("설정에서 사용할 모델을 선택하거나 모델 이름을 입력하세요.");
            var models = await ModelsAsync(connection, token, "", client); model = models[0];
        }
        var conversation = new ApiToolConversation(settings.BrainProvider, prompt);
        for (var round = 0; round < AgentRun.MaxRounds; round++)
        {
            token.ThrowIfCancellationRequested();
            using var request = ApiRequest(connection, conversation.Route, key, conversation.Request(model));
            using var document = await ApiResponse(client, request, token, settings.BrainProvider);
            var turn = conversation.Accept(document.RootElement); token.ThrowIfCancellationRequested();
            if (turn.Calls.Count == 0) return new(turn.Text, Array.Empty<AssistantAction>());
            var results = new List<(ApiToolCall, McpReply)>(); var failed = false;
            foreach (var call in turn.Calls)
            {
                token.ThrowIfCancellationRequested();
                var reply = failed ? new McpReply(true, "skipped", "앞선 동작이 실패해 이 호출은 실행하지 않았어요. 실제 결과를 보고 다시 판단하세요.") : await dispatch(call.Action, token);
                token.ThrowIfCancellationRequested();
                if (reply.Status is "blocked" or "declined") return new(reply.Summary, Array.Empty<AssistantAction>());
                failed |= reply.IsError; results.Add((call, reply));
            }
            conversation.Results(results);
        }
        throw new AgentStoppedException("12단계까지 진행했지만 요청을 마치지 못해 멈췄어요. 실행 기록을 확인하세요.");
    }
}
