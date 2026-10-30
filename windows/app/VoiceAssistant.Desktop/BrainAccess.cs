using System;
using System.Diagnostics;

namespace VoiceAssistant.Desktop;

// Original Brain.permissionArgs: safe exposes the guarded MCP box and WebSearch;
// full additionally grants Bash and direct file/web tools. This is independent of
// confirmation policy for actions executed through the app's MCP box.
internal static class BrainAccess
{
    internal const string FullTools = "Bash,Read,Glob,Grep,WebSearch,WebFetch";
    internal static void Validate(string access)
    {
        if (access is not ("safe" or "full")) throw new ArgumentException(UiText.Get("Claude 작업 권한은 제한 또는 전체 중에서 선택하세요."));
    }
    internal static string Label(string access)
    {
        Validate(access);
        return UiText.Get(access == "full" ? "전체 권한 · 명령·파일·웹" : "제한 권한 · 비서 도구와 웹 검색");
    }
    internal static string Help(string access)
    {
        Validate(access);
        return UiText.Get(access == "full"
            ? "Claude가 명령 실행·파일 조회/변경·웹 접속을 직접 할 수 있습니다. 이 자체 도구의 작업에는 비서의 실행 확인과 되돌리기가 적용되지 않습니다. 설정 저장 후 Claude 및 조합의 Claude 요청(휴대폰 포함)에 적용합니다. Codex·Aside·API와 받아쓰기/글 다듬기에는 적용하지 않습니다."
            : "원본처럼 비서의 화면 도구와 웹 검색만 허용합니다. Claude 자체의 명령 실행·파일 접근·웹 주소 직접 읽기는 허용하지 않습니다. 비서 화면 도구는 선택한 실행 확인 방식을 따릅니다. 설정 저장 후 적용합니다.");
    }
    internal static string Prompt(string access)
    {
        Validate(access);
        return access == "full"
            ? "Claude 작업 권한은 full이다. 제공된 Bash/Read/Glob/Grep/WebSearch/WebFetch 도구를 사용자의 요청에 필요한 명령·파일·웹 작업에 사용할 수 있다. 비서 MCP 도구의 실행 확인은 앱이 별도로 적용한다. 자체 도구의 작업을 앱의 실행 기록이나 되돌리기가 지원한다고 주장하지 않는다. 사용자가 요청하지 않은 작업은 하지 않는다.\n"
            : "Claude 작업 권한은 safe다. 비서 MCP 도구와 WebSearch만 사용할 수 있다. 쉘·파일 도구·WebFetch를 다른 경로로 우회하지 않는다.\n";
    }
    internal static void ConfigureClaude(ProcessStartInfo info, string access, McpEndpoint? mcp, bool textOnly)
    {
        Validate(access);
        var full = access == "full" && !textOnly;
        var tools = textOnly ? "" : full ? FullTools : "WebSearch";
        if (!full) info.ArgumentList.Add("--restricted");
        foreach (var arg in new[] { "--tools",tools,"--strict-mcp-config","--mcp-config",textOnly ? "{\"mcpServers\":{}}" : mcp?.ClaudeConfig ?? "{\"mcpServers\":{}}",
            "--permission-mode","default","--disallowedTools",full ? "Write,Edit,NotebookEdit,SendMessage,ToolSearch,Task,Agent,PowerShell" : "Bash,PowerShell,Read,Write,Edit,NotebookEdit,Glob,Grep,WebFetch,SendMessage,ToolSearch,Task,Agent" }) info.ArgumentList.Add(arg);
        var allowed = tools;
        if (!textOnly && mcp != null) allowed = (allowed.Length > 0 ? allowed + "," : "") + "mcp__sasarisa__*";
        if (allowed.Length > 0) { info.ArgumentList.Add("--allowedTools"); info.ArgumentList.Add(allowed); }
    }
}

public partial class MainWindow
{
    void BrainAccess_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => RefreshBrainAccessHelp();
    void RefreshBrainAccessHelp()
    {
        if (BrainAccessHelp == null) return;
        var access = SelectedTag(BrainAccessInput);
        BrainAccessHelp.Text = access is "safe" or "full" ? BrainAccess.Help(access) : "";
    }
}
