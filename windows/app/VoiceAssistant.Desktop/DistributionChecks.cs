using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

internal static class DistributionChecks
{
    sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => respond(request, token); }
    sealed class Progress : IProgress<ModelProgress> { public void Report(ModelProgress value) { } }
    static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    static ModelFile Spec(string name, byte[] bytes) => new(name, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)));
    static string Folder() { var path = Path.Combine(AppPaths.WindowsRoot, "artifacts", "distribution-tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    static async Task RejectAsync(Func<Task> action)
    { try { await action(); } catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or HttpRequestException or OperationCanceledException) { return; } throw new Exception("Invalid operation succeeded"); }
    public static async Task<int> RunAsync(List<object> rows)
    {
        int failed = 0;
        async Task Check(string name, Func<Task> action) { try { await action(); rows.Add(new { name, pass = true }); } catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.Message }); } }
        var source = new Uri("https://fixture.invalid/model/"); var bytes = Encoding.UTF8.GetBytes("verified Korean model fixture");
        await Check("Model install verifies files before replacing and reuses valid files", async () =>
        {
            var parent = Folder(); var dest = Path.Combine(parent, "model"); Directory.CreateDirectory(dest); File.WriteAllText(Path.Combine(dest, "old.txt"), "keep");
            var reused = Encoding.UTF8.GetBytes("existing tokenizer"); File.WriteAllBytes(Path.Combine(dest, "tokens.txt"), reused); int requests = 0;
            using var client = new HttpClient(new Handler((request, token) => { requests++; Require(request.RequestUri!.AbsolutePath.EndsWith("encoder.onnx"), "Already valid file downloaded"); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }); }));
            await ModelDownload.InstallAsync(dest, new[] { Spec("encoder.onnx", bytes), Spec("tokens.txt", reused) }, source, client, new Progress(), CancellationToken.None);
            Require(requests == 1 && File.ReadAllBytes(Path.Combine(dest, "encoder.onnx")).SequenceEqual(bytes), "Installed content incorrect");
            var backup = Directory.GetDirectories(parent, "model.backup-*").Single(); Require(File.ReadAllText(Path.Combine(backup, "old.txt")) == "keep" && !File.Exists(Path.Combine(dest, "old.txt")), "Previous model not preserved separately");
            Require(Directory.GetDirectories(parent, ".model-download-*").Length == 0, "Download stage leaked");
        });
        await Check("Model checksum and HTTP failures preserve previous model", async () =>
        {
            foreach (var mode in new[] { "checksum", "length", "http" })
            {
                var parent = Folder(); var dest = Path.Combine(parent, "model"); Directory.CreateDirectory(dest); File.WriteAllText(Path.Combine(dest, "old.txt"), "unchanged");
                var content = mode == "checksum" ? Enumerable.Repeat((byte)0, bytes.Length).ToArray() : new byte[bytes.Length + 1];
                using var client = new HttpClient(new Handler((request, token) => Task.FromResult(new HttpResponseMessage(mode == "http" ? HttpStatusCode.NotFound : HttpStatusCode.OK) { Content = new ByteArrayContent(content) })));
                await RejectAsync(() => ModelDownload.InstallAsync(dest, new[] { Spec("encoder.onnx", bytes) }, source, client, new Progress(), CancellationToken.None));
                Require(File.ReadAllText(Path.Combine(dest, "old.txt")) == "unchanged" && Directory.GetDirectories(parent, ".model-download-*").Length == 0 && Directory.GetDirectories(parent, "model.backup-*").Length == 0, "Failed download changed model or left stage");
            }
        });
        await Check("Model cancellation preserves previous model and removes partial staging", async () =>
        {
            var parent = Folder(); var dest = Path.Combine(parent, "model"); Directory.CreateDirectory(dest); File.WriteAllText(Path.Combine(dest, "old.txt"), "unchanged");
            using var stop = new CancellationTokenSource();
            using var client = new HttpClient(new Handler(async (request, token) => { stop.Cancel(); await Task.Delay(1000, token); return new HttpResponseMessage(HttpStatusCode.OK); }));
            await RejectAsync(() => ModelDownload.InstallAsync(dest, new[] { Spec("encoder.onnx", bytes) }, source, client, new Progress(), stop.Token));
            Require(File.ReadAllText(Path.Combine(dest, "old.txt")) == "unchanged" && Directory.GetDirectories(parent, ".model-download-*").Length == 0, "Cancellation damaged existing model");
        });
        await Check("Model file traversal and duplicate names are rejected before network access", async () =>
        {
            using var client = new HttpClient(new Handler((request, token) => throw new Exception("Unexpected network access")));
            foreach (var specs in new[] { new[] { Spec("../outside", bytes) }, new[] { Spec("..", bytes) }, new[] { Spec("file:stream", bytes) }, new[] { Spec("a.onnx", bytes), Spec("A.onnx", bytes) } })
                await RejectAsync(() => ModelDownload.InstallAsync(Path.Combine(Folder(), "model"), specs, source, client, new Progress(), CancellationToken.None));
        });
        await Check("Update backup includes installed files and settings but excludes unlisted files", () =>
        {
            var root = Folder(); var home = Folder(); Directory.CreateDirectory(Path.Combine(root, "runtime"));
            File.WriteAllBytes(Path.Combine(root, "runtime", "worker.py"), bytes); File.WriteAllText(Path.Combine(root, "unlisted.txt"), "foreign"); File.WriteAllText(Path.Combine(home, "config.json"), "{\"Name\":\"아리\"}");
            File.WriteAllText(Path.Combine(root, "distribution.json"), JsonSerializer.Serialize(new DistributionManifest(UpgradeBackup.ProductId, "0.2.0", new() { new("runtime/worker.py", bytes.Length, Convert.ToHexString(SHA256.HashData(bytes))) })));
            var path = UpgradeBackup.Create(root, home); using var zip = ZipFile.OpenRead(path);
            Require(zip.GetEntry("app/runtime/worker.py") != null && zip.GetEntry("user-data/config.json") != null && zip.GetEntry("app/unlisted.txt") == null && zip.GetEntry("app/distribution.json") != null && zip.GetEntry("backup-info.json") != null, "Backup contents incomplete or included foreign data");
            var restore = Path.Combine(home, "restore"); ZipFile.ExtractToDirectory(path, restore); Require(File.ReadAllBytes(Path.Combine(restore, "app", "runtime", "worker.py")).SequenceEqual(bytes) && File.ReadAllText(Path.Combine(restore, "user-data", "config.json")) == File.ReadAllText(Path.Combine(home, "config.json")), "Restored backup differs");
            Require(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.partial").Length == 0, "Partial backup leaked"); return Task.CompletedTask;
        });
        await Check("Update backup rejects manifest traversal and case-insensitive duplicate paths", async () =>
        {
            foreach (var paths in new[] { new[] { "../outside.txt" }, new[] { "C:\\outside.txt" }, new[] { "file:stream" }, new[] { "a.txt", "A.txt" } })
            {
                var root = Folder(); var home = Folder(); File.WriteAllText(Path.Combine(root, "distribution.json"), JsonSerializer.Serialize(new DistributionManifest(UpgradeBackup.ProductId, "0.2.0", paths.Select(x => new DistributionFile(x, 1, "")).ToList())));
                await RejectAsync(() => { UpgradeBackup.Create(root, home); return Task.CompletedTask; }); Require(!Directory.Exists(Path.Combine(home, "install-backups")), "Invalid manifest produced backup");
            }
        });
        return failed;
    }
}
