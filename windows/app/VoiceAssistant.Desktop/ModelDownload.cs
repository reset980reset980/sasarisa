using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

public sealed record ModelFile(string Name, long Length, string Sha256);
public sealed record ModelProgress(long Downloaded, long Total, string Stage)
{ public double Percent => Total > 0 ? Math.Clamp(Downloaded * 100.0 / Total, 0, 100) : 0; }
public static class ModelDownload
{
    public const string Revision = "ba6078bca4daf3f0dd37f79d0ab505af71df14a6";
    public static readonly Uri Source = new("https://huggingface.co/k2-fsa/" + AppPaths.ModelName + "/resolve/" + Revision + "/");
    public static readonly IReadOnlyList<ModelFile> Files = new[]
    {
        new ModelFile("encoder-epoch-99-avg-1.int8.onnx",126968852,"8d0b1aa24fbedd4e3948564ab7facd151b8ce9b0c48fc987c541de2de3af5697"),
        new ModelFile("decoder-epoch-99-avg-1.int8.onnx",2844692,"68ea197936aabd249f38b53a87c775422bca64428ad4427d0e6e8092593e71fb"),
        new ModelFile("joiner-epoch-99-avg-1.int8.onnx",2581421,"128b80a66a1f718488af8560f9d15895109b99ff3e573f0a0130e03774ef1ced"),
        new ModelFile("bpe.model",314212,"1491bc92c47dfda4225f5b8930fba3cfa34c3b1ccd25e7d96c630a262f3e918d"),
        new ModelFile("tokens.txt",60246,"016bdf0965029263b7ad01b742366ee542ef0bef38261510e8176ff6f2e9e668")
    };
    public static async Task<bool> MatchesAsync(string path, ModelFile file, CancellationToken token)
    {
        if (!System.IO.File.Exists(path) || new FileInfo(path).Length != file.Length) return false;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase);
    }
    public static async Task InstallAsync(IProgress<ModelProgress> progress, CancellationToken token)
    {
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Sasarisa-Windows/0.2");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(TimeSpan.FromMinutes(25));
        await InstallAsync(AppPaths.UserModel, Files, Source, client, progress, limit.Token);
    }
    internal static async Task InstallAsync(string destination, IReadOnlyList<ModelFile> files, Uri source, HttpClient client, IProgress<ModelProgress> progress, CancellationToken token)
    {
        var final = Path.GetFullPath(destination); var parent = Path.GetDirectoryName(final)!; Directory.CreateDirectory(parent);
        if (files.Count == 0 || files.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count || files.Any(x => string.IsNullOrWhiteSpace(x.Name) || x.Name is "." or ".." || Path.GetFileName(x.Name) != x.Name || x.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || x.Length <= 0 || x.Sha256.Length != 64 || !x.Sha256.All(Uri.IsHexDigit))) throw new ArgumentException("모델 파일 목록이 올바르지 않아요.");
        var stage = Path.Combine(parent, ".model-download-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
        var total = files.Sum(f => f.Length); long finished = 0; string? backup = null;
        try
        {
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested(); var target = Path.Combine(stage, file.Name); var existing = Path.Combine(final, file.Name);
                progress.Report(new(finished, total, "모델 파일 확인 중"));
                if (await MatchesAsync(existing, file, token)) System.IO.File.Copy(existing, target);
                else
                {
                    using var response = await client.GetAsync(new Uri(source, file.Name), HttpCompletionOption.ResponseHeadersRead, token); response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentLength is long length && length != file.Length) throw new IOException("모델 파일 크기가 예상과 달라 설치를 멈췄어요.");
                    await using var input = await response.Content.ReadAsStreamAsync(token);
                    await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
                    {
                        var buffer = new byte[131072]; long received = 0; int count;
                        while ((count = await input.ReadAsync(buffer, token)) > 0)
                        {
                            token.ThrowIfCancellationRequested(); received += count; if (received > file.Length) throw new IOException("모델 파일이 예상 크기를 초과했어요.");
                            await output.WriteAsync(buffer.AsMemory(0, count), token); progress.Report(new(finished + received, total, "한국어 모델 받는 중"));
                        }
                    }
                    if (!await MatchesAsync(target, file, token)) throw new IOException("모델 파일 검증에 실패했어요. 기존 모델은 유지됩니다. 다시 시도하세요.");
                }
                finished += file.Length;
            }
            token.ThrowIfCancellationRequested(); progress.Report(new(total, total, "검증한 모델 적용 중"));
            if (Directory.Exists(final)) { backup = final + ".backup-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]; Directory.Move(final, backup); }
            try { Directory.Move(stage, final); }
            catch { if (backup != null && !Directory.Exists(final)) Directory.Move(backup, final); throw; }
            progress.Report(new(total, total, "한국어 모델 준비 완료"));
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }
}
