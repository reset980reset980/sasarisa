using System;
using System.Linq;

namespace VoiceAssistant.Desktop;

public sealed record DesktopPreferences
{
    public string UserName { get; set; } = "";
    public string WakeHotkey { get; set; } = "Ctrl+Shift+Space";
    public string StopHotkey { get; set; } = "Ctrl+Alt+Backspace";
    public string OverlayPosition { get; set; } = "bottomRight";
    public string OverlayScreen { get; set; } = "active";
    public bool ShowIdleOrb { get; set; }
    public string GreetingStyle { get; set; } = "reactor";
    public string GreetingText { get; set; } = "";
    public string GreetingTitle { get; set; } = "";
    public string GreetingSubtitle { get; set; } = "";
    public string[] GreetingPhrases { get; set; } = Array.Empty<string>();
    public string[] GreetingUrls { get; set; } = Array.Empty<string>();
    public string[] GreetingApps { get; set; } = Array.Empty<string>();
    public bool SetupComplete { get; set; }
    public void Validate(string activation)
    {
        if (UserName.Length > 30 || UserName.Any(char.IsControl)) throw new ArgumentException("사용자 호칭은 30자 이내로 입력하세요.");
        HotkeyManager.ValidatePair(WakeHotkey, StopHotkey, activation == "hold");
        if (OverlayPosition is not ("center" or "bottomCenter" or "bottomRight" or "bottomLeft" or "top")) throw new ArgumentException("알림 위치를 확인하세요.");
        if (string.IsNullOrWhiteSpace(OverlayScreen) || OverlayScreen.Length > 200) throw new ArgumentException("알림을 표시할 모니터를 확인하세요.");
        if (GreetingStyle is not ("reactor" or "calm" or "card")) throw new ArgumentException("인사 화면 종류를 확인하세요.");
        if (GreetingText.Length > 500 || GreetingTitle.Length > 80 || GreetingSubtitle.Length > 160) throw new ArgumentException("인사말은 500자, 제목은 80자, 부제는 160자 이내로 입력하세요.");
        if (GreetingPhrases == null || GreetingUrls == null || GreetingApps == null || GreetingPhrases.Length > 12 || GreetingUrls.Length + GreetingApps.Length > 8) throw new ArgumentException("인사 호출 문장은 12개, 함께 열 항목은 8개까지입니다.");
        if (GreetingPhrases.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 80 || x.Any(char.IsControl))) throw new ArgumentException("인사 호출 문장은 각각 1~80자로 입력하세요.");
        foreach (var url in GreetingUrls) ActionPolicy.Validate(new("open_url", url));
        if (GreetingApps.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 100)) throw new ArgumentException("인사할 때 열 앱을 확인하세요.");
    }
}
