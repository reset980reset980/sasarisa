using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace VoiceAssistant.Desktop;

internal static class DataFileStamp
{
    internal static string Text(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    internal static string ReadText(string path, out string stamp)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > PersonalDataTransfer.MaxBytes) throw new ArgumentException("저장 파일은 64 MB 이내여야 합니다.");
        stamp = Convert.ToHexString(SHA256.HashData(input)); input.Position = 0;
        using var reader = new StreamReader(input, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true); return reader.ReadToEnd();
    }
    internal static string Read(string path)
    {
        try { using var input = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(input)); }
        catch (FileNotFoundException) { return "missing"; }
        catch (DirectoryNotFoundException) { return "missing"; }
    }
    internal static void Require(string path, string expected)
    {
        if (Read(path) != expected) throw new InvalidOperationException("검토하는 동안 저장 파일이 바뀌었어요. 다시 가져와 변경 내용을 확인하세요.");
    }
}
