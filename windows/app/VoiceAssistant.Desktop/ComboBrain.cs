using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceAssistant.Desktop;

internal sealed record ComboSelection(string Preferred, string Provider, UiSettings Settings)
{
    public string Description => "조합 → " + Brain.NameFor(Provider) +
        (Preferred == Provider ? "" : " · " + Brain.NameFor(Preferred) + "가 설치되지 않아 대신 맡깁니다.");
}

// Match the original ComboBrain's task categories and installation fallback order.
// Resolve once before executing; failures never replay a task with another provider.
internal static class ComboBrain
{
    static readonly string[] coding = { "코드", "코딩", "버그", "빌드", "배포", "커밋", "깃허브", "github", "함수", "에러", "리팩", "스크립트", "프로그램 짜", "앱 만들", "클로드", "claude", "xcode", "terminal", "code", "bug", "build", "deploy", "commit", "refactor", "script" };
    static readonly string[] web = { "사이트", "홈페이지", "웹", "들어가", "접속", "로그인", "메일", "쿠팡", "스레드", "인스타", "스튜디오", "네이버", "구글", "예약", "주문", "장바구니", "페이지", "브라우저", "댓글", "검색해서", "찾아서", "가격 비교", "website", "site", "page", "browser", "mail", "order", "book a", "comment", "log in", "sign in", "amazon" };
    public static string Pick(string command)
    {
        var text = command.ToLowerInvariant();
        return coding.Any(text.Contains) ? "claude" : web.Any(text.Contains) ? "aside" : "codex";
    }
    public static IReadOnlyList<string> Installed()
    {
        var providers = new List<string>();
        if (Brain.CodexBinary() != null) providers.Add("codex");
        if (Brain.ClaudeBinary() != null) providers.Add("claude");
        if (AsideClient.Binary() != null) providers.Add("aside");
        return providers;
    }
    public static string Status()
    {
        var found = Installed();
        return found.Count == 0 ? "조합 · Codex, Claude Code 또는 Aside CLI 설치 필요" : "조합 · 설치됨: " + string.Join(" · ", found.Select(Brain.NameFor)) + " · 로그인은 각 도구에서 확인하세요.";
    }
    public static ComboSelection Resolve(UiSettings settings, string command, IReadOnlyList<string>? installed = null, bool textOnly = false)
    {
        if (settings.BrainProvider != "combo") return new(settings.BrainProvider, settings.BrainProvider, settings);
        // Dictation is text editing, even when its quoted text mentions a website.
        var preferred = textOnly ? "codex" : Pick(command);
        var order = preferred switch { "claude" => new[] { "claude", "codex", "aside" }, "aside" => new[] { "aside", "codex", "claude" }, _ => new[] { "codex", "claude", "aside" } };
        var available = installed ?? Installed();
        var provider = order.FirstOrDefault(available.Contains) ?? throw new InvalidOperationException("조합에 사용할 AI가 없어요. Codex, Claude Code 또는 Aside CLI를 설치하고 로그인하세요.");
        var profile = settings.ProviderProfiles.GetValueOrDefault(provider) ?? new ProviderPreferences();
        return new(preferred, provider, settings with { BrainProvider = provider, BrainModel = profile.Model, BrainUrl = profile.Url });
    }
}
