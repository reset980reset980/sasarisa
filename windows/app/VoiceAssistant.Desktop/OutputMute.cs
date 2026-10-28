using System;
using NAudio.CoreAudioApi;

namespace VoiceAssistant.Desktop;

internal interface IOutputMuteDevice : IDisposable
{
    string Id { get; }
    bool Muted { get; set; }
}

internal sealed class OutputMute(Func<IOutputMuteDevice> open)
{
    internal sealed record State(string DeviceId, bool Muted);
    internal sealed record Change(State Previous, State Current);
    internal static OutputMute System { get; } = new(() => new Endpoint());
    static State Capture(IOutputMuteDevice device) => new(device.Id, device.Muted);

    internal Change Set(bool muted)
    {
        using var device = open(); var before = Capture(device);
        if (before.Muted != muted) device.Muted = muted;
        var after = Capture(device);
        if (after.DeviceId != before.DeviceId || after.Muted != muted) throw new InvalidOperationException("출력 장치의 음소거 변경을 확인하지 못했어요.");
        return new(before, after);
    }

    internal void Restore(Change change)
    {
        using var device = open(); var current = Capture(device);
        if (current != change.Current || current.DeviceId != change.Previous.DeviceId)
            throw new InvalidOperationException("출력 장치나 음소거 상태가 바뀌어 덮어쓰지 않았어요. 원하는 상태를 다시 말해 주세요.");
        if (current.Muted != change.Previous.Muted) device.Muted = change.Previous.Muted;
        if (Capture(device) != change.Previous) throw new InvalidOperationException("출력 장치의 음소거 복원을 확인하지 못했어요.");
    }

    sealed class Endpoint : IOutputMuteDevice
    {
        readonly MMDeviceEnumerator devices = new();
        readonly MMDevice device;
        public Endpoint()
        {
            try { device = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); }
            catch { devices.Dispose(); throw; }
        }
        public string Id => device.ID;
        public bool Muted { get => device.AudioEndpointVolume.Mute; set => device.AudioEndpointVolume.Mute = value; }
        public void Dispose() { device.Dispose(); devices.Dispose(); }
    }
}
