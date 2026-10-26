using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace VoiceAssistant.Desktop;

public sealed record WebSite(string Id, string Label, string Names, string Search, string Home, string Host);

public sealed class WebSearchSession(Func<DateTimeOffset>? clock = null)
{
    readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    WebSite? last;
    DateTimeOffset searchedAt;
    public WebSite Recent => last != null && now() >= searchedAt && now() - searchedAt < TimeSpan.FromSeconds(120) ? last : WebCommands.Sites[0];
    public string Context => $"후속 웹 검색의 현재 사이트: {Recent.Label}. 사이트를 명시하지 않은 검색은 이 사이트의 검색 주소 {Recent.Search} 뒤에 검색어를 URL 인코딩해서 사용한다.";
    public void Opened(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var actual) || actual.UserInfo.Length != 0) return;
        foreach (var site in WebCommands.Sites)
        {
            var basis = new Uri(site.Search);
            if (actual.Scheme != basis.Scheme || actual.Host != basis.Host || actual.AbsolutePath != basis.AbsolutePath) continue;
            var key = basis.Query.TrimStart('?').Split('=')[0];
            if (!actual.Query.TrimStart('?').Split('&').Any(x => x.StartsWith(key + "=", StringComparison.Ordinal) && x.Length > key.Length + 1)) continue;
            last = site; searchedAt = now(); return;
        }
    }
}

public static class WebCommands
{
    public static readonly WebSite[] Sites = {
        new("google", "구글", "구글|google", "https://www.google.com/search?q=", "https://www.google.com/", "google.com"),
        new("shopping", "네이버 쇼핑", @"네이버\s*쇼핑", "https://search.shopping.naver.com/search/all?query=", "https://shopping.naver.com/", "shopping.naver.com"),
        new("naver", "네이버", "네이버|naver", "https://search.naver.com/search.naver?query=", "https://www.naver.com/", "naver.com"),
        new("coupang", "쿠팡", "쿠팡|coupang", "https://www.coupang.com/np/search?q=", "https://www.coupang.com/", "coupang.com"),
        new("amazon", "아마존", "아마존|amazon", "https://www.amazon.com/s?k=", "https://www.amazon.com/", "amazon.com")
    };
    const string Ask = @"(?:검색해\s*봐|검색해\s*줘|검색해|검색|찾아\s*줘|찾아\s*봐|찾아|보여\s*줘|알아봐)(?:\s*줘)?";
    static CommandPlan Url(string reply, string url) => new(reply, new[] { new AssistantAction("open_url", url) });
    static bool OtherTask(string text) => Regex.IsMatch(text, @"로그인|로그아웃|메일|답장|장바구니|결제|주문해|구매해|확인해|읽어|요약|비교해|추천해|그리고|검색하면서|찾으면서|열고|검색하고", RegexOptions.IgnoreCase);
    public static CommandPlan? Search(string text, WebSearchSession? session)
    {
        foreach (var site in Sites.OrderByDescending(x => x.Id == "shopping"))
        {
            var match = Regex.Match(text, "^(?:" + site.Names + @")(?:(?:에다가|에다|에서|으로|에|로|의)(?=\s|$))?\s*(.*)$", RegexOptions.IgnoreCase);
            if (!match.Success) continue;
            var query = match.Groups[1].Value.Trim();
            query = Regex.Replace(query, @"^(?:홈페이지(?:에)?\s*들어가서|사이트에서|들어가서|에\s*가서|가서|접속해서|열어서|켜서)\s*", "");
            if (OtherTask(query)) return null;
            if (Regex.IsMatch(query, @"^(?:열어|켜|들어가|가자|접속해|띄워)(?:\s*줘)?$")) return Url(site.Label + "를 열었어요.", site.Home);
            query = Regex.Replace(query, @"\s*" + Ask + "$", "").Trim();
            if (query.Length == 0) return Url(site.Label + "를 열었어요.", site.Home);
            if (query.Length > 500) throw new InvalidOperationException("검색어는 500자 이내로 입력하세요.");
            return Url(site.Label + " 검색 결과를 열었어요.", site.Search + Uri.EscapeDataString(query));
        }
        var cheap = Regex.Match(text, @"^(.+?)\s*(?:최저가|가격\s*비교)(?:\s*(?:찾아|검색|보여|알아봐))?(?:\s*줘)?$");
        if (cheap.Success && !OtherTask(cheap.Groups[1].Value)) return Url("네이버 쇼핑 검색 결과를 열었어요.", Sites.Single(x => x.Id == "shopping").Search + Uri.EscapeDataString(cheap.Groups[1].Value.Trim()));
        var generic = Regex.Match(text, @"^(.+?)\s*(?:검색해\s*봐|검색해\s*줘|검색해|검색|찾아\s*줘|찾아\s*봐|찾아)(?:\s*줘)?$");
        if (!generic.Success || OtherTask(generic.Groups[1].Value)) return null;
        var words = generic.Groups[1].Value.Trim(); if (words.Length is < 1 or > 500) return null;
        var selected = session?.Recent ?? Sites[0];
        return Url(selected.Label + " 검색 결과를 열었어요.", selected.Search + Uri.EscapeDataString(words));
    }
    public static string Host(string host)
    {
        if (host.Length is < 1 or > 253 || host.Any(x => char.IsWhiteSpace(x) || x is '/' or '\\' or ':' or '@' or '?' or '#' or '*')) throw new InvalidOperationException("정확한 사이트 도메인을 지정하세요.");
        string normalized;
        try { normalized = new IdnMapping().GetAscii(host.TrimEnd('.')).ToLowerInvariant(); }
        catch (ArgumentException) { throw new InvalidOperationException("사이트 도메인을 확인하세요."); }
        if (Uri.CheckHostName(normalized) == UriHostNameType.Unknown) throw new InvalidOperationException("사이트 도메인을 확인하세요.");
        return normalized;
    }
    public static bool MatchesHost(string? address, string host)
    {
        host = Host(host);
        var uri = WindowsActions.BrowserAddress(address);
        if (uri == null || uri.UserInfo.Length != 0) return false;
        var actual = uri.IdnHost.TrimEnd('.');
        if (host == "youtube.com" && actual.Equals("studio.youtube.com", StringComparison.OrdinalIgnoreCase)) return false;
        if (Uri.CheckHostName(host) == UriHostNameType.IPv4) return actual.Equals(host, StringComparison.OrdinalIgnoreCase);
        return actual.Equals(host, StringComparison.OrdinalIgnoreCase) || actual.EndsWith("." + host, StringComparison.OrdinalIgnoreCase);
    }
    public static CommandPlan? Browser(string text)
    {
        var compact = text.Replace(" ", "");
        if (compact is "탭목록" or "브라우저탭목록" or "열린탭목록") return new("", new[] { new AssistantAction("browser_tabs") });
        if (compact is "전체탭목록" or "브라우저전체탭목록") return new("", new[] { new AssistantAction("browser_tabs", "all") });
        var page = Regex.Match(compact, @"^(?:브라우저)?(전체)?탭목록(\d{1,3})페이지$");
        if (page.Success) return new("", new[] { new AssistantAction("browser_tabs", page.Groups[1].Success ? "all" : "", int.Parse(page.Groups[2].Value)) });
        var mail = Regex.Match(text, @"^(네이버\s*메일|지메일|gmail|구글\s*메일|아웃룩)(?:을|를)?\s*(?:열어|켜|들어가|가자|보여|띄워)(?:\s*줘)?$", RegexOptions.IgnoreCase);
        if (mail.Success) return Url("메일 페이지를 열었어요.", mail.Groups[1].Value.StartsWith("네이버") ? "https://mail.naver.com/" : mail.Groups[1].Value == "아웃룩" ? "https://outlook.live.com/mail/" : "https://mail.google.com/");
        var nth = Regex.Match(text, @"^(\S+)\s*번째\s*(메일|이메일|편지|탭)(?:을|를)?\s*(?:좀\s*)?(열어|클릭해|클릭|눌러|들어가|봐|보여|확인해|확인|선택해|닫아)(?:\s*줘)?$");
        if (nth.Success && Ordinal(nth.Groups[1].Value) is > 0 and <= 100)
        {
            if (nth.Groups[2].Value == "탭") return new("", new[] { new AssistantAction(nth.Groups[3].Value == "닫아" ? "browser_close_nth" : "browser_select_nth", Number: Ordinal(nth.Groups[1].Value)) });
            if (nth.Groups[3].Value != "닫아") return new("", new[] { new AssistantAction("mail_select", Number: Ordinal(nth.Groups[1].Value)) });
        }
        var close = Regex.Match(text, @"^(?:지금\s*켜져\s*있는\s*)?(.+?)(?:의)?\s*(?:탭|창|페이지)(?:들)?(?:을|를)?\s*(?:모두\s*|다\s*)?(?:닫아|꺼|종료해)(?:\s*줘)?$", RegexOptions.IgnoreCase);
        if (!close.Success) return null;
        var label = close.Groups[1].Value.Trim();
        // The dedicated YouTube player retains its existing "창 닫아" command.
        if (Regex.IsMatch(label, "^(유튜브|youtube)$", RegexOptions.IgnoreCase) && !text.Contains("탭")) return null;
        var site = Sites.FirstOrDefault(x => Regex.IsMatch(label, "^(?:" + x.Names + ")$", RegexOptions.IgnoreCase));
        var host = site?.Host ?? (Regex.IsMatch(label, "^(유튜브|youtube)$", RegexOptions.IgnoreCase) ? "youtube.com" : label);
        if (site == null && host != "youtube.com" && !host.Contains('.') && host != "localhost") return null;
        try { return new("", new[] { new AssistantAction("browser_close_site", Host(host)) }); } catch (InvalidOperationException) { return null; }
    }
    public static int Ordinal(string value) => int.TryParse(value, out var number) ? number : value switch { "첫" or "첫째" or "한" or "하나" => 1, "두" or "둘" => 2, "세" or "셋" => 3, "네" or "넷" => 4, "다섯" => 5, "여섯" => 6, "일곱" => 7, "여덟" => 8, "아홉" => 9, "열" => 10, _ => 0 };
}
