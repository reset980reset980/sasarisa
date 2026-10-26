using System;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

internal sealed record FieldEditState(string Text, int Start, int Length)
{
    internal void Validate()
    {
        if (Text.Length > 6000 || Start < 0 || Length < 0 || Start > Text.Length || Length > Text.Length - Start || SplitsSurrogate(Start) || SplitsSurrogate(Start + Length)) throw new InvalidOperationException("입력칸의 글 또는 선택 범위를 확인하지 못했어요.");
    }
    bool SplitsSurrogate(int at) => at > 0 && at < Text.Length && char.IsHighSurrogate(Text[at - 1]) && char.IsLowSurrogate(Text[at]);
    internal FieldEditState Apply(string text)
    {
        Validate(); var result = Text[..Start] + text + Text[(Start + Length)..];
        if (result.Length > 6000) throw new InvalidOperationException("입력 후 전체 글은 6,000자 이내여야 해요.");
        return new(result, Start + text.Length, 0);
    }
}

internal static class FieldInsertion
{
    internal static string Normalize(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');
    internal static async Task RunAsync(FieldEditState original, string text, Func<FieldEditState> read, Action<string> write, CancellationToken token, Func<CancellationToken, Task>? settle = null)
    {
        text = Normalize(text); _ = original.Apply(text);
        if (text.Length == 0) throw new ArgumentException("추가할 글이 없어요.");
        settle ??= cancellation => Task.Delay(75, cancellation);
        var expected = original;
        for (var offset = 0; offset < text.Length;)
        {
            token.ThrowIfCancellationRequested();
            if (read() != expected) throw new AgentStoppedException("입력칸의 글·커서·선택 영역이 바뀌어 추가 입력을 멈췄어요.");
            var count = Math.Min(64, text.Length - offset);
            if (offset + count < text.Length && char.IsHighSurrogate(text[offset + count - 1]) && char.IsLowSurrogate(text[offset + count])) count--;
            var part = text.Substring(offset, count);
            token.ThrowIfCancellationRequested(); write(part); expected = expected.Apply(part); offset += count;
            await settle(token);
        }
        token.ThrowIfCancellationRequested();
        if (read() != expected) throw new InvalidOperationException("글 입력은 전달했지만 최종 내용·커서 위치가 예상과 달라요. 대상 입력칸을 확인하세요.");
    }
}
