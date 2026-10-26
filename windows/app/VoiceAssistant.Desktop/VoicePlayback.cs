using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace VoiceAssistant.Desktop;

public sealed record VoicePlaybackObserver(Action<string> Started, Action<double> Level, Action<bool> Stopped);

internal sealed class MeasuredVoiceProvider(AudioFileReader reader, Action<double> level) : IWaveProvider
{
    public WaveFormat WaveFormat => reader.WaveFormat;
    public int Read(byte[] buffer, int offset, int count)
        => Read(buffer.AsSpan(offset, count));
    public int Read(Span<byte> buffer)
    {
        var read = reader.Read(buffer);
        if (read > 0)
        {
            if (reader.WaveFormat.Encoding != WaveFormatEncoding.IeeeFloat || reader.WaveFormat.BitsPerSample != 32) throw new InvalidOperationException("측정할 음성 형식을 확인하지 못했어요.");
            double sum = 0;
            var samples = MemoryMarshal.Cast<byte, float>(buffer[..read]);
            foreach (var sample in samples) sum += sample * sample;
            level(Math.Sqrt(sum / samples.Length));
        }
        return read;
    }
}

public static class VoicePlayback
{
    public static bool MatchesDevice(string selected, string deviceId) => selected.Equals(deviceId, StringComparison.OrdinalIgnoreCase) || selected.EndsWith("\\" + deviceId, StringComparison.OrdinalIgnoreCase);
    static MMDevice Device(MMDeviceEnumerator devices, string id)
    {
        if (id.Length == 0) return devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        MMDevice? found = null;
        foreach (var device in devices.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            if (found == null && MatchesDevice(id, device.ID)) found = device;
            else device.Dispose();
        }
        return found ?? throw new InvalidOperationException("선택한 출력 장치가 없어요. 기본 출력이나 연결된 장치를 선택하세요.");
    }
    public static Task PlayAsync(VoiceAudio audio, UiSettings settings, CancellationToken token) => PlayAsync(audio, settings, token, null);
    public static Task PlayAsync(VoiceAudio audio, UiSettings settings, CancellationToken token, VoicePlaybackObserver? observer) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        if (audio.Extension is not (".wav" or ".mp3") || audio.Bytes.Length > ExternalVoice.MaxBytes) throw new ArgumentException("음성 파일 형식이나 크기를 확인하세요.");
        var folder = Path.Combine(AppPaths.Home, "voice-temp"); Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, Guid.NewGuid().ToString("N") + audio.Extension);
        try
        {
            File.WriteAllBytes(path, audio.Bytes); token.ThrowIfCancellationRequested();
            using var reader = new AudioFileReader(path); reader.Volume = settings.SpeechVolume / 100f;
            if (reader.TotalTime <= TimeSpan.Zero || reader.TotalTime > TimeSpan.FromMinutes(4)) throw new InvalidOperationException("음성 파일 길이는 4분 이내여야 해요.");
            using var devices = new MMDeviceEnumerator();
            using var selected = Device(devices, settings.OutputId);
            using var player = new WasapiPlayerBuilder().WithDevice(selected).WithSharedMode().Build();
            Exception? playbackError = null; player.PlaybackStopped += (_, e) => playbackError = e.Exception;
            player.Init(observer == null ? reader : new MeasuredVoiceProvider(reader, observer.Level)); token.ThrowIfCancellationRequested();
            AppPaths.Log("tts-audio", $"start provider={settings.Tts.Provider} volume={settings.SpeechVolume} customOutput={settings.OutputId.Length > 0} seconds={reader.TotalTime.TotalSeconds:F2}");
            var completed = false;
            var deadline = DateTime.UtcNow + reader.TotalTime + TimeSpan.FromSeconds(10);
            try
            {
                observer?.Started(selected.FriendlyName);
                token.ThrowIfCancellationRequested(); player.Play();
                while (player.PlaybackState != PlaybackState.Stopped)
                {
                    if (token.WaitHandle.WaitOne(40)) token.ThrowIfCancellationRequested();
                    if (DateTime.UtcNow > deadline) throw new TimeoutException("음성 재생 시간이 초과됐어요.");
                }
                token.ThrowIfCancellationRequested();
                if (playbackError != null) throw new InvalidOperationException("음성 재생 장치에서 오류가 발생했어요.", playbackError);
                AppPaths.Log("tts-audio", "done");
                completed = true;
            }
            finally { try { player.Stop(); } finally { observer?.Stopped(!completed); } }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }, token);
}
