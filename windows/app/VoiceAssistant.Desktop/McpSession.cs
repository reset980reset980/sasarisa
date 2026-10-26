using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

internal sealed record McpReply(bool IsError, string Status, string Summary, string? Data = null, AssistantImage? Image = null);
internal sealed record McpRequest(string Secret, AssistantAction Action);
internal sealed record McpEndpoint(string Pipe, string Secret)
{
    internal const string PipeVariable = "SASARISA_MCP_PIPE", SecretVariable = "SASARISA_MCP_SECRET";
    internal Dictionary<string, string?> Environment => new() { [PipeVariable] = Pipe, [SecretVariable] = Secret };
    internal static McpEndpoint FromEnvironment()
    {
        var pipe = System.Environment.GetEnvironmentVariable(PipeVariable) ?? "";
        var secret = System.Environment.GetEnvironmentVariable(SecretVariable) ?? "";
        if (!pipe.StartsWith("Sasarisa-MCP-", StringComparison.Ordinal) || pipe.Length != 45 || !Guid.TryParseExact(pipe[13..], "N", out _) || secret.Length != 64 || !secret.All(Uri.IsHexDigit)) throw new InvalidOperationException("활성 비서 요청의 MCP 연결 정보가 없어요.");
        return new(pipe, secret);
    }
    internal string ClaudeConfig => JsonSerializer.Serialize(new { mcpServers = new Dictionary<string, object> { ["sasarisa"] = new { type = "stdio", command = System.Environment.ProcessPath!, args = new[] { "--mcp" }, env = Environment } } });
    internal void ConfigureCodex(ProcessStartInfo info)
    {
        // JSON string quoting is also valid for TOML basic strings here; no shell involved.
        var env = "{" + string.Join(",", Environment.Select(p => p.Key + "=" + JsonSerializer.Serialize(p.Value))) + "}";
        // Permit only this app's fixed catalogue to reach its own action policy and
        // confirmation window. Keep Codex's shell/file sandbox and unknown tools closed.
        var names = McpTools.Catalog.Select(t => t.Name).ToArray();
        var approvals = "{" + string.Join(",", names.Select(name => name + "={approval_mode=\"approve\"}")) + "}";
        info.ArgumentList.Insert(info.ArgumentList.Count - 1, "-c");
        info.ArgumentList.Insert(info.ArgumentList.Count - 1, "mcp_servers.sasarisa={command=" + JsonSerializer.Serialize(System.Environment.ProcessPath) + ",args=[\"--mcp\"],env=" + env + ",enabled_tools=" + JsonSerializer.Serialize(names) + ",default_tools_approval_mode=\"prompt\",tools=" + approvals + ",tool_timeout_sec=120,startup_timeout_sec=20}");
    }
}

// One short-lived, same-user pipe for one app request. No global RPC endpoint.
internal sealed class McpSession : IAsyncDisposable
{
    const int FrameLimit = 2 * 1024 * 1024;
    readonly CancellationTokenSource lifetime;
    readonly SemaphoreSlim serial = new(1, 1);
    readonly Func<AssistantAction, CancellationToken, Task<McpReply>> dispatch;
    readonly Task[] listeners;
    internal McpEndpoint Endpoint { get; } = new("Sasarisa-MCP-" + Guid.NewGuid().ToString("N"), Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));

    internal McpSession(Func<AssistantAction, CancellationToken, Task<McpReply>> dispatch, CancellationToken token)
    {
        this.dispatch = dispatch; lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        listeners = Enumerable.Range(0, 4).Select(_ => ListenAsync()).ToArray();
    }
    async Task ListenAsync()
    {
        while (!lifetime.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(Endpoint.Pipe, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(lifetime.Token);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); deadline.CancelAfter(TimeSpan.FromSeconds(130));
                var request = await ReadAsync<McpRequest>(pipe, deadline.Token);
                if (request.Secret == null || !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(request.Secret), System.Text.Encoding.UTF8.GetBytes(Endpoint.Secret)))
                { await WriteAsync(pipe, new McpReply(true, "blocked", "활성 요청과 연결 정보가 일치하지 않아요."), deadline.Token); continue; }
                using var call = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                // SDK cancellation closes the client pipe. No late confirmation/action may survive it.
                var disconnected = WatchDisconnectAsync(pipe, call);
                try
                {
                    await serial.WaitAsync(call.Token);
                    McpReply reply;
                    try { call.Token.ThrowIfCancellationRequested(); ActionPolicy.ValidateAi(request.Action); reply = await dispatch(request.Action, call.Token); }
                    finally { serial.Release(); }
                    await WriteAsync(pipe, reply, call.Token);
                }
                finally { call.Cancel(); await disconnected; }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or JsonException or ArgumentException or InvalidOperationException) { }
        }
    }
    static async Task WatchDisconnectAsync(Stream pipe, CancellationTokenSource call)
    {
        try { await pipe.ReadExactlyAsync(new byte[1], call.Token); call.Cancel(); }
        catch (IOException) { call.Cancel(); }
        catch (OperationCanceledException) { }
    }
    internal static async Task<McpReply> CallAsync(McpEndpoint endpoint, AssistantAction action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await using var pipe = new NamedPipeClientStream(".", endpoint.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(5000, token);
        await WriteAsync(pipe, new McpRequest(endpoint.Secret, action), token);
        return await ReadAsync<McpReply>(pipe, token, FrameLimit);
    }
    static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > FrameLimit) throw new InvalidOperationException("MCP 메시지 크기가 제한을 넘었어요.");
        var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, token); await stream.WriteAsync(bytes, token); await stream.FlushAsync(token);
    }
    static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token, int limit = 64 * 1024)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, token);
        var size = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size < 1 || size > limit) throw new InvalidOperationException("MCP 메시지 크기가 올바르지 않아요.");
        var bytes = new byte[size]; await stream.ReadExactlyAsync(bytes, token);
        return JsonSerializer.Deserialize<T>(bytes) ?? throw new JsonException("MCP 메시지가 비어 있어요.");
    }
    public async ValueTask DisposeAsync()
    { lifetime.Cancel(); await Task.WhenAll(listeners); serial.Dispose(); lifetime.Dispose(); }
}
