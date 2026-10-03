using System;
using System.Linq;
using LibreHardwareMonitor.Hardware;

namespace FanSilencer;

/// <summary>
/// 软件传感器源（LibreHardwareMonitor）：与游戏加加等监控软件同源——
/// CPU 包温度读 MSR/PECI，GPU 核心温度走显卡驱动官方接口（NVML/ADLX），
/// 从源头避开 EC 遥测刚开机时的整体失真。读取失败时上层自动回退 EC 通道。
/// </summary>
internal static class SensorSource
{
    private static Computer _computer;
    public static bool Available { get; private set; }

    public static void Init()
    {
        try
        {
            _computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = false,
                IsMotherboardEnabled = false,
                IsControllerEnabled = false,
                IsNetworkEnabled = false,
                IsStorageEnabled = false,
                IsPsuEnabled = false
            };
            _computer.Open();
            Available = _computer.Hardware.Any();
            Logger.Info(Available
                ? $"软件传感器已初始化（{_computer.Hardware.Count()} 个设备：CPU/GPU 温度优先走此通道）"
                : "软件传感器初始化完成但未发现设备，温度将回退 EC 通道");
        }
        catch (Exception ex)
        {
            Logger.Error("软件传感器初始化失败（温度将回退 EC 通道）: " + ex.Message);
            Available = false;
        }
    }

    /// <summary>读取 (CPU 包温度, GPU 核心温度)，单位 °C；某项不可用返回 null。</summary>
    public static (int? Cpu, int? Gpu) Read()
    {
        if (!Available) return (null, null);
        try
        {
            int? cpu = null, gpu = null;
            foreach (var hw in _computer.Hardware)
            {
                switch (hw.HardwareType)
                {
                    case HardwareType.Cpu:
                        hw.Update();
                        cpu = CpuPackageTemp(hw) ?? cpu;
                        break;
                    case HardwareType.GpuNvidia:
                    case HardwareType.GpuAmd:
                    case HardwareType.GpuIntel:
                        hw.Update();
                        gpu = GpuCoreTemp(hw) ?? gpu;
                        break;
                }
            }
            return (cpu, gpu);
        }
        catch (Exception ex)
        {
            Logger.Error("软件传感器读取失败: " + ex.Message);
            return (null, null);
        }
    }

    /// <summary>CPU 包温度：优先 Package / Tctl，退而取核心最高温。</summary>
    private static int? CpuPackageTemp(IHardware hw)
    {
        var temps = hw.Sensors.Where(s => s.SensorType == SensorType.Temperature && s.Value.HasValue).ToList();
        if (temps.Count == 0) return null;
        var pkg = temps.FirstOrDefault(s => s.Name == "CPU Package")
               ?? temps.FirstOrDefault(s => s.Name.Contains("Tctl", StringComparison.OrdinalIgnoreCase))
               ?? temps.FirstOrDefault(s => s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase));
        if (pkg != null) return (int)Math.Round(pkg.Value.Value);
        var cores = temps.Where(s => s.Name.StartsWith("Core", StringComparison.OrdinalIgnoreCase)).ToList();
        return cores.Count > 0 ? (int)Math.Round(cores.Max(s => s.Value.Value)) : null;
    }

    /// <summary>GPU 核心温度：优先 "GPU Core"，避开热结点/显存等次要读数。</summary>
    private static int? GpuCoreTemp(IHardware hw)
    {
        var temps = hw.Sensors.Where(s => s.SensorType == SensorType.Temperature && s.Value.HasValue).ToList();
        if (temps.Count == 0) return null;
        var core = temps.FirstOrDefault(s => s.Name.Contains("GPU Core", StringComparison.OrdinalIgnoreCase))
                ?? temps.FirstOrDefault(s => !s.Name.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase)
                                          && !s.Name.Contains("Memory", StringComparison.OrdinalIgnoreCase));
        return core != null ? (int)Math.Round(core.Value.Value) : null;
    }
}
