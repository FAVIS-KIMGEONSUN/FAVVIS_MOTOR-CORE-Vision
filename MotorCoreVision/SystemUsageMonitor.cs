using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Microsoft.VisualBasic.Devices;

namespace FAVIS
{
    public sealed class SystemUsageMonitor : IDisposable
    {
        private readonly PerformanceCounter _cpuCounter =
            new PerformanceCounter("Processor", "% Processor Time", "_Total");
        private readonly PerformanceCounter _memoryCounter =
            new PerformanceCounter("Memory", "% Committed Bytes In Use");
        private readonly ComputerInfo _computerInfo = new ComputerInfo();
        private readonly List<PerformanceCounter> _gpuCounters =
            new List<PerformanceCounter>();

        public SystemUsageSnapshot Read()
        {
            RefreshGpuCounters();

            double cpuUsage = ReadCounter(_cpuCounter);
            double memoryUsage = ReadCounter(_memoryCounter);
            double gpuUsage = 0;
            foreach (PerformanceCounter gpuCounter in _gpuCounters)
            {
                gpuUsage += ReadCounter(gpuCounter);
            }

            return new SystemUsageSnapshot
            {
                CpuUsage = ClampPercentage(cpuUsage),
                GpuUsage = ClampPercentage(gpuUsage),
                MemoryUsage = ClampPercentage(memoryUsage),
                UsedMemoryGb = (_computerInfo.TotalPhysicalMemory -
                                _computerInfo.AvailablePhysicalMemory) /
                               (1024d * 1024d * 1024d),
                Drives = ReadDriveUsage()
            };
        }

        private void RefreshGpuCounters()
        {
            DisposeGpuCounters();

            try
            {
                PerformanceCounterCategory category = new PerformanceCounterCategory("GPU Engine");
                foreach (string instanceName in category.GetInstanceNames())
                {
                    if (!instanceName.Contains("engtype_Cuda"))
                    {
                        continue;
                    }

                    _gpuCounters.Add(new PerformanceCounter(
                        "GPU Engine",
                        "Utilization Percentage",
                        instanceName));
                }
            }
            catch (InvalidOperationException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (Win32Exception)
            {
            }
        }

        private static double ReadCounter(PerformanceCounter counter)
        {
            try
            {
                return counter.NextValue();
            }
            catch (InvalidOperationException)
            {
                return 0;
            }
            catch (UnauthorizedAccessException)
            {
                return 0;
            }
            catch (Win32Exception)
            {
                return 0;
            }
        }

        private static Dictionary<string, DriveUsage> ReadDriveUsage()
        {
            var drives = new Dictionary<string, DriveUsage>(StringComparer.OrdinalIgnoreCase);
            foreach (string driveName in new[] { "C", "D" })
            {
                try
                {
                    DriveInfo drive = new DriveInfo(driveName + ":\\");
                    if (!drive.IsReady || drive.TotalSize <= 0)
                    {
                        continue;
                    }

                    long usedBytes = drive.TotalSize - drive.AvailableFreeSpace;
                    drives[driveName] = new DriveUsage
                    {
                        Percentage = usedBytes * 100d / drive.TotalSize,
                        UsedGb = usedBytes / (1024d * 1024d * 1024d)
                    };
                }
                catch (DriveNotFoundException)
                {
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            return drives;
        }

        private static double ClampPercentage(double value)
        {
            return Math.Max(0, Math.Min(100, value));
        }

        private void DisposeGpuCounters()
        {
            foreach (PerformanceCounter gpuCounter in _gpuCounters)
            {
                gpuCounter.Dispose();
            }

            _gpuCounters.Clear();
        }

        public void Dispose()
        {
            _cpuCounter.Dispose();
            _memoryCounter.Dispose();
            DisposeGpuCounters();
        }
    }

    public sealed class SystemUsageSnapshot
    {
        public double CpuUsage { get; set; }
        public double GpuUsage { get; set; }
        public double MemoryUsage { get; set; }
        public double UsedMemoryGb { get; set; }
        public Dictionary<string, DriveUsage> Drives { get; set; }
    }

    public sealed class DriveUsage
    {
        public double Percentage { get; set; }
        public double UsedGb { get; set; }
    }
}
