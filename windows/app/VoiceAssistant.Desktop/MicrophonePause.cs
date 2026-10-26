using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceAssistant.Desktop;

public sealed record MicrophoneUser(int ProcessId, string Name);
public sealed record MicrophoneSnapshot(bool Available, IReadOnlyList<MicrophoneUser> Users, int Devices, DateTimeOffset ObservedAt);

// This gate controls recognition, not OS microphone permissions or another app's capture stream.
public sealed class MicrophonePause(Func<DateTimeOffset>? clock = null)
{
    readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    MicrophoneSnapshot? snapshot;
    DateTimeOffset manualUntil;
    public bool Enabled { get; set; } = true;
    public bool Manual => manualUntil > now();
    public bool Known => snapshot is { Available: true } && now() - snapshot.ObservedAt < TimeSpan.FromSeconds(6);
    public IReadOnlyList<MicrophoneUser> Users => snapshot?.Users ?? Array.Empty<MicrophoneUser>();
    public bool AutomaticPaused => Enabled && (!Known || Users.Count > 0);
    public bool Blocked => AutomaticPaused && !Manual;
    public bool Permit(bool requested) => requested && !Blocked;
    public void Observe(MicrophoneSnapshot value, params int[] ownProcesses)
    {
        snapshot = value with { Users = value.Users.Where(x => !ownProcesses.Contains(x.ProcessId) || x.ProcessId <= 0).DistinctBy(x => x.ProcessId).ToArray() };
    }
    public void BeginManual(TimeSpan duration) => manualUntil = now().Add(duration);
    public void EndManual() => manualUntil = DateTimeOffset.MinValue;
    public string Detail(string hotkey) => Known
        ? "다른 앱이 마이크를 사용하고 있어요. 끝나면 다시 듣습니다. " + hotkey + "로 한 번 말할 수 있어요."
        : "마이크 사용 상태를 확인하고 있어요. " + hotkey + "로 직접 말하거나 설정에서 자동 대기를 조절하세요.";
}
