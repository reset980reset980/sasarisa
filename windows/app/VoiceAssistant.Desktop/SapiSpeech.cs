using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

// Render to a WAV, then use the same measured output path as external voices.
// https://learn.microsoft.com/en-us/previous-versions/windows/desktop/ms723597(v=vs.85)
internal static class SapiSpeech
{
    public static Task<VoiceAudio> RenderAsync(string text, UiSettings options, CancellationToken token)
    {
        var language = ConversationLanguage.Get(options.Language);
        var completion = new TaskCompletionSource<VoiceAudio>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            object? instance = null, streamObject = null;
            VoiceAudio? rendered = null; Exception? failure = null; var cancelled = false;
            var path = Path.Combine(AppPaths.Home, "voice-temp", "sapi-" + Guid.NewGuid().ToString("N") + ".wav");
            try
            {
                token.ThrowIfCancellationRequested(); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                instance = Activator.CreateInstance(Type.GetTypeFromProgID("SAPI.SpVoice") ?? throw new InvalidOperationException("Windows 음성 출력이 설치되어 있지 않아요."))!;
                dynamic voice = instance;
                object collection = voice.GetVoices(); dynamic voices = collection; var found = false; var bestPriority = 0;
                try
                {
                    for (var i = 0; i < voices.Count; i++)
                    {
                        object itemObject = voices.Item(i); dynamic item = itemObject;
                        try
                        {
                            var priority = options.VoiceId.Length > 0 ? (string)item.Id == options.VoiceId ? 3 : 0 : language.SapiPriority((string)item.GetAttribute("Language"));
                            if (priority <= bestPriority) continue;
                            voice.Voice = item; found = true; bestPriority = priority;
                            if (priority >= 2) break;
                        }
                        finally { Marshal.FinalReleaseComObject(itemObject); }
                    }
                }
                finally { Marshal.FinalReleaseComObject(collection); }
                if (options.VoiceId.Length > 0 && !found) throw new InvalidOperationException("선택한 목소리가 없어요. 설정에서 설치된 목소리를 선택하세요.");
                if (!found) throw new InvalidOperationException(language.Label + " Windows 목소리가 설치되지 않았어요. Windows 언어 설정에서 음성 기능을 설치하거나 외부 목소리를 선택하세요.");
                streamObject = Activator.CreateInstance(Type.GetTypeFromProgID("SAPI.SpFileStream") ?? throw new InvalidOperationException("Windows 음성 파일 출력을 사용할 수 없어요."))!;
                dynamic stream = streamObject; stream.Open(path, 3, false);
                voice.AudioOutputStream = stream; voice.Rate = options.SpeechRate; voice.Volume = 100;
                token.ThrowIfCancellationRequested(); voice.Speak(text.Length > 1500 ? text[..1500] : text, 17);
                var deadline = DateTime.UtcNow.AddSeconds(30);
                while (!(bool)voice.WaitUntilDone(40))
                {
                    if (token.IsCancellationRequested) { voice.Speak("", 2); token.ThrowIfCancellationRequested(); }
                    if (DateTime.UtcNow > deadline) { voice.Speak("", 2); throw new TimeoutException("Windows 음성 생성 시간이 초과됐어요."); }
                    System.Windows.Forms.Application.DoEvents();
                }
                stream.Close(); token.ThrowIfCancellationRequested();
                if (new FileInfo(path).Length > ExternalVoice.MaxBytes) throw new InvalidOperationException("생성한 음성 파일이 너무 커요.");
                rendered = new(File.ReadAllBytes(path), ".wav");
            }
            catch (OperationCanceledException) { cancelled = true; }
            catch (Exception ex) { failure = ex; }
            finally
            {
                if (streamObject != null) { try { ((dynamic)streamObject).Close(); } catch { } Marshal.FinalReleaseComObject(streamObject); }
                if (instance != null) Marshal.FinalReleaseComObject(instance);
                try { if (File.Exists(path)) File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppPaths.Log("tts-cleanup", ex.Message); }
            }
            if (cancelled || token.IsCancellationRequested) completion.TrySetCanceled(token);
            else if (failure != null) completion.TrySetException(failure);
            else completion.TrySetResult(rendered!);
        }) { IsBackground = true, Name = "VoiceAssistant speech rendering" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
    }
}
