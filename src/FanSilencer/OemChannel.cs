using System;
using System.IO;
using System.Runtime.InteropServices;

namespace FanSilencer;

/// <summary>
/// OEM 官方通道：调用已安装的签名 InsydeDCHU.dll（Control Center / FanSpeedSetting 同款路径）。
/// P/Invoke 签名与官方 UWP 程序反编译源码 1:1 对齐：
///   SetWMI(cmd, sub, value)  = SetDCHU_Data(cmd, [value LE 4 字节，第 4 字节覆盖为子命令], 4)
///   SetWMIPackage(cmd, buf)  = SetDCHU_Data(cmd, buf, 256)
///   GetWMIPackage(cmd)       = GetDCHU_Data_Buffer(cmd, 256 字节缓冲)
///   Set/GetAPPData(page,off) = Write/ReadAppSettings(page, offset, len, buf)
/// 底层经 AcpiBridge.sys（KMDF）评估 ACPI _DSM 访问 EC，驱动内部串行化，
/// 应用层不做任何端口 IO 与高频轮询。
/// </summary>
internal static class OemChannel
{
    public const int CmdTelemetry = 12;     // package 12：实时转速/占空比/温度
    public const int CmdFanTable = 13;      // package 13：风扇数量/固件表（读）
    public const int CmdFanTableWrite = 14; // package 14：自定义风扇曲线表（写）
    public const int CmdFanControl = 121;   // 0x79：风扇模式/偏移/默认值等

    public const byte SubFanMode = 1;       // 121/1：切换模式（0=自动 1=全速 3=静音 5=Max-Q 6=自定义）
    public const byte SubLoadDefaults = 34; // 121/34：风扇表恢复固件默认

    private static IntPtr _lib = IntPtr.Zero;
    public static bool Available { get; private set; }
    public static string DllPath { get; private set; } = "";

    public static bool Init()
    {
        if (_lib != IntPtr.Zero) return Available;
        try
        {
            string[] candidates =
            {
                @"C:\Program Files (x86)\ControlCenter\InsydeDCHU.dll",
                Path.Combine(AppContext.BaseDirectory, "InsydeDCHU.dll")
            };
            foreach (var path in candidates)
            {
                if (!File.Exists(path)) continue;
                _lib = NativeLibrary.Load(path);
                DllPath = path;
                Available = true;
                Logger.Info("OEM 通道已加载: " + path);
                break;
            }
            if (!Available) Logger.Error("未找到 InsydeDCHU.dll");
        }
        catch (Exception ex)
        {
            Logger.Error("InsydeDCHU.dll 加载失败: " + ex.Message);
        }
        return Available;
    }

    // ----- 签名与官方 FanSpeedSetting 反编译源码一致 -----

    [DllImport("InsydeDCHU.dll")]
    private static extern int GetDCHU_Data_Integer(int command, ref int data);

    [DllImport("InsydeDCHU.dll")]
    private static extern int SetDCHU_Data(int command, byte[] buffer, int length);

    [DllImport("InsydeDCHU.dll")]
    private static extern int GetDCHU_Data_Buffer(int command, ref byte buffer);

    [DllImport("InsydeDCHU.dll")]
    private static extern int ReadAppSettings(int page, int offset, int length, ref byte buffer);

    [DllImport("InsydeDCHU.dll")]
    private static extern int WriteAppSettings(int page, int offset, int length, ref byte buffer);

    public static int? GetInteger(int command)
    {
        if (!Available) return null;
        try
        {
            int data = 0;
            GetDCHU_Data_Integer(command, ref data);
            return data;
        }
        catch (Exception ex)
        {
            Logger.Error($"GetInteger({command}) 异常: {ex.Message}");
            return null;
        }
    }

    public static int? GetBuffer(int command, byte[] buffer)
    {
        if (!Available) return null;
        try
        {
            return GetDCHU_Data_Buffer(command, ref buffer[0]);
        }
        catch (Exception ex)
        {
            Logger.Error($"GetBuffer({command}) 异常: {ex.Message}");
            return null;
        }
    }

    public static bool SetBuffer(int command, byte[] buffer, int length)
    {
        if (!Available) return false;
        try
        {
            SetDCHU_Data(command, buffer, length);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"SetBuffer({command}) 异常: {ex.Message}");
            return false;
        }
    }

    /// <summary>官方 SetWMI(cmd, sub, value)：4 字节 LE 值 + 第 4 字节被子命令覆盖。</summary>
    public static bool SetWMI(int command, byte subCommand, uint value)
    {
        var buf = BitConverter.GetBytes(value); // 4 字节 LE
        buf[3] = subCommand;
        return SetBuffer(command, buf, 4);
    }

    /// <summary>切换固件风扇模式（官方 SetFanMode）。0=自动 1=全速 3=静音 5=Max-Q 6=自定义。</summary>
    public static bool SetFanMode(int mode) => SetWMI(CmdFanControl, SubFanMode, (uint)mode);

    /// <summary>风扇表恢复固件默认（官方 WMI13_LoadDefault：121/34/1）。</summary>
    public static bool LoadFanDefaults() => SetWMI(CmdFanControl, SubLoadDefaults, 1);

    public static byte[] GetAppData(int page, int offset, int length)
    {
        var buf = new byte[length];
        if (!Available) return buf;
        try { ReadAppSettings(page, offset, length, ref buf[0]); }
        catch (Exception ex) { Logger.Error($"GetAppData 异常: {ex.Message}"); }
        return buf;
    }

    public static bool SetAppData(int page, int offset, byte[] data)
    {
        if (!Available) return false;
        try { WriteAppSettings(page, offset, data.Length, ref data[0]); return true; }
        catch (Exception ex) { Logger.Error($"SetAppData 异常: {ex.Message}"); return false; }
    }
}

/// <summary>package 12 实时遥测解码（DCHU.DEVT 布局，来自逆向文档 + 本机验证）。</summary>
public class OemTelemetry
{
    public DateTime Time;
    public int FanCount;
    public int CpuRpmRaw, Gpu1RpmRaw, Gpu2RpmRaw;
    public int CpuDuty, Gpu1Duty, Gpu2Duty;   // 0-255
    public int CpuTemp, Gpu1Temp, Gpu2Temp;   // °C
    public bool Valid;

    /// <summary>FanSpeedSetting 的转速显示公式：原始周期字段 → 显示 RPM。</summary>
    public static int DisplayRpm(int raw) =>
        raw <= 0 ? 0 : (int)Math.Round(60.0 / (0.00005565217391304348 * raw)) * 2;

    public static OemTelemetry Read()
    {
        var t = new OemTelemetry { Time = DateTime.Now };
        var buf = new byte[256];
        int? rc = OemChannel.GetBuffer(OemChannel.CmdTelemetry, buf);
        if (rc == null) return t;
        t.FanCount = OemChannel.GetInteger(56) ?? 0;
        t.CpuRpmRaw = buf[2] << 8 | buf[3];
        t.Gpu1RpmRaw = buf[4] << 8 | buf[5];
        t.Gpu2RpmRaw = buf[6] << 8 | buf[7];
        t.CpuDuty = buf[16];
        t.Gpu1Duty = buf[19];
        t.Gpu2Duty = buf[22];
        t.CpuTemp = buf[18];
        t.Gpu1Temp = buf[21];
        t.Gpu2Temp = buf[24];
        t.Valid = true;
        return t;
    }
}
