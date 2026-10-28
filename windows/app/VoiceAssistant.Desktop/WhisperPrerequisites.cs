using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;

namespace VoiceAssistant.Desktop;

internal sealed record WhisperPrerequisiteStatus(bool Ready, string Detail);
internal sealed class WhisperPrerequisiteException(string detail) : IOException(detail);

internal static class WhisperPrerequisites
{
    internal static readonly string[] Libraries = { "msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll", "vcomp140.dll" };
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibraryEx(string file, IntPtr reserved, uint flags);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool FreeLibrary(IntPtr module);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)] static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("kernel32.dll", EntryPoint = "GetProcAddress", ExactSpelling = true)] static extern IntPtr GetProcAddressOrdinal(IntPtr module, IntPtr ordinal);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetThreadErrorMode(uint mode, out uint oldMode);
    const string Repair = "Microsoft Visual C++ x64 구성 요소를 준비해야 해요. ‘Microsoft 구성 요소 설치·복구’를 누른 뒤 다시 확인해 주세요.";
    public static WhisperPrerequisiteStatus CheckSystem() => CheckSystem(Environment.SystemDirectory);
    internal static WhisperPrerequisiteStatus CheckSystem(string directory)
    {
        var modules = new Dictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase);
        bool changed = SetThreadErrorMode(0x8001, out var old);
        try
        {
            foreach (var name in Libraries)
            {
                // Absolute system paths exclude PATH, the current directory and app-local impostors.
                var path = Path.Combine(Path.GetFullPath(directory), name);
                var module = LoadLibraryEx(path, IntPtr.Zero, 0x800 /* LOAD_LIBRARY_SEARCH_SYSTEM32 */);
                if (module == IntPtr.Zero) return new(false, Repair + "\n확인할 파일: " + name);
                modules.Add(name, module);
            }
            return new(true, "Microsoft Visual C++ x64 구성 요소를 불러왔어요.");
        }
        finally { foreach (var module in modules.Values) FreeLibrary(module); if (changed) SetThreadErrorMode(old, out _); }
    }
    public static void RequireSystem()
    {
        var status = CheckSystem(); if (!status.Ready) throw new WhisperPrerequisiteException(status.Detail);
    }
    // Check the actual pinned native imports, so an old runtime is not called ready just because its files exist.
    public static void RequireImports(string runtime) => RequireImports(runtime, Environment.SystemDirectory);
    internal static void RequireImports(string runtime, string system)
    {
        var modules = new Dictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase);
        bool changed = SetThreadErrorMode(0x8001, out var old);
        try
        {
            foreach (var file in Directory.EnumerateFiles(runtime).Where(x => Path.GetExtension(x) is ".dll" or ".exe"))
                foreach (var import in ReadImports(file))
                {
                    if (!Libraries.Contains(import.Library, StringComparer.OrdinalIgnoreCase)) continue;
                    if (!modules.TryGetValue(import.Library, out var module))
                    {
                        module = LoadLibraryEx(Path.Combine(Path.GetFullPath(system), import.Library), IntPtr.Zero, 0x800);
                        if (module == IntPtr.Zero) throw new WhisperPrerequisiteException(Repair + "\n확인할 파일: " + import.Library);
                        modules.Add(import.Library, module);
                    }
                    if (import.Name != null ? GetProcAddress(module, import.Name) == IntPtr.Zero : GetProcAddressOrdinal(module, new IntPtr(import.Ordinal)) == IntPtr.Zero)
                        throw new WhisperPrerequisiteException(Repair + "\n현재 " + import.Library + "에서 필요한 함수를 찾지 못했어요.");
                }
        }
        finally { foreach (var module in modules.Values) FreeLibrary(module); if (changed) SetThreadErrorMode(old, out _); }
    }
    internal sealed record Import(string Library, string? Name, ushort Ordinal = 0);
    internal static IReadOnlyList<Import> ReadImports(string path)
    {
        using var file = File.OpenRead(path); using var pe = new PEReader(file);
        if (pe.PEHeaders.PEHeader is not { Magic: PEMagic.PE32Plus } header) throw new IOException("64비트 음성 보정 파일을 확인해 주세요.");
        var result = new List<Import>(); var directory = header.ImportTableDirectory;
        if (directory.RelativeVirtualAddress == 0) return result;
        var descriptors = pe.GetSectionData(directory.RelativeVirtualAddress).GetReader();
        string Name(int rva)
        {
            var reader = pe.GetSectionData(rva).GetReader(); var bytes = new List<byte>();
            for (int n = 0; n < 4096 && reader.RemainingBytes > 0; n++) { var value = reader.ReadByte(); if (value == 0) return Encoding.ASCII.GetString(bytes.ToArray()); bytes.Add(value); }
            throw new IOException("보정 엔진의 함수 이름을 읽지 못했어요.");
        }
        for (int descriptor = 0; descriptor < 1024 && descriptors.RemainingBytes >= 20; descriptor++)
        {
            uint lookup = descriptors.ReadUInt32(); _ = descriptors.ReadUInt32(); _ = descriptors.ReadUInt32(); uint name = descriptors.ReadUInt32(); uint address = descriptors.ReadUInt32();
            if (name == 0 && lookup == 0 && address == 0) return result;
            var library = Name(checked((int)name));
            if (!Libraries.Contains(library, StringComparer.OrdinalIgnoreCase)) continue;
            var thunks = pe.GetSectionData(checked((int)(lookup == 0 ? address : lookup))).GetReader();
            bool ended = false;
            for (int n = 0; n < 32768 && thunks.RemainingBytes >= 8; n++)
            {
                ulong thunk = thunks.ReadUInt64(); if (thunk == 0) { ended = true; break; }
                if ((thunk & 0x8000000000000000UL) != 0) result.Add(new(library, null, (ushort)(thunk & 0xffff)));
                else result.Add(new(library, Name(checked((int)thunk + 2))));
            }
            if (!ended) throw new IOException("보정 엔진의 함수 목록이 올바르지 않아요.");
        }
        throw new IOException("보정 엔진의 의존성 목록이 올바르지 않아요.");
    }
}
