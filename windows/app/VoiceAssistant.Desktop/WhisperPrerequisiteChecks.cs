using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

internal static class WhisperPrerequisiteChecks
{
    sealed class ProgressSink : IProgress<ModelProgress> { public void Report(ModelProgress value) { } }
    internal static byte[] ImportFixture(string function, bool ordinal = false)
    {
        var data = new byte[0x600];
        void U16(int at, ushort value) => BitConverter.GetBytes(value).CopyTo(data, at);
        void U32(int at, uint value) => BitConverter.GetBytes(value).CopyTo(data, at);
        U16(0, 0x5a4d); U32(0x3c, 0x80); U32(0x80, 0x4550); U16(0x84, 0x8664); U16(0x86, 1); U16(0x94, 0xf0); U16(0x96, 0x2022);
        U16(0x98, 0x20b); U32(0xb8, 0x1000); U32(0xbc, 0x200); U32(0xd0, 0x3000); U32(0xd4, 0x200); U32(0x104, 16); U32(0x110, 0x2000); U32(0x114, 40);
        Encoding.ASCII.GetBytes(".idata").CopyTo(data, 0x188); U32(0x190, 0x400); U32(0x194, 0x2000); U32(0x198, 0x400); U32(0x19c, 0x200); U32(0x1ac, 0xc0000040);
        U32(0x200, 0x2080); U32(0x20c, 0x2060); U32(0x210, 0x2080); Encoding.ASCII.GetBytes("vcruntime140.dll\0").CopyTo(data, 0x260);
        BitConverter.GetBytes(ordinal ? 0x8000000000000001UL : 0x20a0UL).CopyTo(data, 0x280);
        Encoding.ASCII.GetBytes(function + "\0").CopyTo(data, 0x2a2); return data;
    }
    public static async Task<int> RunAsync(List<object> rows, bool native = false)
    {
        int failed = 0;
        void Require(bool value, string message) { if (!value) throw new Exception(message); }
        async Task Check(string name, Func<Task> action) { try { await action(); rows.Add(new { name, pass = true }); } catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.ToString() }); } }
        var root = Path.Combine(AppPaths.Home, "prerequisite-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        await Check("Whisper missing runtime uses a real empty lookup directory and starts no server or download", async () =>
        {
            var status = WhisperPrerequisites.CheckSystem(root); Require(!status.Ready && status.Detail.Contains("Microsoft Visual C++"), "Missing runtime called ready");
            var assets = Path.Combine(root, "assets"); using var engine = new WhisperEngine(assets, () => WhisperPrerequisites.CheckSystem(root));
            try { await engine.CheckAsync("cpu", default); throw new Exception("Missing runtime accepted"); } catch (WhisperPrerequisiteException) { }
            try { await engine.InstallAsync("cuda", new ProgressSink(), default); throw new Exception("Download started without runtime"); } catch (WhisperPrerequisiteException) { }
            Require(engine.ProcessId == 0 && !Directory.Exists(assets), "Missing runtime still launched/downloaded");
        });
        await Check("Whisper import parser reads x64 names ordinals and rejects malformed images", () =>
        {
            var image = Path.Combine(root, "import.dll"); File.WriteAllBytes(image, ImportFixture("__CxxFrameHandler3"));
            var imports = WhisperPrerequisites.ReadImports(image); Require(imports.Count == 1 && imports[0].Name == "__CxxFrameHandler3" && imports[0].Library == "vcruntime140.dll", "Named import lost");
            File.WriteAllBytes(image, ImportFixture("", true)); Require(WhisperPrerequisites.ReadImports(image).Single().Ordinal == 1, "Ordinal import lost");
            File.WriteAllBytes(image, new byte[64]);
            try { WhisperPrerequisites.ReadImports(image); throw new Exception("Malformed image accepted"); } catch (Exception ex) when (ex is BadImageFormatException or IOException) { }
            return Task.CompletedTask;
        });
        await Check("Microsoft installer is interactive does not auto restart and distinguishes cancelled failed and reboot results", () =>
        {
            var info = MicrosoftRuntimeSetup.LaunchInfo(Path.Combine(root, "installer.exe"));
            Require(info.UseShellExecute && info.ArgumentList.SequenceEqual(new[] { "/install", "/norestart" }) && info.Verb != "runas", "Unrequested unattended install/elevation flags");
            Require(MicrosoftRuntimeSetup.ExitNotice(3010).Contains("다시 시작") && MicrosoftRuntimeSetup.ExitNotice(1602).Contains("취소") && MicrosoftRuntimeSetup.ExitNotice(1234).Contains("1234"), "Installer outcome hidden");
            return Task.CompletedTask;
        });
        await Check("Microsoft installer refuses unsigned content before execution", () =>
        {
            var path = Path.Combine(root, "unsigned.exe"); File.WriteAllBytes(path, ImportFixture("__CxxFrameHandler3"));
            try { MicrosoftRuntimeSetup.VerifyPublisher(path); throw new Exception("Unsigned executable trusted"); } catch (IOException) { }
            return Task.CompletedTask;
        });
        if (native)
        {
            await Check("Whisper loads this PC's Microsoft runtime and checks all official CPU CUDA required exports", () =>
            {
                Require(WhisperPrerequisites.CheckSystem().Ready, "PC runtime missing");
                foreach (var flavor in new[] { "cpu", "cuda12" }) WhisperPrerequisites.RequireImports(Path.Combine(AppPaths.WindowsRoot, "artifacts", "whisper-native", flavor));
                var fixture = Path.Combine(root, "imports"); Directory.CreateDirectory(fixture);
                var path = Path.Combine(fixture, "import.dll"); File.WriteAllBytes(path, ImportFixture("__CxxFrameHandler3")); WhisperPrerequisites.RequireImports(fixture);
                File.WriteAllBytes(path, ImportFixture("_SasarisaMissingRuntimeFunction"));
                try { WhisperPrerequisites.RequireImports(fixture); throw new Exception("Outdated/missing runtime export accepted"); } catch (WhisperPrerequisiteException) { }
                return Task.CompletedTask;
            });
            await Check("Microsoft official installer downloads verifies pinned digest signer and reuses verified cache without launching", async () =>
            {
                var cache = Path.Combine(root, "downloads");
                var path = await MicrosoftRuntimeSetup.PrepareAsync(cache, new ProgressSink(), default);
                Require(await ModelDownload.MatchesAsync(path, MicrosoftRuntimeSetup.Installer, default), "Wrong installer file");
                var written = File.GetLastWriteTimeUtc(path); Require(await MicrosoftRuntimeSetup.PrepareAsync(cache, new ProgressSink(), default) == path && File.GetLastWriteTimeUtc(path) == written, "Verified cache replaced");
                var tampered = Path.Combine(root, "tampered.exe"); var bytes = File.ReadAllBytes(path); bytes[0x400] ^= 1; File.WriteAllBytes(tampered, bytes);
                try { MicrosoftRuntimeSetup.VerifyPublisher(tampered); throw new Exception("Tampered installer accepted"); } catch (IOException) { }
                using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
                try { await MicrosoftRuntimeSetup.PrepareAsync(cache, new ProgressSink(), cancelled.Token); throw new Exception("Cancelled preparation accepted"); } catch (OperationCanceledException) { }
            });
            await Check("Microsoft installer rejects another valid publisher", () =>
            {
                try { MicrosoftRuntimeSetup.VerifyPublisher(AppPaths.Python); throw new Exception("Python Software Foundation signer accepted as Microsoft"); }
                catch (IOException ex) when (ex.Message.Contains("Microsoft가 서명한")) { }
                return Task.CompletedTask;
            });
        }
        return failed;
    }
}
