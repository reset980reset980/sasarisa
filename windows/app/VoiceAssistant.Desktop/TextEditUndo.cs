using System;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

// Document text stays in the bounded, in-memory action history only.
internal sealed class TextEditUndo(FieldEditState before, FieldEditState after,
    Func<CancellationToken, Task<FieldEditState>> read,
    Func<FieldEditState, string, CancellationToken, Task> restore)
{
    internal async Task<string> RunAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); before.Validate(); after.Validate();
        var current = await read(token);
        // Moving the caret is harmless. Any intervening content change blocks undo.
        if (current.Text != after.Text) throw new AgentStoppedException("입력 후 문서 내용이 바뀌어 되돌리지 않았어요. 현재 글을 그대로 두었어요.");
        token.ThrowIfCancellationRequested();
        await restore(before, after.Text, token);
        if (await read(token) != before) throw new InvalidOperationException("복원 결과의 글과 선택 위치를 확인하지 못했어요. 문서를 확인하세요.");
        return "자비스가 입력하기 전의 글과 선택 위치로 되돌렸어요.";
    }
}
