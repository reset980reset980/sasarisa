using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace VoiceAssistant.Desktop;

internal static class InstanceScopeChecks
{
    public static int Run(List<object> rows)
    {
        var failed = 0;
        void Check(string name, Action action)
        { try { action(); rows.Add(new { name, pass = true }); } catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.Message }); } }
        void Require(bool value) { if (!value) throw new Exception("Profile instance boundary changed"); }
        Check("Default profile preserves upgrade IPC and equivalent profile paths share one scope", () =>
        {
            var home = Path.Combine(Path.GetTempPath(), "assistant-instance-" + Guid.NewGuid().ToString("N"));
            Require(InstanceScope.ForHome(home, home) == "Local\\VoiceAssistantWindows");
            Require(InstanceScope.ForHome(home.ToLowerInvariant() + "\\.\\", home) == "Local\\VoiceAssistantWindows");
            var other = Path.Combine(home, "test");
            Require(InstanceScope.ForHome(other, home) == InstanceScope.ForHome(other.ToUpperInvariant() + "\\", home));
            Require(InstanceScope.ForHome(other, home) != InstanceScope.ForHome(Path.Combine(home, "test2"), home));
        });
        Check("Alternate profile wake and quit signals are isolated", () =>
        {
            var home = Path.Combine(Path.GetTempPath(), "assistant-instance-" + Guid.NewGuid().ToString("N"));
            var a = InstanceScope.ForHome(Path.Combine(home, "a"), home);
            var b = InstanceScope.ForHome(Path.Combine(home, "b"), home);
            using var show = new EventWaitHandle(false, EventResetMode.AutoReset, a + ".Show");
            using var quit = new EventWaitHandle(false, EventResetMode.AutoReset, a + ".Quit");
            using var otherQuit = new EventWaitHandle(false, EventResetMode.AutoReset, b + ".Quit");
            using var request = EventWaitHandle.OpenExisting(a + ".Quit"); request.Set();
            Require(quit.WaitOne(0) && !otherQuit.WaitOne(0) && !show.WaitOne(0));
        });
        return failed;
    }
}
