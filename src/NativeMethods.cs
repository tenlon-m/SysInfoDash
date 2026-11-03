using System;
using System.Runtime.InteropServices;

namespace SysInfoDash
{
    /// <summary>底层 Win32 调用集合（高 DPI、物理内存查询）。</summary>
    internal static class NativeMethods
    {
        // ---------- 高 DPI ----------
        [DllImport("user32.dll")]
        internal static extern bool SetProcessDPIAware();

        // ---------- 物理内存 (GlobalMemoryStatusEx) ----------
        [StructLayout(LayoutKind.Sequential)]
        internal struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
    }
}
