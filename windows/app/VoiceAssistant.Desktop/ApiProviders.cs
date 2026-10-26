using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace VoiceAssistant.Desktop;

public sealed record ProviderPreferences(string Model = "", string Url = "");
public sealed record ApiProvider(string Id, string Name, string BaseUrl, bool EditableUrl = false, bool RequiresKey = true);

public static class ApiProviders
{
    public static readonly IReadOnlyList<ApiProvider> All = new[]
    {
        new ApiProvider("anthropic", "Anthropic API", "https://api.anthropic.com/v1"),
        new ApiProvider("openai", "OpenAI API", "https://api.openai.com/v1"),
        new ApiProvider("gemini", "Gemini API", "https://generativelanguage.googleapis.com/v1beta/openai"),
        new ApiProvider("openrouter", "OpenRouter", "https://openrouter.ai/api/v1"),
        new ApiProvider("groq", "Groq", "https://api.groq.com/openai/v1"),
        new ApiProvider("lmstudio", "LM Studio", "http://127.0.0.1:1234/v1", true, false),
        new ApiProvider("compatible", "OpenAI 호환 서버", "http://127.0.0.1:1234/v1", true, false)
    };
    public static ApiProvider? Find(string id) => All.FirstOrDefault(p => p.Id == id);
    public static string DefaultUrl(string id) => Find(id)?.BaseUrl ?? (id == "ollama" ? "http://127.0.0.1:11434" : "");
    public static Uri Endpoint(string id, string url)
    {
        var provider = Find(id) ?? throw new ArgumentException("API 제공자를 확인하세요.");
        var address = provider.EditableUrl ? url.Trim() : provider.BaseUrl;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") ||
            (uri.Scheme == "http" && !uri.IsLoopback) || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("서버 주소는 HTTPS 또는 이 PC의 HTTP 주소를 입력하세요. 키는 주소에 넣지 마세요.");
        if (id == "lmstudio" && !uri.IsLoopback) throw new ArgumentException("LM Studio는 이 PC의 로컬 서버 주소를 입력하세요. 원격 서버는 ‘OpenAI 호환 서버’를 선택하세요.");
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
    }
    public static string CredentialTarget(string id, string url)
    {
        var endpoint = Endpoint(id, url).AbsoluteUri;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(endpoint)));
        return "VoiceAssistantWindows:api:" + id + ":" + hash;
    }
}
