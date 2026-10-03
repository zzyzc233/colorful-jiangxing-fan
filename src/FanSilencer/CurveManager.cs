using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace FanSilencer;

/// <summary>多点曲线的一个可调节点（温度 °C / 占空比 %）。</summary>
public class FanPoint
{
    public int T { get; set; }
    public int D { get; set; }
    public FanPoint() { }
    public FanPoint(int t, int d) { T = t; D = d; }
    public FanPoint Clone() => new(T, D);
}

/// <summary>
/// 单个风扇的多点曲线：固件起点 (40°C,28%) 与终点 (100°C,100%) 固定，中间节点数量不限。
/// EC 固件表每次只接受两个可调点（官方 Load_Rxx 格式），因此运行时由 CurveManager 按当前
/// 温度取“所在区间”的两个节点写成窗口帧（WindowFor）动态刷写，等效实现任意多点的平滑曲线；
/// 温度高于最后一个节点时固件走 (末节点)→(100°C,100%) 段，程序异常退出后风扇仍会随温度升到全速。
/// </summary>
public class FanCurve
{
    public const int T1 = 40, D1 = 28;          // 固件固定起点（只读展示）
    public const int TEnd = 100, DEnd = 100;    // 固件固定终点
    public const int MaxPoints = 8;

    public List<FanPoint> Points { get; set; } = new();

    public FanCurve Clone() => new() { Points = Points.Select(p => p.Clone()).ToList() };

    /// <summary>官方 Load_Rxx 公式：斜率用【百分比】差值 × 2.55 × 16。</summary>
    public static int Slope(double d1pct, int t1, double d2pct, int t2) =>
        Math.Clamp((int)Math.Round((d2pct - d1pct) / Math.Max(1, t2 - t1) * 2.55 * 16.0), -32768, 32767);

    /// <summary>节点排序 + 严格递增约束（温度 41-99、占空比 29-99），节点数 2-8。</summary>
    public void Normalize()
    {
        Points ??= new List<FanPoint>();
        Points = Points.Where(p => p != null).OrderBy(p => p.T).ToList();
        if (Points.Count < 2)
            Points = new List<FanPoint> { new(60, 40), new(85, 75) };
        if (Points.Count > MaxPoints)
            Points = Points.Take(MaxPoints).ToList();
        foreach (var p in Points)
        {
            p.T = Math.Clamp(p.T, T1 + 1, TEnd - 1);
            p.D = Math.Clamp(p.D, D1 + 1, DEnd - 1);
        }
        for (int i = 1; i < Points.Count; i++)                 // 温度严格递增
            if (Points[i].T <= Points[i - 1].T) Points[i].T = Points[i - 1].T + 1;
        if (Points[^1].T > TEnd - 1)                           // 挤出上界则从右往左压回
        {
            Points[^1].T = TEnd - 1;
            for (int i = Points.Count - 2; i >= 0; i--)
                Points[i].T = Math.Min(Points[i].T, Points[i + 1].T - 1);
        }
        for (int i = 1; i < Points.Count; i++)                 // 占空比严格递增
            if (Points[i].D <= Points[i - 1].D) Points[i].D = Points[i - 1].D + 1;
        if (Points[^1].D > DEnd - 1)
        {
            Points[^1].D = DEnd - 1;
            for (int i = Points.Count - 2; i >= 0; i--)
                Points[i].D = Math.Min(Points[i].D, Points[i + 1].D - 1);
        }
    }

    /// <summary>多点曲线在给定温度下的目标占空比（分段线性，含固件两端）。</summary>
    public int DutyAt(int temp)
    {
        int ta = T1, da = D1;
        foreach (var p in Points)
        {
            if (temp <= p.T) return Interp(ta, da, p.T, p.D, temp);
            ta = p.T; da = p.D;
        }
        return Interp(ta, da, TEnd, DEnd, temp);
    }

    internal static int Interp(int ta, int da, int tb, int db, int t) =>
        t <= ta ? da : t >= tb ? db
        : (int)Math.Round(da + (db - da) * (double)(t - ta) / Math.Max(1, tb - ta));
}

/// <summary>一帧写入 EC 的两点窗口（含固件起点，用于官方斜率公式）。</summary>
public class EcWindow
{
    public int T1 = FanCurve.T1, D1 = FanCurve.D1;
    public int T2, D2, T3, D3;
    public int R12() => FanCurve.Slope(D1, T1, D2, T2);
    public int R23() => FanCurve.Slope(D2, T2, D3, T3);
    public int R34() => FanCurve.Slope(D3, T3, FanCurve.TEnd, FanCurve.DEnd);
}

/// <summary>
/// 自定义风扇曲线管理，写入序列与官方 FanSpeedSetting 反编译源码一致：
/// 1. package 14 写 EC 运行时表：0x02 起 CPU/GPU1/GPU2 的 T2,D2,T3,D3（占空比=百分比×2.55），
///    0x0E 起每风扇 R12/R23/R34（高字节在前，百分比斜率）。
/// 2. SetFanInfo：AppSettings 页 4 回写应用侧数据（百分比窗口 + 低字节在前的斜率 + 版本/模式字节）。
/// 3. SetFanMode(6) 切自定义模式。
/// 恢复原厂 = 官方 WMI13_LoadDefault（121/34/1）+ 模式 0。
/// </summary>
internal static class CurveManager
{
    private const int AppPage = 4;
    private const int RefreshStepPct = 3;   // 目标占空比与当前窗口偏差 ≥3% 才刷写，避免频繁刷表

    public static FanCurve Cpu { get; private set; } = DefaultCpu();
    public static FanCurve Gpu { get; private set; } = DefaultGpu();

    /// <summary>整体替换两风扇曲线（预置按钮用），并清空窗口缓存以便重刷。</summary>
    internal static void ReplaceCurves(FanCurve cpu, FanCurve gpu)
    {
        Cpu = cpu;
        Gpu = gpu;
        _lastCpuWin = _lastGpuWin = null;
    }

    private static EcWindow _lastCpuWin, _lastGpuWin;

    /// <summary>
    /// 默认均衡曲线：EC 的 CPU 温度计比游戏加加等软件传感器口径高约 10°C，
    /// 因此温度轴整体右移补偿——待机段（EC ≤70°，约等于游戏加加 60°）贴着固件
    /// 下限求安静，80° 以后保持散热力度，95° 以上仍由固件兜底拉满。
    /// </summary>
    public static FanCurve DefaultCpu() => new()
    {
        Points = new List<FanPoint> { new(50, 29), new(70, 30), new(75, 33), new(80, 38), new(85, 48), new(90, 62), new(95, 80) }
    };

    public static FanCurve DefaultGpu() => new()
    {
        Points = new List<FanPoint> { new(45, 29), new(60, 30), new(70, 37), new(78, 50), new(85, 66), new(92, 82) }
    };

    /// <summary>按当前温度取曲线所在区间的两个节点，组成一帧 EC 两点窗口。</summary>
    public static EcWindow WindowFor(FanCurve c, int temp)
    {
        c.Normalize();
        int i = 0;
        for (int k = 0; k < c.Points.Count; k++)
            if (c.Points[k].T <= temp) i = k;
        i = Math.Clamp(i, 0, c.Points.Count - 2);   // 末区间之上保持 (末节点)→(100,100) 固件兜底段
        return new EcWindow { T2 = c.Points[i].T, D2 = c.Points[i].D, T3 = c.Points[i + 1].T, D3 = c.Points[i + 1].D };
    }

    /// <summary>窗口曲线在给定温度下的隐含占空比（固件将按此执行）。</summary>
    public static int ImpliedDuty(EcWindow w, int temp)
    {
        if (temp <= w.T2) return FanCurve.Interp(w.T1, w.D1, w.T2, w.D2, temp);
        if (temp <= w.T3) return FanCurve.Interp(w.T2, w.D2, w.T3, w.D3, temp);
        return FanCurve.Interp(w.T3, w.D3, FanCurve.TEnd, FanCurve.DEnd, temp);
    }

    /// <summary>多点曲线是否偏离当前 EC 窗口足够多（≥3%），需要刷写新窗口。</summary>
    public static bool NeedsRefresh(int cpuTemp, int gpuTemp)
    {
        return _lastCpuWin == null || _lastGpuWin == null
            || Math.Abs(ImpliedDuty(_lastCpuWin, cpuTemp) - Cpu.DutyAt(cpuTemp)) >= RefreshStepPct
            || Math.Abs(ImpliedDuty(_lastGpuWin, gpuTemp) - Gpu.DutyAt(gpuTemp)) >= RefreshStepPct;
    }

    /// <summary>把当前温度所在区间的窗口写入 EC 表（不动模式与 AppData）。</summary>
    public static bool RefreshWindows(int cpuTemp, int gpuTemp, bool force = false)
    {
        if (!force && !NeedsRefresh(cpuTemp, gpuTemp)) return true;
        return WriteCurveToEc(WindowFor(Cpu, cpuTemp), WindowFor(Gpu, gpuTemp), log: true);
    }

    /// <summary>从驱动 AppData 页 4 读取当前曲线（官方 Read_FanInfo 布局，占空比为百分比）。</summary>
    public static bool ReadFromAppData()
    {
        var d = OemChannel.GetAppData(AppPage, 0, 256);
        if (d.Length < 70 || d[16] == 0 && d[22] == 0) return false;
        Cpu = TwoPointCurve(d[23], d[24], d[16], d[17], d[18]);
        Gpu = TwoPointCurve(d[41], d[42], d[34], d[35], d[36]);
        Logger.Info($"已读取应用侧曲线: CPU({Cpu.Points[0].T},{Cpu.Points[0].D}%)({Cpu.Points[1].T},{Cpu.Points[1].D}%) " +
                    $"GPU({Gpu.Points[0].T},{Gpu.Points[0].D}%)({Gpu.Points[1].T},{Gpu.Points[1].D}%)");
        return true;
    }

    /// <summary>从 EC 固件表（package 13，255 刻度）读取，AppData 不可用时兜底。</summary>
    public static void ReadFromEcTable()
    {
        var buf = new byte[256];
        if (OemChannel.GetBuffer(OemChannel.CmdFanTable, buf) == null) return;
        Cpu = TwoPointCurve(buf[0x12], buf[0x14], buf[0x13] * 100 / 255, buf[0x11] * 100 / 255, buf[0x15] * 100 / 255);
        Gpu = TwoPointCurve(buf[0x1A], buf[0x1C], buf[0x1B] * 100 / 255, buf[0x19] * 100 / 255, buf[0x1D] * 100 / 255);
    }

    private static FanCurve TwoPointCurve(int t2, int t3, int d1, int d2, int d3) => new()
    {
        Points = new List<FanPoint>
        {
            new(Math.Max(t2, FanCurve.T1 + 1), Math.Clamp(d2, 1, 99)),
            new(Math.Max(t3, t2 + 1), Math.Clamp(d3, 1, 99))
        }
    };

    public static void LoadCurrent()
    {
        if (!ReadFromAppData()) ReadFromEcTable();
    }

    private static byte Raw(int percent) => (byte)Math.Clamp(Math.Round(percent / 100.0 * 255.0), 0, 255);

    private static void PutSlopeHiLo(byte[] b, int off, int slope)
    {
        slope = Math.Clamp(slope, short.MinValue, short.MaxValue);
        b[off] = (byte)(slope >> 8);
        b[off + 1] = (byte)(slope & 0xFF);
    }

    /// <summary>应用多点曲线（写当前温度所在窗口 + 切自定义模式 6 + 回写 AppData）。</summary>
    public static bool Apply(FanCurve cpu, FanCurve gpu, int cpuTemp, int gpuTemp)
    {
        if (!OemChannel.Available && !OemChannel.Init()) return false;
        cpu.Normalize();
        gpu.Normalize();
        var cw = WindowFor(cpu, cpuTemp);
        var gw = WindowFor(gpu, gpuTemp);

        // --- package 14：EC 运行时表 ---
        if (!WriteCurveToEc(cw, gw, log: true)) return false;

        // --- SetFanMode(6) ---
        if (!OemChannel.SetFanMode(6)) return false;

        // --- SetFanInfo：AppData 页 4（读改写，保留版本/模式/默认值字节） ---
        var d = OemChannel.GetAppData(AppPage, 0, 256);
        d[5] = 6; // FanMode = 自定义
        d[16] = (byte)cw.D1; d[17] = (byte)cw.D2; d[18] = (byte)cw.D3; d[19] = 100;
        d[20] = (byte)cw.D2; d[21] = (byte)cw.D3;
        d[22] = (byte)cw.T1; d[23] = (byte)cw.T2; d[24] = (byte)cw.T3; d[25] = 100;
        d[26] = (byte)cw.T2; d[27] = (byte)cw.T3;
        d[28] = (byte)(cw.R12() & 0xFF); d[29] = (byte)(cw.R12() >> 8);
        d[30] = (byte)(cw.R23() & 0xFF); d[31] = (byte)(cw.R23() >> 8);
        d[32] = (byte)(cw.R34() & 0xFF); d[33] = (byte)(cw.R34() >> 8);
        d[34] = (byte)gw.D1; d[35] = (byte)gw.D2; d[36] = (byte)gw.D3; d[37] = 100;
        d[38] = (byte)gw.D2; d[39] = (byte)gw.D3;
        d[40] = (byte)gw.T1; d[41] = (byte)gw.T2; d[42] = (byte)gw.T3; d[43] = 100;
        d[44] = (byte)gw.T2; d[45] = (byte)gw.T3;
        d[46] = (byte)(gw.R12() & 0xFF); d[47] = (byte)(gw.R12() >> 8);
        d[48] = (byte)(gw.R23() & 0xFF); d[49] = (byte)(gw.R23() >> 8);
        d[50] = (byte)(gw.R34() & 0xFF); d[51] = (byte)(gw.R34() >> 8);
        OemChannel.SetAppData(AppPage, 0, d);

        Cpu = cpu.Clone();
        Gpu = gpu.Clone();
        Logger.Info($"多点曲线已应用: CPU {Pts(cpu)}  GPU {Pts(gpu)}（当前窗口 CPU {cw.T2}-{cw.T3}° / GPU {gw.T2}-{gw.T3}°）模式6");
        return true;
    }

    private static string Pts(FanCurve c) => string.Join(" ", c.Points.Select(p => $"{p.T}°:{p.D}%"));

    /// <summary>恢复固件默认曲线并切回自动模式（官方 WMI13_LoadDefault 路径）。</summary>
    public static bool RestoreStock()
    {
        if (!OemChannel.Available && !OemChannel.Init()) return false;
        OemChannel.LoadFanDefaults();
        bool ok = OemChannel.SetFanMode(0);
        _lastCpuWin = _lastGpuWin = null;
        Logger.Info("已恢复固件默认风扇表 + 自动模式");
        return ok;
    }

    private static EcWindow WindowFromTable(byte[] t, int off) => new()
    {
        T1 = t[off], D1 = t[off + 1] * 100 / 255,
        T2 = t[off + 2], D2 = t[off + 3] * 100 / 255,
        T3 = t[off + 4], D3 = t[off + 5] * 100 / 255
    };

    /// <summary>
    /// 快照回放：把接管前备份的曲线/模式/应用数据原样写回（不切自定义模式，模式用备份值）。
    /// </summary>
    public static bool RestoreSnapshot(byte[] table, byte[] appData, int mode)
    {
        if (!OemChannel.Available && !OemChannel.Init()) return false;
        try
        {
            if (table != null && table[0x10] != 0)
            {
                var cw = WindowFromTable(table, 0x10);
                var gw = WindowFromTable(table, 0x18);
                WriteCurveToEc(cw, gw, log: false);
                _lastCpuWin = cw;
                _lastGpuWin = gw;
            }
            if (appData != null && appData.Length == 256)
                OemChannel.SetAppData(AppPage, 0, appData);
            OemChannel.SetFanMode(mode);
            Logger.Info($"已回放接管前状态: 模式={mode}");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("快照回放失败: " + ex.Message);
            return false;
        }
    }

    /// <summary>仅写 EC 运行时表（package 14），不动模式与 AppData。</summary>
    private static bool WriteCurveToEc(EcWindow cpu, EcWindow gpu, bool log)
    {
        var p = new byte[256];
        p[2] = (byte)cpu.T2;  p[3] = Raw(cpu.D2);  p[4] = (byte)cpu.T3;  p[5] = Raw(cpu.D3);
        p[6] = (byte)gpu.T2;  p[7] = Raw(gpu.D2);  p[8] = (byte)gpu.T3;  p[9] = Raw(gpu.D3);
        p[10] = (byte)gpu.T2; p[11] = Raw(gpu.D2); p[12] = (byte)gpu.T3; p[13] = Raw(gpu.D3);
        PutSlopeHiLo(p, 14, cpu.R12());  PutSlopeHiLo(p, 16, cpu.R23());  PutSlopeHiLo(p, 18, cpu.R34());
        PutSlopeHiLo(p, 20, gpu.R12());  PutSlopeHiLo(p, 22, gpu.R23());  PutSlopeHiLo(p, 24, gpu.R34());
        PutSlopeHiLo(p, 26, gpu.R12());  PutSlopeHiLo(p, 28, gpu.R23());  PutSlopeHiLo(p, 30, gpu.R34());
        bool ok = OemChannel.SetBuffer(OemChannel.CmdFanTableWrite, p, 256);
        if (ok)
        {
            bool zoneChanged = _lastCpuWin == null || _lastGpuWin == null
                || _lastCpuWin.T2 != cpu.T2 || _lastCpuWin.T3 != cpu.T3
                || _lastGpuWin.T2 != gpu.T2 || _lastGpuWin.T3 != gpu.T3;
            _lastCpuWin = cpu;
            _lastGpuWin = gpu;
            if (log && zoneChanged)
                Logger.Info($"曲线窗口更新: CPU {cpu.T2}-{cpu.T3}°({cpu.D2}-{cpu.D3}%)  GPU {gpu.T2}-{gpu.T3}°({gpu.D2}-{gpu.D3}%)");
        }
        return ok;
    }

    // ----- 我们的多点曲线持久化（与应用侧 AppData 相互独立） -----

    private static string CurveFilePath => Path.Combine(AppContext.BaseDirectory, "curve.json");

    public static void SaveToFile()
    {
        try
        {
            var model = new
            {
                cpu = new { points = Cpu.Points.Select(p => new { p.T, p.D }) },
                gpu = new { points = Gpu.Points.Select(p => new { p.T, p.D }) }
            };
            var json = JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(CurveFilePath, json);
        }
        catch (Exception ex) { Logger.Error("曲线保存失败: " + ex.Message); }
    }

    public static bool LoadFromFile()
    {
        try
        {
            if (!File.Exists(CurveFilePath)) return false;
            var doc = JsonDocument.Parse(File.ReadAllText(CurveFilePath));
            bool ok = false;
            foreach (var (fan, node) in new[] { ("cpu", doc.RootElement.GetProperty("cpu")), ("gpu", doc.RootElement.GetProperty("gpu")) })
            {
                var c = fan == "cpu" ? Cpu : Gpu;
                if (node.TryGetProperty("points", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    var pts = arr.EnumerateArray()
                        .Select(e => new FanPoint(e.GetProperty("T").GetInt32(), e.GetProperty("D").GetInt32()))
                        .ToList();
                    if (pts.Count >= 2) { c.Points = pts; ok = true; }
                }
                else if (node.TryGetProperty("T2", out var t2))   // v0.3 旧格式：两个可调点
                {
                    c.Points = new List<FanPoint>
                    {
                        new(t2.GetInt32(), node.GetProperty("D2").GetInt32()),
                        new(node.GetProperty("T3").GetInt32(), node.GetProperty("D3").GetInt32())
                    };
                    ok = true;
                }
                c.Normalize();
            }
            return ok;
        }
        catch (Exception ex)
        {
            Logger.Error("曲线读取失败: " + ex.Message);
            return false;
        }
    }
}
