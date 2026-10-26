using System.Text.RegularExpressions;

namespace VoiceAssistant.Desktop;

internal static class YouTubeCommands
{
    const string PoliteEnd = @"(?:\s*(?:줘|주세요|봐|봐줘|줄래|줄래요))?";
    internal static bool OpensHome(string text)
    {
        var compact = text.Replace(" ", "");
        return Regex.IsMatch(compact,
            @"^(?:(?:유튜브|youtube)(?:(?:의)?(?:홈페이지|사이트|페이지|화면|창|홈|앱))?(?:을|를)?(?:좀)?(?:열어|켜|실행(?:해)?|띄워|보여|접속해|들어가)?(?:줘|주세요|봐|봐줘|줄래|줄래요)?|open(?:the)?youtube(?:homepage|home|site|app)?)$",
            RegexOptions.IgnoreCase);
    }
    internal static CommandPlan? Query(string text)
    {
        // A one-syllable particle requires a following space so that 에스파/로파이
        // retain their first syllable even when recognition omits spaces.
        var prefix = Regex.Match(text, @"^(?:유튜브|youtube)(?:에서|에다가|에다|[에로](?=\s))?\s*(.+)$", RegexOptions.IgnoreCase);
        if (!prefix.Success) return null;
        var query = prefix.Groups[1].Value.Trim();
        var ending = Regex.Match(query, @"^(.*?)\s*(검색해|검색|찾아|틀어|재생해|재생|열어|켜|보여)" + PoliteEnd + @"$", RegexOptions.IgnoreCase);
        var searchOnly = false;
        if (ending.Success)
        {
            query = ending.Groups[1].Value.Trim();
            searchOnly = ending.Groups[2].Value is "검색해" or "검색" or "찾아";
        }
        // Return a clarification instead of falling through to a generic web
        // search for the site name or playing the request verb as a title.
        if (query.Length == 0) return new("유튜브에서 어떤 영상을 찾을까요?", System.Array.Empty<AssistantAction>());
        return new(searchOnly ? "유튜브 검색 결과를 열었어요." : "첫 번째 영상의 재생을 요청했어요.",
            new[] { new AssistantAction(searchOnly ? "youtube_search" : "youtube_play", query) });
    }
}
