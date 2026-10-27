using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VoiceAssistant.Desktop;

// Closing this job kills the owned server even if the assistant crashes.
internal sealed class WhisperProcessJob : IDisposable
{
    IntPtr handle;
    [StructLayout(LayoutKind.Sequential)] struct BasicLimits { public long UserTime, JobTime; public uint Flags; public UIntPtr MinWorkingSet, MaxWorkingSet; public uint ActiveProcesses; public UIntPtr Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] struct Limits { public BasicLimits Basic; public IoCounters Io; public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetInformationJobObject(IntPtr job, int informationClass, ref Limits limits, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool CloseHandle(IntPtr handle);
    [DllImport("iphlpapi.dll", SetLastError = true)] static extern uint GetExtendedTcpTable(IntPtr table, ref uint size, bool order, uint family, int tableClass, uint reserved);
    public WhisperProcessJob(Process process)
    {
        handle = CreateJobObject(IntPtr.Zero, null);
        try
        {
            var limits = new Limits { Basic = new BasicLimits { Flags = 0x2000 } };
            if (handle == IntPtr.Zero || !SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf<Limits>()) || !AssignProcessToJobObject(handle, process.Handle)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch { Dispose(); throw; }
    }
    public static bool OwnsListener(int pid, int port)
    {
        uint size = 0; _ = GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 3, 0);
        if (size < 4 || size > 16 * 1024 * 1024) return false;
        var memory = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetExtendedTcpTable(memory, ref size, false, 2, 3, 0) != 0) return false;
            int count = Marshal.ReadInt32(memory);
            if (count < 0 || 4L + count * 24L > size) return false;
            for (int i = 0; i < count; i++)
            {
                var row = IntPtr.Add(memory, 4 + i * 24);
                if (Marshal.ReadInt32(row) == 2 && Marshal.ReadInt32(row, 4) == 0x0100007f &&
                    (Marshal.ReadByte(row, 8) * 256 + Marshal.ReadByte(row, 9)) == port && Marshal.ReadInt32(row, 20) == pid) return true;
            }
            return false;
        }
        finally { Marshal.FreeHGlobal(memory); }
    }
    public void Dispose() { if (handle != IntPtr.Zero) { CloseHandle(handle); handle = IntPtr.Zero; } }
}
