using System.Reflection;

namespace VoiceAssistant.Desktop;

public sealed record PhoneClientInfo(string Kind, string Version)
{
    public static PhoneClientInfo For(bool preview = false) => new(preview ? "preview" : "standard",
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0");
}
