using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

internal static class WhisperAssets
{
    public const string Revision = "b5454"; // Same source commit as v1.9.5: d1be6fde11ac6e0407606b4e42fe72d34add8037.
    public const string ModelRevision = "5359861c739e955e79d9a303bcbc70fb988958b1";
    public static readonly ModelFile Model = new("ggml-large-v3-turbo-q8_0.bin", 874188075, "317eb69c11673c9de1e1f0d459b253999804ec71ac4c23c17ecf5fbe24e259a1");
    static readonly ModelFile Cpu = new("whisper-bin-x64.zip", 8928640, "6ba69e3482d7826214f90a6a9c84ca07782aec1e1d0c6a7c30c994fd5d816ccb");
    static readonly ModelFile Cuda = new("whisper-bin-win-cuda-12.4.0-x64.zip", 684913404, "afef0b881c500958921c3f5523b50e59ee2ec9b6f5cbd25b324c51ed308a957a");
    static readonly IReadOnlyDictionary<string, ModelFile[]> Files = LoadManifest();
    public static string Home => Path.Combine(AppPaths.Home, "whisper");
    public static string Runtime(string root, string mode) => Path.Combine(root, Revision + "-" + (mode == "cuda" ? "cuda12" : "cpu"));
    public static string ModelPath(string root) => Path.Combine(root, "models", Model.Name);
    public static ModelFile Archive(string mode) => mode switch { "cpu" => Cpu, "cuda" => Cuda, _ => throw new ArgumentException("CPU 또는 NVIDIA 방식을 선택하세요.") };
    public static IReadOnlyList<ModelFile> RuntimeFiles(string mode) { _ = Archive(mode); return Files[mode]; }
    static IReadOnlyDictionary<string, ModelFile[]> LoadManifest()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("VoiceAssistant.Desktop.WhisperRuntimeFiles.json")!;
        using var reader = new StreamReader(stream);
        return JsonSerializer.Deserialize<Dictionary<string, ModelFile[]>>(reader.ReadToEnd())!;
    }
    internal static void PlainDirectory(string path)
    {
        for (var d = new DirectoryInfo(Path.GetFullPath(path)); d != null; d = d.Parent)
            if (d.Exists && (d.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("음성 보정 폴더에 연결 경로를 사용할 수 없어요.");
    }
    internal static void SafeFiles(IReadOnlyList<ModelFile> files)
    {
        if (files.Count == 0 || files.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count ||
            files.Any(x => string.IsNullOrWhiteSpace(x.Name) || x.Name is "." or ".." || Path.GetFileName(x.Name) != x.Name || x.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || x.Length <= 0 || x.Sha256.Length != 64 || !x.Sha256.All(Uri.IsHexDigit)))
            throw new ArgumentException("음성 보정 파일 목록이 올바르지 않아요.");
    }
    public static async Task<bool> VerifyRuntimeAsync(string path, IReadOnlyList<ModelFile> files, CancellationToken token)
    {
        SafeFiles(files); PlainDirectory(path);
        if (!Directory.Exists(path)) return false;
        var expected = files.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (Directory.EnumerateFileSystemEntries(path).Any(x => !expected.Contains(Path.GetFileName(x)) || (File.GetAttributes(x) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)) return false;
        foreach (var file in files) if (!await ModelDownload.MatchesAsync(Path.Combine(path, file.Name), file, token)) return false;
        return true;
    }
    public static async Task VerifyAsync(string root, string mode, CancellationToken token)
    {
        PlainDirectory(root); PlainDirectory(Path.Combine(root, "models"));
        if (!await VerifyRuntimeAsync(Runtime(root, mode), RuntimeFiles(mode), token) ||
            (File.Exists(ModelPath(root)) && (File.GetAttributes(ModelPath(root)) & FileAttributes.ReparsePoint) != 0) ||
            !await ModelDownload.MatchesAsync(ModelPath(root), Model, token))
            throw new IOException("로컬 음성 보정 파일이 없거나 손상됐어요. 설정의 ‘보정 엔진 준비’에서 설치·복구해 주세요.");
    }
    public static async Task InstallAsync(string root, string mode, IProgress<ModelProgress> progress, CancellationToken token)
    {
        var archive = Archive(mode); PlainDirectory(root); Directory.CreateDirectory(root);
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Sasarisa-Windows/0.2");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(TimeSpan.FromMinutes(45));
        var ct = limit.Token; var runtime = Runtime(root, mode); var files = RuntimeFiles(mode);
        if (!await VerifyRuntimeAsync(runtime, files, ct))
        {
            var stage = Path.Combine(root, ".whisper-download-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
            try
            {
                var zip = Path.Combine(stage, archive.Name);
                await DownloadAsync(client, new Uri("https://github.com/ggml-org/whisper.cpp/releases/download/" + Revision + "/" + archive.Name), zip, archive, progress, ct);
                await ExtractAsync(zip, runtime, files, progress, ct);
            }
            finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
        }
        PlainDirectory(Path.Combine(root, "models"));
        if (!await ModelDownload.MatchesAsync(ModelPath(root), Model, ct))
            await ModelDownload.InstallAsync(Path.Combine(root, "models"), new[] { Model }, new Uri("https://huggingface.co/ggerganov/whisper.cpp/resolve/" + ModelRevision + "/"), client, progress, ct);
        await VerifyAsync(root, mode, ct); progress.Report(new(1, 1, "보정 엔진 파일 준비 완료"));
    }
    internal static async Task DownloadAsync(HttpClient client, Uri uri, string target, ModelFile expected, IProgress<ModelProgress> progress, CancellationToken token)
    {
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token); response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long size && size != expected.Length) throw new IOException("다운로드 크기가 예상과 달라 중단했어요.");
        await using var input = await response.Content.ReadAsStreamAsync(token);
        await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
        {
            long total = 0; var buffer = new byte[131072]; int count;
            while ((count = await input.ReadAsync(buffer, token)) > 0)
            {
                total += count; if (total > expected.Length) throw new IOException("다운로드 파일이 예상 크기를 초과했어요.");
                await output.WriteAsync(buffer.AsMemory(0, count), token); progress.Report(new(total, expected.Length, "보정 엔진 받는 중"));
            }
        }
        if (!await ModelDownload.MatchesAsync(target, expected, token)) throw new IOException("보정 엔진 파일 검증에 실패했어요. 다시 시도해 주세요.");
    }
    // Called only after the whole upstream archive has passed its pinned digest.
    internal static async Task ExtractAsync(string zip, string destination, IReadOnlyList<ModelFile> files, IProgress<ModelProgress> progress, CancellationToken token)
    {
        SafeFiles(files); PlainDirectory(destination);
        var final = Path.GetFullPath(destination); var parent = Path.GetDirectoryName(final)!; Directory.CreateDirectory(parent);
        var stage = Path.Combine(parent, ".whisper-extract-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
        string? backup = null;
        try
        {
            using (var archive = ZipFile.OpenRead(zip))
            {
                var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in archive.Entries)
                {
                    if (entry.FullName == "Release/" && entry.Length == 0) continue;
                    var parts = entry.FullName.Split('/');
                    if (parts.Length != 2 || parts[0] != "Release" || parts[1].Length == 0 || parts[1] is "." or ".." || parts[1].IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !entries.TryAdd(parts[1], entry))
                        throw new IOException("보정 엔진 압축 파일의 경로가 올바르지 않아요.");
                }
                long done = 0; long total = files.Sum(x => x.Length);
                foreach (var file in files)
                {
                    token.ThrowIfCancellationRequested();
                    if (!entries.TryGetValue(file.Name, out var entry) || entry.Length != file.Length) throw new IOException("보정 엔진 파일 구성이 예상과 달라요.");
                    await using var input = entry.Open();
                    var target = Path.Combine(stage, file.Name);
                    await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
                    {
                        long received = 0; var buffer = new byte[131072]; int count;
                        while ((count = await input.ReadAsync(buffer, token)) > 0)
                        {
                            received += count; if (received > file.Length) throw new IOException("보정 엔진 압축 해제 크기가 예상과 달라요.");
                            await output.WriteAsync(buffer.AsMemory(0, count), token);
                        }
                    }
                    if (!await ModelDownload.MatchesAsync(target, file, token)) throw new IOException("보정 엔진 파일의 해시가 일치하지 않아요.");
                    done += file.Length; progress.Report(new(done, total, "보정 엔진 파일 검증 중"));
                }
            }
            token.ThrowIfCancellationRequested();
            if (Directory.Exists(final)) { backup = final + ".backup-" + Guid.NewGuid().ToString("N"); Directory.Move(final, backup); }
            try { Directory.Move(stage, final); }
            catch { if (backup != null && !Directory.Exists(final)) Directory.Move(backup, final); throw; }
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }
}
