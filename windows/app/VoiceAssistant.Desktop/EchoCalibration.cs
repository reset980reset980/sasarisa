using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VoiceAssistant.Desktop;

internal sealed class EchoCalibration(string path)
{
    public static string Key(string input, string output) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input + "\n" + output)));
    Dictionary<string, double> Read()
    {
        try
        {
            var values = File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(path)) : null;
            return values is { Count: <= 32 } ? values : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    public double Get(string key) => Read().TryGetValue(key, out var value) && double.IsFinite(value) && value is >= .03 and <= .6 ? value : .12;
    public void Save(string key, double value)
    {
        if (key.Length != 64 || !double.IsFinite(value) || value is < .03 or > .6) return;
        try
        {
            var values = Read();
            if (values.Count >= 32 && !values.ContainsKey(key)) return;
            values[key] = value;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path + ".new", JsonSerializer.Serialize(values)); File.Move(path + ".new", path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppPaths.Log("echo-calibration", ex.Message); }
    }
}
