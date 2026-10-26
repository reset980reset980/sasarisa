using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace VoiceAssistant.Desktop;

public sealed record DistributionFile(string Path, long Size, string Sha256);
public sealed record DistributionManifest(string ProductId, string Version, List<DistributionFile> Files);
public static class UpgradeBackup
{
    public const string ProductId = "7f114a1b-b332-4d79-98b4-fe4092418d4d";
    public static bool StopExisting()
    {
        try { using var signal = EventWaitHandle.OpenExisting(AppPaths.InstancePrefix + ".Quit"); signal.Set(); } catch (WaitHandleCannotBeOpenedException) { }
        if (!Mutex.TryOpenExisting(AppPaths.InstancePrefix + ".Local", out var existing)) return true;
        using (existing)
        {
            var acquired = false;
            try { try { acquired = existing.WaitOne(TimeSpan.FromSeconds(12)); } catch (AbandonedMutexException) { acquired = true; } return acquired; }
            finally { if (acquired) existing.ReleaseMutex(); }
        }
    }
    internal static string OwnedPath(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':')) throw new InvalidDataException("Invalid distribution path");
        var target = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!target.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Distribution path escapes installation");
        for (var item = target; item.Length >= fullRoot.TrimEnd(Path.DirectorySeparatorChar).Length; item = Path.GetDirectoryName(item)!)
        { if ((File.Exists(item) || Directory.Exists(item)) && File.GetAttributes(item).HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("Linked installation paths cannot be archived"); if (Path.GetDirectoryName(item) == null) break; }
        return target;
    }
    public static string Create(string root, string home)
    {
        var manifestPath = OwnedPath(root, "distribution.json");
        if (!File.Exists(manifestPath) || new FileInfo(manifestPath).Length > 4_000_000) throw new InvalidDataException("설치 목록이 없어 업데이트 백업을 만들지 못했어요.");
        var manifest = JsonSerializer.Deserialize<DistributionManifest>(File.ReadAllText(manifestPath), SettingsStore.Json) ?? throw new InvalidDataException("Invalid manifest");
        if (manifest.ProductId != ProductId || manifest.Files == null || manifest.Files.Count is < 1 or > 10000 || !Version.TryParse(manifest.Version, out _)) throw new InvalidDataException("Invalid installation manifest");
        var paths = manifest.Files.Select(file => (Relative: file.Path, Full: OwnedPath(root, file.Path))).ToArray();
        if (paths.Select(x => x.Full).Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length) throw new InvalidDataException("Duplicate installation paths");
        var backups = Path.Combine(Path.GetFullPath(home), "install-backups"); Directory.CreateDirectory(backups);
        var result = Path.Combine(backups, "Sasarisa-" + manifest.Version + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6] + ".zip"); var temp = result + ".partial";
        var missing = new List<string>(); int count = 0;
        try
        {
            using (var archive = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                foreach (var path in paths) { if (File.Exists(path.Full)) { archive.CreateEntryFromFile(path.Full, "app/" + path.Relative.Replace('\\', '/'), CompressionLevel.Fastest); count++; } else missing.Add(path.Relative); }
                archive.CreateEntryFromFile(manifestPath, "app/distribution.json", CompressionLevel.Fastest); count++;
                foreach (var name in new[] { "config.json", "personal.json", "apps.json", "timers.json", "echo-calibration.json", "name-training.json" })
                { var file = OwnedPath(home, name); if (File.Exists(file)) { archive.CreateEntryFromFile(file, "user-data/" + name, CompressionLevel.Fastest); count++; } }
                using var writer = new StreamWriter(archive.CreateEntry("backup-info.json").Open());
                writer.Write(JsonSerializer.Serialize(new { manifest.Version, OriginalRoot = Path.GetFullPath(root), Created = DateTimeOffset.UtcNow, MissingFiles = missing }, SettingsStore.Json)); count++;
            }
            using (var check = ZipFile.OpenRead(temp)) { if (check.Entries.Count != count) throw new IOException("업데이트 백업을 확인하지 못했어요."); }
            File.Move(temp, result); return result;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
