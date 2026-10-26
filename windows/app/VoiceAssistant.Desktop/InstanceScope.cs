using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace VoiceAssistant.Desktop;

// Alternate VOICE_ASSISTANT_HOME profiles must not show or shut down the normal application.
internal static class InstanceScope
{
    internal static string ForHome(string home, string standardHome)
    {
        static string Normalize(string value) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(value)).ToUpperInvariant();
        var normalized = Normalize(home);
        if (normalized == Normalize(standardHome)) return "Local\\VoiceAssistantWindows";
        return "Local\\VoiceAssistantWindows.Profile." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..32];
    }
}
