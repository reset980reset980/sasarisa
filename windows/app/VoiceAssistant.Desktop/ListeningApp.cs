using System;
using System.IO;

namespace VoiceAssistant.Desktop;

public partial class App
{
    readonly ListeningState listeningState = new();
    void RememberListening(bool requested)
    {
        try { listeningState.Remember(requested); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppPaths.Log("listening-state", ex.GetType().Name);
            window.AddMessage("system", "다음 실행의 듣기 상태를 저장하지 못했어요. 저장 공간과 권한을 확인하세요.");
        }
    }
    void RestoreListening(bool explicitListen)
    {
        if (explicitListen) RememberListening(true);
        var ready = settings.Desktop.SetupComplete && SetupReadiness.HasSpeechEnvironmentFor(settings) && setupWindow == null;
        var start = listeningState.ShouldStart(settings.Desktop.StartupListening, ready, explicitListen);
        AppPaths.Log("listening-startup", $"mode={settings.Desktop.StartupListening}; requested={listeningState.Requested}; ready={ready}; start={start}");
        if (start) { RememberListening(true); StartListening(automatic: true); }
        else if (listeningState.Error != null && settings.Desktop.StartupListening == "restore") window.AddMessage("system", listeningState.Error);
    }
}
