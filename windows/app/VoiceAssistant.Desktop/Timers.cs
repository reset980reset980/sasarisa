using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace VoiceAssistant.Desktop;

public sealed record SavedTimer(Guid Id, DateTimeOffset Due, string Label);

public sealed class TimerStore
{
    readonly string path;
    List<SavedTimer> items = new();
    public IReadOnlyList<SavedTimer> Items => items;
    public TimerStore(string? file = null)
    {
        path = file ?? Path.Combine(AppPaths.Home, "timers.json");
        if (File.Exists(path)) try { items = JsonSerializer.Deserialize<List<SavedTimer>>(File.ReadAllText(path), SettingsStore.Json) ?? new(); } catch (Exception ex) { AppPaths.Log("timers", ex.Message); }
    }
    void Save(List<SavedTimer> next)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".new", JsonSerializer.Serialize(next, SettingsStore.Json));
        if (File.Exists(path)) File.Replace(path + ".new", path, path + ".backup"); else File.Move(path + ".new", path);
        items = next;
    }
    public SavedTimer Add(int seconds, string label) { ActionPolicy.Validate(new("timer", label, seconds)); var timer = new SavedTimer(Guid.NewGuid(), DateTimeOffset.Now.AddSeconds(seconds), label); Save(items.Append(timer).ToList()); return timer; }
    public bool Remove(Guid id) { if (!items.Any(x => x.Id == id)) return false; Save(items.Where(x => x.Id != id).ToList()); return true; }
    public void Clear() => Save(new());
    public IReadOnlyList<SavedTimer> TakeDue(DateTimeOffset now)
    {
        var due = items.Where(x => x.Due <= now).ToArray();
        if (due.Length > 0) Save(items.Where(x => x.Due > now).ToList());
        return due;
    }
}
