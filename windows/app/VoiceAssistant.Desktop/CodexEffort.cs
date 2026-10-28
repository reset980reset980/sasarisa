using System;

namespace VoiceAssistant.Desktop;

internal static class CodexEffort
{
    public static string Label(string value) => value switch
    {
        "low" => "낮음",
        "medium" => "보통",
        "high" => "높음",
        _ => throw new ArgumentException("Codex 생각 깊이는 낮음·보통·높음 중에서 선택하세요.")
    };
    public static string Argument(string value)
    {
        _ = Label(value);
        return "model_reasoning_effort=\"" + value + "\"";
    }
}
