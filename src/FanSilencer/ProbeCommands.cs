using System;
using System.IO;
using System.Threading;

namespace FanSilencer;

/// <summary>
/// 探针与 OEM 通道命令：
///   probe oem        —— 只读验证遥测通道（package 12/13 + 整型 getter）
///   probe oem-mode N —— 切换固件风扇模式（0=自动 1=全速 3=静音）
/// </summary>
internal static class ProbeCommands
{
    private static readonly object SayLock = new();

    /// <summary>输出到控制台 + 落盘（UAC 提权后控制台输出拿不到，以文件为准）。</summary>
    private static void Say(string line)
    {
        Console.WriteLine(line);
        try
        {
            lock (SayLock)
                File.AppendAllText(Path.Combine(Logger.LogDir, "probe-output.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff ") + line + Environment.NewLine);
        }
        catch { }
    }

    public static int Run(string[] args)
    {
        if (args.Length >= 2 && args[1] == "oem") return OemProbe(args);
        if (args.Length >= 2 && args[1] == "oem-mode") return OemSetMode(args);
        if (args.Length >= 2 && args[1] == "oem-curve") return OemCurve(args);
        if (args.Length >= 2 && args[1] == "power") return PowerProbe();
        Say("用法: FanSilencer probe oem | oem-mode <0|1|3> | oem-curve show|apply|restore | power");
        return 1;
    }

    /// <summary>只读探测：功耗墙控制可行性（命令 122 支持位 / 包 16、18 / 命令 6 当前值）。</summary>
    private static int PowerProbe()
    {
        Say("== 电源/功耗墙只读探测 ==");
        if (!OemChannel.Init())
        {
            Say("错误：InsydeDCHU.dll 加载失败");
            return 2;
        }

        int? v122 = OemChannel.GetInteger(122);
        bool oc = v122.HasValue && ((v122.Value >> 23) & 1) == 1;
        Say($"  命令122 = {v122}  →  bit23 CPU-OC 支持: {(oc ? "是" : "否")}");
        int? v257 = OemChannel.GetInteger(257);
        Say($"  命令257 (官方 GetPowerMode 候选) = {v257}");
        // 注意：切勿对本命令空间做盲目扫描——标量命令不全是查询，部分是
        // 动作（实测扫到某条命令直接注入了电源键事件导致系统睡眠）。

        var p16 = new byte[256];
        if (OemChannel.GetBuffer(16, p16) != null)
            Say("  包16 头部: " + Convert.ToHexString(p16, 0, 16));
        var p18 = new byte[256];
        if (OemChannel.GetBuffer(18, p18) != null)
            Say("  包18 头部: " + Convert.ToHexString(p18, 0, 16));

        // 官方读法：SetDCHU(6, 子命令) 选择寄存器 → GetInteger(4) 取值
        foreach (var (sub, name) in new[] { (33u, "PL1"), (37u, "PL2"), (41u, "PL时间窗") })
        {
            if (!OemChannel.SetWMI(6, (byte)sub, 0))
            {
                Say($"  {name}: 写选择器失败");
                continue;
            }
            var v = OemChannel.GetInteger(4);
            Say($"  子命令{sub} {name} 当前值: {v}");
        }
        Say("== 探测结束（未做任何写入） ==");
        return 0;
    }

    /// <summary>自定义风扇曲线：show=读固件表；apply=写静音曲线+模式6；restore=还原原厂表+模式0。</summary>
    private static int OemCurve(string[] args)
    {
        string action = args.Length >= 3 ? args[2] : "show";
        if (!OemChannel.Init())
        {
            Say("错误：InsydeDCHU.dll 加载失败");
            return 2;
        }
        if (action == "show" || action == "snapshot")
        {
            var buf = new byte[256];
            if (OemChannel.GetBuffer(OemChannel.CmdFanTable, buf) == null)
            {
                Say("读取风扇表失败");
                return 3;
            }
            // 二进制快照落盘，供前后对比
            string snap = Path.Combine(Logger.LogDir, $"table-{DateTime.Now:HHmmss}.bin");
            File.WriteAllBytes(snap, buf);
            Say($"  CPU : T1={buf[0x10]}°C D1={buf[0x11]}({buf[0x11] * 100 / 255}%)  T2={buf[0x12]}°C D2={buf[0x13]}({buf[0x13] * 100 / 255}%)  T3={buf[0x14]}°C D3={buf[0x15]}({buf[0x15] * 100 / 255}%)");
            Say($"  GPU1: T1={buf[0x18]}°C D1={buf[0x19]}({buf[0x19] * 100 / 255}%)  T2={buf[0x1A]}°C D2={buf[0x1B]}({buf[0x1B] * 100 / 255}%)  T3={buf[0x1C]}°C D3={buf[0x1D]}({buf[0x1D] * 100 / 255}%)");
            Say("  表头 0x00-0x3F: " + Convert.ToHexString(buf, 0, 0x40));
            Say("  快照已保存: " + snap);
            return 0;
        }
        if (action == "apply")
        {
            var before = OemTelemetry.Read();
            Say("== 应用均衡多点曲线，切换前 CPU duty=" + before.CpuDuty + " GPU duty=" + before.Gpu1Duty + " ==");
            if (!CurveManager.Apply(CurveManager.DefaultCpu(), CurveManager.DefaultGpu(), before.CpuTemp, before.Gpu1Temp))
            {
                Say("应用失败");
                return 3;
            }
            for (int i = 0; i < 5; i++)
            {
                Thread.Sleep(2500);
                var t = OemTelemetry.Read();
                Say($"  t+{(i + 1) * 2.5:F0}s: CPU {t.CpuTemp}°C duty={t.CpuDuty}({t.CpuDuty * 100 / 255}%) rpm≈{OemTelemetry.DisplayRpm(t.CpuRpmRaw)} | " +
                    $"GPU {t.Gpu1Temp}°C duty={t.Gpu1Duty}({t.Gpu1Duty * 100 / 255}%) rpm≈{OemTelemetry.DisplayRpm(t.Gpu1RpmRaw)}");
            }
            Say("  如需还原: FanSilencer probe oem-curve restore");
            return 0;
        }
        if (action == "restore")
        {
            Say("== 还原固件默认风扇表 + 自动模式 ==");
            CurveManager.RestoreStock();
            Thread.Sleep(2500);
            var t = OemTelemetry.Read();
            Say($"  现在: CPU {t.CpuTemp}°C duty={t.CpuDuty}({t.CpuDuty * 100 / 255}%) | GPU {t.Gpu1Temp}°C duty={t.Gpu1Duty}({t.Gpu1Duty * 100 / 255}%)");
            return 0;
        }
        Say("未知子命令: " + action);
        return 1;
    }

    private static int ArgInt(string[] args, string name, int def) =>
        int.TryParse(ArgValue(args, name, ""), out var v) ? v : def;

    private static string ArgValue(string[] args, string name, string def)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : def;
    }

    /// <summary>只读验证 OEM 遥测通道。零写入。</summary>
    private static int OemProbe(string[] args)
    {
        int times = ArgInt(args, "--times", 3);
        if (!OemChannel.Init())
        {
            Say("错误：InsydeDCHU.dll 加载失败（未装 Control Center？）");
            return 2;
        }
        Say("== OEM 通道只读验证 ==");
        Say("DLL: " + OemChannel.DllPath);

        foreach (int cmd in new[] { 56, 97, 99, 100, 53, 54, 112, 113 })
        {
            int? v = OemChannel.GetInteger(cmd);
            Say($"  命令 {cmd,3} → {(v == null ? "读取失败" : v.ToString())}");
        }

        for (int i = 0; i < times; i++)
        {
            var t = OemTelemetry.Read();
            if (t.Valid)
                Say($"  遥测: CPU {t.CpuTemp}°C duty={t.CpuDuty}({t.CpuDuty * 100 / 255}%) rpm_raw={t.CpuRpmRaw} | " +
                    $"GPU {t.Gpu1Temp}°C duty={t.Gpu1Duty}({t.Gpu1Duty * 100 / 255}%) rpm_raw={t.Gpu1RpmRaw}");
            else
                Say("  遥测: 读取失败");
            if (i < times - 1) Thread.Sleep(2000);
        }

        var buf = new byte[256];
        int? rc = OemChannel.GetBuffer(OemChannel.CmdFanTable, buf);
        Say($"  风扇表(package 13) 返回码 {rc}：风扇数={buf[12]} 初始模式={buf[14]}");
        Say("  前 48 字节: " + Convert.ToHexString(buf, 0, 48));
        Say("== 只读验证完成（未写入任何内容） ==");
        return 0;
    }

    /// <summary>
    /// 切换固件风扇模式（一次调用，官方路径）：probe oem-mode &lt;0|1|3|5|6&gt;
    /// 0=自动 1=全速 3=静音 5=Max-Q 6=自定义。切换后连续遥测确认效果。
    /// </summary>
    private static int OemSetMode(string[] args)
    {
        if (args.Length < 3 || !int.TryParse(args[2], out int mode) || mode is < 0 or > 6)
        {
            Say("用法: FanSilencer probe oem-mode <0|1|3|5|6>   (0=自动 1=全速 3=静音)");
            return 1;
        }
        if (!OemChannel.Init())
        {
            Say("错误：InsydeDCHU.dll 加载失败");
            return 2;
        }
        var before = OemTelemetry.Read();
        Say($"== 切换固件模式 → {FanController.ModeName(mode)}({mode}) ==");
        Say($"  切换前: CPU {before.CpuTemp}°C duty={before.CpuDuty} | GPU {before.Gpu1Temp}°C duty={before.Gpu1Duty}");

        OemChannel.SetFanMode(mode);

        for (int i = 0; i < 4; i++)
        {
            Thread.Sleep(2000);
            var t = OemTelemetry.Read();
            Say($"  t+{(i + 1) * 2}s: CPU {t.CpuTemp}°C duty={t.CpuDuty}({t.CpuDuty * 100 / 255}%) rpm≈{OemTelemetry.DisplayRpm(t.CpuRpmRaw)} | " +
                $"GPU {t.Gpu1Temp}°C duty={t.Gpu1Duty}({t.Gpu1Duty * 100 / 255}%) rpm≈{OemTelemetry.DisplayRpm(t.Gpu1RpmRaw)}");
        }
        Say("  如需还原: FanSilencer probe oem-mode 0");
        return 0;
    }
}
