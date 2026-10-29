using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VoiceAssistant.Desktop;

// Closing this job kills the owned server even if the assistant crashes.
internal sealed class WhisperProcessJob : IDisposable
{
    const uint AfInet = 2;
    const int TcpTableOwnerPidListener = 3;
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
        return InspectListener(pid, port).Owns;
    }
    internal sealed record ListenerResult(bool Owns, uint Status, int Rows, string Matching, int Queries);
    internal delegate uint TableReader(IntPtr memory, ref uint size);
    internal static ListenerResult InspectListener(int pid, int port, TableReader? reader = null)
    {
        // TCP_TABLE_OWNER_PID_LISTENER already filters out connected/non-listening
        // endpoints. Keep that OS filter and require the exact loopback address,
        // port and owned PID. dwState is inconsistent on this Windows build even
        // for an endpoint returned in this filtered table (native regression checked).
        // https://learn.microsoft.com/windows/win32/api/iprtrmib/ne-iprtrmib-tcp_table_class
        reader ??= (IntPtr memory, ref uint length) => GetExtendedTcpTable(memory, ref length, false, AfInet, TcpTableOwnerPidListener, 0);
        uint size = 0; var status = reader(IntPtr.Zero, ref size);
        if (status != 122 && status != 0) return new(false, status, 0, "size query failed", 1);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (size < 4 || size > 16 * 1024 * 1024) return new(false,status,0,"invalid size",attempt+1);
            var capacity = size; var memory = Marshal.AllocHGlobal((int)capacity);
            try
            {
                status = reader(memory, ref size);
                if (status == 122) { if (size > capacity) continue; return new(false,status,0,"buffer did not grow",attempt+2); }
                if (status != 0) return new(false,status,0,"table query failed",attempt+2);
                int count = Marshal.ReadInt32(memory);
                if (size > capacity || count < 0 || 4L + count * 24L > size) return new(false,status,count,"invalid row bounds",attempt+2);
                string matching = "no matching port";
                for (int i = 0; i < count; i++)
                {
                    var row = IntPtr.Add(memory, 4 + i * 24);
                    if ((Marshal.ReadByte(row,8)*256 + Marshal.ReadByte(row,9)) != port) continue;
                    var state = Marshal.ReadInt32(row); var address = Marshal.ReadInt32(row,4); var owner = Marshal.ReadInt32(row,20);
                    matching = $"state={state}, loopback={address == 0x0100007f}, pid={owner}";
                    if (address == 0x0100007f && owner == pid) return new(true,status,count,matching,attempt+2);
                }
                return new(false,status,count,matching,attempt+2);
            }
            finally { Marshal.FreeHGlobal(memory); }
        }
        return new(false,status,0,"buffer kept growing",4);
    }
    public void Dispose() { if (handle != IntPtr.Zero) { CloseHandle(handle); handle = IntPtr.Zero; } }
}
