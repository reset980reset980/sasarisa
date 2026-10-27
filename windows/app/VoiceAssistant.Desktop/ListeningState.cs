using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace VoiceAssistant.Desktop;

// User intent survives temporary pauses, settings reloads and worker recovery.
// Audio, recognition results and device information are never stored here.
public sealed class ListeningState
{
    sealed record Saved(int Version, bool Requested);
    readonly string path;
    public bool Requested { get; private set; }
    public string? Error { get; private set; }
    public ListeningState(string? path = null)
    {
        this.path = path ?? Path.Combine(AppPaths.Home, "listening-state.json");
        try
        {
            if (!File.Exists(this.path)) return;
            if (new FileInfo(this.path).Length > 4096) throw new InvalidDataException("Listening state is too large");
            var saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(this.path));
            if (saved?.Version != 1) throw new InvalidDataException("Unsupported listening state");
            Requested = saved.Requested;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        { Error = "이전 듣기 상태를 읽지 못해 자동 듣기를 시작하지 않았어요. ‘듣기 시작’으로 다시 켜 주세요."; }
    }
    public void Remember(bool requested)
    {
        if (Error == null && Requested == requested && File.Exists(path)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(new Saved(1, requested)), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temp, path, path + ".previous"); else File.Move(temp, path);
            Requested = requested; Error = null;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public bool ShouldStart(string mode, bool ready, bool explicitListen = false)
    {
        if (!ready) return false;
        if (explicitListen) return true;
        return mode switch { "always" => true, "off" => false, "restore" => Error == null && Requested, _ => false };
    }
}
