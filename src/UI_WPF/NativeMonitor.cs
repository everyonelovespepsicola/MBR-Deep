using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MBRDeepDrawer
{
    public static class NativeMonitor
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct FILETIME
        {
            public uint dwLowDateTime;
            public uint dwHighDateTime;

            public ulong ToUInt64()
            {
                return ((ulong)dwHighDateTime << 32) | dwLowDateTime;
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetSystemTimes(out FILETIME lpIdleTime, out FILETIME lpKernelTime, out FILETIME lpUserTime);

        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORYSTATUSEX
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

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        private static ulong _lastIdleTime = 0;
        private static ulong _lastSystemTime = 0;

        public static double GetCpuUsage()
        {
            if (GetSystemTimes(out FILETIME idleTime, out FILETIME kernelTime, out FILETIME userTime))
            {
                ulong currentIdleTime = idleTime.ToUInt64();
                ulong currentSystemTime = kernelTime.ToUInt64() + userTime.ToUInt64();

                if (_lastSystemTime == 0)
                {
                    // First call: initialize baselines
                    _lastIdleTime = currentIdleTime;
                    _lastSystemTime = currentSystemTime;
                    return 0.0;
                }

                ulong systemDelta = currentSystemTime - _lastSystemTime;
                ulong idleDelta = currentIdleTime - _lastIdleTime;

                _lastIdleTime = currentIdleTime;
                _lastSystemTime = currentSystemTime;

                if (systemDelta == 0) return 0.0;

                return Math.Max(0.0, Math.Min(100.0, ((double)(systemDelta - idleDelta) / systemDelta) * 100.0));
            }
            return 0.0;
        }

        public static (double Percentage, double UsedGB, double TotalGB) GetMemoryUsage()
        {
            MEMORYSTATUSEX memStatus = new MEMORYSTATUSEX();
            memStatus.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));

            if (GlobalMemoryStatusEx(ref memStatus))
            {
                double totalGB = memStatus.ullTotalPhys / (1024.0 * 1024.0 * 1024.0);
                double availGB = memStatus.ullAvailPhys / (1024.0 * 1024.0 * 1024.0);
                double usedGB = totalGB - availGB;
                double percentage = (usedGB / totalGB) * 100.0;

                return (percentage, usedGB, totalGB);
            }
            return (0.0, 0.0, 0.0);
        }

        [DllImport("ntdll.dll")]
        private static extern int NtQuerySystemInformation(int SystemInformationClass, IntPtr SystemInformation, int SystemInformationLength, out int ReturnLength);

        [StructLayout(LayoutKind.Sequential)]
        public struct SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION
        {
            public long IdleTime;
            public long KernelTime;
            public long UserTime;
            public long DpcTime;
            public long InterruptTime;
            public int InterruptCount;
        }

        private static SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION[]? _lastCoreInfo;

        public static double[] GetCoreUsages()
        {
            int coreCount = Environment.ProcessorCount;
            int structSize = Marshal.SizeOf(typeof(SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION));
            int length = coreCount * structSize;
            IntPtr ptr = Marshal.AllocHGlobal(length);
            double[] usages = new double[coreCount];
            int status = NtQuerySystemInformation(8, ptr, length, out _);
            if (status == 0)
            {
                var currentInfo = new SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION[coreCount];
                for (int i = 0; i < coreCount; i++) currentInfo[i] = Marshal.PtrToStructure<SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION>(ptr + (i * structSize));
                if (_lastCoreInfo == null)
                {
                    _lastCoreInfo = currentInfo;
                    Marshal.FreeHGlobal(ptr);
                    return usages;
                }
                for (int i = 0; i < coreCount; i++)
                {
                    long systemDelta = (currentInfo[i].KernelTime - _lastCoreInfo[i].KernelTime) + (currentInfo[i].UserTime - _lastCoreInfo[i].UserTime);
                    if (systemDelta > 0) usages[i] = Math.Max(0.0, Math.Min(100.0, ((double)(systemDelta - (currentInfo[i].IdleTime - _lastCoreInfo[i].IdleTime)) / systemDelta) * 100.0));
                }
                _lastCoreInfo = currentInfo;
            }
            Marshal.FreeHGlobal(ptr);
            return usages;
        }

        private static PerformanceCounter? _diskTimeCounter;
        private static PerformanceCounter? _diskReadCounter;
        private static PerformanceCounter? _diskWriteCounter;

        private static PerformanceCounter[]? _diskTimeCounters;
        private static PerformanceCounter[]? _diskReadCounters;
        private static PerformanceCounter[]? _diskWriteCounters;
        private static string[]? _diskNames;

        private static PerformanceCounter[]? _netRecvCounters;
        private static PerformanceCounter[]? _netSentCounters;

        private static readonly object _initLock = new object();

        public static void InitializeExtraCounters()
        {
            lock (_initLock)
            {
                try
                {
                    if (_diskTimeCounter == null)
                    {
                        _diskTimeCounter = new PerformanceCounter("PhysicalDisk", "% Disk Time", "_Total", true);
                        _diskReadCounter = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total", true);
                        _diskWriteCounter = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total", true);

                        var pccDisk = new PerformanceCounterCategory("PhysicalDisk");
                        var diskInstances = pccDisk.GetInstanceNames();
                        var timeList = new List<PerformanceCounter>();
                        var readList = new List<PerformanceCounter>();
                        var writeList = new List<PerformanceCounter>();
                        var nameList = new List<string>();

                        foreach (var inst in diskInstances)
                        {
                            if (inst.Equals("_Total", StringComparison.OrdinalIgnoreCase)) continue;

                            timeList.Add(new PerformanceCounter("PhysicalDisk", "% Disk Time", inst, true));
                            readList.Add(new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", inst, true));
                            writeList.Add(new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", inst, true));
                            nameList.Add(inst);
                        }

                        _diskTimeCounters = timeList.ToArray();
                        _diskReadCounters = readList.ToArray();
                        _diskWriteCounters = writeList.ToArray();
                        _diskNames = nameList.ToArray();
                    }

                    if (_netRecvCounters == null)
                    {
                        var pcc = new PerformanceCounterCategory("Network Interface");
                        var instances = pcc.GetInstanceNames();
                        var recvList = new List<PerformanceCounter>();
                        var sentList = new List<PerformanceCounter>();

                        foreach (var inst in instances)
                        {
                            // Skip local loopback interfaces so we only measure actual hardware adapters
                            if (inst.Contains("Loopback", StringComparison.OrdinalIgnoreCase)) continue;

                            recvList.Add(new PerformanceCounter("Network Interface", "Bytes Received/sec", inst, true));
                            sentList.Add(new PerformanceCounter("Network Interface", "Bytes Sent/sec", inst, true));
                        }
                        _netRecvCounters = recvList.ToArray();
                        _netSentCounters = sentList.ToArray();
                    }
                }
                catch { }
            }
        }

        public static (double Percentage, double ReadBps, double WriteBps) GetDiskUsage()
        {
            if (_diskTimeCounter == null) return (0.0, 0.0, 0.0);
            try { return (Math.Min(100.0, _diskTimeCounter.NextValue()), _diskReadCounter?.NextValue() ?? 0, _diskWriteCounter?.NextValue() ?? 0); }
            catch { return (0.0, 0.0, 0.0); }
        }

        public static (string Name, double Percentage, double ReadBps, double WriteBps)[] GetDetailedDiskUsages()
        {
            if (_diskNames == null || _diskTimeCounters == null || _diskReadCounters == null || _diskWriteCounters == null)
                return Array.Empty<(string, double, double, double)>();

            var result = new (string Name, double Percentage, double ReadBps, double WriteBps)[_diskNames.Length];
            for (int i = 0; i < _diskNames.Length; i++)
            {
                try
                {
                    result[i] = (
                        _diskNames[i],
                        Math.Min(100.0, _diskTimeCounters[i].NextValue()),
                        _diskReadCounters[i].NextValue(),
                        _diskWriteCounters[i].NextValue()
                    );
                }
                catch { result[i] = (_diskNames[i], 0.0, 0.0, 0.0); }
            }
            return result;
        }

        public static (double RecvBps, double SentBps) GetNetworkUsage()
        {
            if (_netRecvCounters == null || _netSentCounters == null) return (0.0, 0.0);
            try { double r = 0, s = 0; for (int i = 0; i < _netRecvCounters.Length; i++) { r += _netRecvCounters[i].NextValue(); s += _netSentCounters[i].NextValue(); } return (r, s); }
            catch { return (0.0, 0.0); }
        }

        private static PerformanceCounterCategory? _gpuEngineCategory;
        private static Dictionary<string, PerformanceCounter> _gpuCounters = new Dictionary<string, PerformanceCounter>();
        private static PerformanceCounterCategory? _gpuMemoryCategory;
        private static PerformanceCounter[]? _gpuMemoryCounters;

        public static (double Percentage, double MemoryGB) GetGpuUsageAndMemory()
        {
            double memTotal = 0;
            lock (_initLock)
            {
                if (_gpuMemoryCategory == null)
                {
                    try
                    {
                        _gpuMemoryCategory = new PerformanceCounterCategory("GPU Adapter Memory");
                        var memInstances = _gpuMemoryCategory.GetInstanceNames();
                        var counters = new List<PerformanceCounter>();
                        foreach (var inst in memInstances)
                        {
                            counters.Add(new PerformanceCounter("GPU Adapter Memory", "Dedicated Usage", inst));
                        }
                        _gpuMemoryCounters = counters.ToArray();
                    }
                    catch { }
                }
            }

            if (_gpuMemoryCounters != null)
            {
                foreach (var pc in _gpuMemoryCounters)
                {
                    try { memTotal += pc.NextValue(); } catch { }
                }
            }
            double memGB = memTotal / (1024.0 * 1024.0 * 1024.0);

            lock (_initLock)
            {
                if (_gpuEngineCategory == null)
                {
                    try { _gpuEngineCategory = new PerformanceCounterCategory("GPU Engine"); }
                    catch { return (0.0, memGB); }
                }
            }

            double utilTotal = 0;
            try
            {
                var instances = _gpuEngineCategory.GetInstanceNames();
                var currentInstances = new HashSet<string>();

                foreach (var inst in instances)
                {
                    // Focus specifically on 3D acceleration engines
                    if (inst.EndsWith("engtype_3D", StringComparison.OrdinalIgnoreCase))
                    {
                        currentInstances.Add(inst);
                    }
                }

                var keysToRemove = new List<string>();
                foreach (var key in _gpuCounters.Keys)
                {
                    if (!currentInstances.Contains(key)) keysToRemove.Add(key);
                }

                foreach (var key in keysToRemove)
                {
                    _gpuCounters[key].Dispose();
                    _gpuCounters.Remove(key);
                }

                foreach (var inst in currentInstances)
                {
                    if (!_gpuCounters.ContainsKey(inst))
                    {
                        var pc = new PerformanceCounter("GPU Engine", "Utilization Percentage", inst);
                        pc.NextValue(); // Prime the first read
                        _gpuCounters[inst] = pc;
                    }
                    else
                    {
                        try { utilTotal += _gpuCounters[inst].NextValue(); } catch { }
                    }
                }
            }
            catch { }

            return (Math.Min(100.0, utilTotal), memGB);
        }
    }
}
