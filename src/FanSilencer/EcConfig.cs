using System;
using System.IO;
using System.Text.Json;

namespace FanSilencer;

public class EcConfig
{
    public int TelemetryPollMs { get; set; } = 1000;
    public int DefaultMode { get; set; } = 6;    // 启动后自动应用的模式：6=自定义曲线（覆盖 CC3.0）
    public bool ApplyModeOnStart { get; set; } = true;
    public int HotCpuTemp { get; set; } = 97;    // 自定义曲线下 CPU 达到此温度自动切全速（紧急兜底）
    public int HotGpuTemp { get; set; } = 97;    // GPU 同
    public int CoolCpuTemp { get; set; } = 90;   // 双双回落后自动恢复曲线
    public int CoolGpuTemp { get; set; } = 90;

    public static EcConfig Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                };
                return JsonSerializer.Deserialize<EcConfig>(File.ReadAllText(path), options) ?? new EcConfig();
            }
        }
        catch (Exception ex)
        {
            Logger.Error("配置读取失败，使用默认配置: " + ex.Message);
        }
        return new EcConfig();
    }
}
