using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

internal static class MicrosoftRuntimeSetup
{
    public const string Version = "14.51.36247.0";
    internal static readonly ModelFile Installer = new("vc_redist.x64.exe", 18731856, "843068991daaa1f73ad9f6239bce4d0f6a07a51f18c37ea2a867e9beca71295c");
    internal static readonly Uri Source = new("https://download.visualstudio.microsoft.com/download/pr/ebdab8e5-1d7b-4d9f-a11b-cbb1720c3b12/843068991DAAA1F73AD9F6239BCE4D0F6A07A51F18C37EA2A867E9BECA71295C/VC_redist.x64.exe");
    public static async Task<string> PrepareAsync(string root, IProgress<ModelProgress> progress, CancellationToken token)
    {
        WhisperAssets.PlainDirectory(root); Directory.CreateDirectory(root);
        var final = Path.Combine(root, "vc-redist-" + Version + ".exe");
        if (!await ModelDownload.MatchesAsync(final, Installer, token))
        {
            var stage = Path.Combine(root, ".vc-redist-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                await WhisperAssets.DownloadAsync(client, Source, stage, Installer, progress, token);
                await Task.Run(() => VerifyPublisher(stage), token); token.ThrowIfCancellationRequested();
                // Keep a previous cache copy for diagnosis instead of executing or overwriting it.
                if (File.Exists(final)) File.Move(final, final + ".previous-" + Guid.NewGuid().ToString("N"));
                File.Move(stage, final);
            }
            finally { if (File.Exists(stage)) File.Delete(stage); }
        }
        await Task.Run(() => VerifyPublisher(final), token); token.ThrowIfCancellationRequested(); return final;
    }
    internal static ProcessStartInfo LaunchInfo(string path)
    {
        var info = new ProcessStartInfo(Path.GetFullPath(path)) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(path))! };
        // The user explicitly opens Microsoft's interactive installer and reviews its terms/UAC there.
        // Do not request quiet installation, accept terms on their behalf, or allow automatic restart.
        info.ArgumentList.Add("/install"); info.ArgumentList.Add("/norestart"); return info;
    }
    internal static async Task<int> RunAsync(string path, CancellationToken token)
    {
        using var readOnly = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!await ModelDownload.MatchesAsync(path, Installer, token)) throw new IOException("Microsoft 설치 파일이 바뀌었어요. 다시 받아 주세요.");
        await Task.Run(() => VerifyPublisher(path), token); token.ThrowIfCancellationRequested();
        using var process = Process.Start(LaunchInfo(path)) ?? throw new IOException("Microsoft 설치 프로그램을 열지 못했어요.");
        // Cancelling this wait must not kill an installer that is changing shared Windows components.
        await process.WaitForExitAsync(token); return process.ExitCode;
    }
    internal static string ExitNotice(int code) => code switch
    {
        0 => "Microsoft 설치 프로그램이 완료됐어요. 구성 요소를 다시 확인합니다.",
        3010 => "Microsoft 구성 요소 설치를 마치려면 Windows를 다시 시작해야 해요. 작업을 저장한 뒤 직접 다시 시작해 주세요.",
        1602 => "Microsoft 구성 요소 설치를 취소했어요.",
        1638 => "더 최신인 Microsoft 구성 요소가 이미 설치되어 있어요. 실제 사용 가능 여부를 다시 확인합니다.",
        _ => "Microsoft 구성 요소 설치를 완료하지 못했어요. 설치 창의 안내를 확인해 주세요. 코드: " + code
    };
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct TrustFile { public uint Size; [MarshalAs(UnmanagedType.LPWStr)] public string Path; public IntPtr Handle, Subject; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct TrustData
    {
        public uint Size; public IntPtr Policy, Sip; public uint Ui, Revocation, Choice; public IntPtr File; public uint StateAction; public IntPtr State;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Url; public uint Flags, Context; public IntPtr Signature;
    }
    [DllImport("wintrust.dll", ExactSpelling = true)] static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
    internal static void VerifyPublisher(string path)
    {
        var file = new TrustFile { Size = (uint)Marshal.SizeOf<TrustFile>(), Path = Path.GetFullPath(path) };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());
        Marshal.StructureToPtr(file, pointer, false);
        var data = new TrustData { Size = (uint)Marshal.SizeOf<TrustData>(), Ui = 2, Choice = 1, File = pointer, StateAction = 1, Flags = 0x80 };
        var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        try
        {
            int result = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            if (result != 0) throw new IOException("Microsoft 설치 파일의 전자 서명을 확인하지 못했어요. 인터넷 연결과 Windows 날짜를 확인해 주세요.");
            using var stream = File.OpenRead(path); using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
            var table = pe.PEHeaders.PEHeader?.CertificateTableDirectory ?? throw new IOException("설치 파일의 인증서 정보가 없어요.");
            if (table.RelativeVirtualAddress <= 0 || table.Size < 8 || table.Size > 8 * 1024 * 1024 || (long)table.RelativeVirtualAddress + table.Size > stream.Length) throw new IOException("설치 파일의 인증서 범위가 올바르지 않아요.");
            stream.Position = table.RelativeVirtualAddress;
            using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, true);
            uint length = reader.ReadUInt32(); ushort revision = reader.ReadUInt16(), kind = reader.ReadUInt16();
            if (length < 8 || length > table.Size || revision != 0x200 || kind != 2) throw new IOException("설치 파일의 서명 형식이 올바르지 않아요.");
            var cms = new SignedCms(); cms.Decode(reader.ReadBytes(checked((int)length - 8)));
            if (cms.SignerInfos.Count != 1 || cms.SignerInfos[0].Certificate?.GetNameInfo(X509NameType.SimpleName, false) != "Microsoft Corporation") throw new IOException("Microsoft가 서명한 설치 파일이 아니어서 실행하지 않았어요.");
        }
        finally { data.StateAction = 2; _ = WinVerifyTrust(new IntPtr(-1), ref action, ref data); Marshal.DestroyStructure<TrustFile>(pointer); Marshal.FreeHGlobal(pointer); }
    }
}
