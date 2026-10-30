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
        if (access is not ("safe" or "full")) throw new ArgumentException(UiText.Get("AI 작업 권한은 제한 또는 전체 중에서 선택하세요."));
    }
    internal static string Label(string access)
    {
        Validate(access);
        return UiText.Get(access == "full" ? "전체 권한 · 보호 앱과 암호 입력" : "제한 권한 · 일반 화면 작업");
    }
    internal static string Help(string access)
    {
        Validate(access);
        return UiText.Get(access == "full"
            ? "비서 화면 도구로 터미널·개발 도구 등 보호 앱과 암호 칸에도 입력할 수 있습니다. 암호 내용은 읽거나 자동으로 되돌리지 않습니다. Claude는 명령·파일·웹 도구도 직접 사용하며 자체 도구에는 비서의 확인·되돌리기가 적용되지 않습니다. 비서 화면 도구의 대상 확인·취소·실행 확인은 유지합니다. 저장 후 휴대폰 요청에도 적용합니다. Aside 자체 도구의 권한과 글 다듬기의 도구 차단은 바꾸지 않습니다."
            : "보호 앱과 암호 칸의 비서 화면 조작을 차단하고 일반 글 입력의 줄바꿈은 공백으로 전달합니다. Claude는 비서 MCP와 웹 검색만 사용하며 자체 명령·파일·웹 주소 읽기는 허용하지 않습니다. 비서 화면 도구는 선택한 실행 확인 방식을 따릅니다. 저장 후 적용합니다.");
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
