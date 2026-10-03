using System;
using System.Linq;
using LibreHardwareMonitor.Hardware;

namespace FanSilencer;

/// <summary>
/// 软件传感器源（LibreHardwareMonitor）：GPU 核心温度走显卡驱动官方接口
/// （NVML/ADLX），与游戏加加等监控软件同源，避开 EC 的 GPU 遥测失真与
/// 独显休眠读 0° 的问题。
/// CPU 温度不用软件传感器——CPU"包温度"（芯片结温）在负载突刺时会比
/// 游戏加加显示的温度高 15-20°C，而 EC 的 CPU 温度与风扇固件实际依据
/// 一致、待机时与游戏加加基本吻合，故 CPU 始终以 EC 为准。
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
                IsCpuEnabled = false,
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
                ? $"GPU 软件传感器已初始化（{_computer.Hardware.Count()} 个设备，温度走驱动接口）"
                : "GPU 软件传感器初始化完成但未发现设备，温度将回退 EC 通道");
        }
        catch (Exception ex)
        {
            Logger.Error("GPU 软件传感器初始化失败（温度将回退 EC 通道）: " + ex.Message);
            Available = false;
        }
    }

    /// <summary>读取 GPU 核心温度（°C）；不可用返回 null。</summary>
    public static int? ReadGpu()
    {
        if (!Available) return null;
        try
        {
            int? gpu = null;
            foreach (var hw in _computer.Hardware)
            {
                if (hw.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
                {
                    hw.Update();
                    gpu = GpuCoreTemp(hw) ?? gpu;
                }
            }
            return gpu;
        }
        catch (Exception ex)
        {
            Logger.Error("GPU 软件传感器读取失败: " + ex.Message);
            return null;
        }
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
