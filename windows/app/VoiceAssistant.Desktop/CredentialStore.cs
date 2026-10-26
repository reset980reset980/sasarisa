using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace VoiceAssistant.Desktop;

// Read only this application's exact generic credential. Never enumerate other credentials.
public static class CredentialStore
{
    const uint Generic = 1;
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct Credential
    {
        public uint Flags, Type;
        public string TargetName, Comment;
        public FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias, UserName;
    }
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] static extern bool WriteNative(ref Credential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] static extern bool ReadNative(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] static extern bool DeleteNative(string target, uint type, uint flags);
    [DllImport("advapi32.dll")] static extern void CredFree(IntPtr credential);
    static void ValidateTarget(string target)
    {
        if (!target.StartsWith("VoiceAssistantWindows:api:", StringComparison.Ordinal)) throw new ArgumentException("비서의 API 키만 관리할 수 있어요.");
    }
    public static void Save(string target, string key)
    {
        ValidateTarget(target);
        key = key.Trim();
        if (string.IsNullOrEmpty(key) || key.Length > 1280 || key.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
            throw new ArgumentException("API 키를 한 줄로 입력하세요. 최대 1280자입니다.");
        var pointer = Marshal.StringToCoTaskMemUni(key);
        try
        {
            var credential = new Credential { Type = Generic, TargetName = target, Comment = "자비스 Windows API", CredentialBlobSize = (uint)(key.Length * 2), CredentialBlob = pointer, Persist = 2, UserName = "API" };
            if (!WriteNative(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows에 API 키를 저장하지 못했어요.");
        }
        finally { Marshal.ZeroFreeCoTaskMemUnicode(pointer); }
    }
    public static string? Read(string target)
    {
        ValidateTarget(target);
        if (!ReadNative(target, Generic, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 1168) return null;
            throw new Win32Exception(error, "Windows에 저장된 API 키를 읽지 못했어요.");
        }
        try { var credential = Marshal.PtrToStructure<Credential>(pointer); return Marshal.PtrToStringUni(credential.CredentialBlob, checked((int)credential.CredentialBlobSize / 2)); }
        finally { CredFree(pointer); }
    }
    public static void Delete(string target)
    {
        ValidateTarget(target);
        if (!DeleteNative(target, Generic, 0) && Marshal.GetLastWin32Error() != 1168)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows에서 API 키를 삭제하지 못했어요.");
    }
}
