using System;
using System.IO;
using System.Threading;

namespace FanSilencer;

public enum ControllerState
{
    Idle,        // 未设置（固件保持当前状态）
    Managed,     // 管理中（按所选固件模式运行 + 高温监护）
    Fault        // OEM 通道异常
}

/// <summary>
/// 风扇控制引擎 v0.4（OEM 通道版）：
/// - 启动时快照 Control Center 当前的调度（EC 风扇表 + 模式 + AppData）；
/// - 运行期间我们的曲线持续压制外部调度（模式字节检查 + 定期重申），高温自动全速兜底；
/// - 退出时把快照原样回放，Control Center 的调度自动复原。
/// 全程经签名 InsydeDCHU.dll（AcpiBridge 驱动），无端口 IO，无高频轮询。
/// </summary>
internal class FanController : IDisposable
{
    private readonly EcConfig _cfg;
    private readonly object _sync = new();
    private Thread _loop;
    private volatile bool _running;
    private int _hotStreak, _coolStreak;
    private bool _hotTriggered;
    private int _tickCount;

    // 温度毛刺滤波（EC 遥测偶发单帧离谱值，如瞬时 119°C）。-1 = 尚无可信读数
    private int _ctlCpu = -1, _ctlGpu = -1;
    private int _pendCpu = int.MinValue, _pendGpu = int.MinValue;
    private DateTime _lastGlitchLog = DateTime.MinValue;
    public int CpuTempFiltered => _ctlCpu;
    public int GpuTempFiltered => _ctlGpu;

    // 接管前状态快照（CC3.0 调度）
    private byte[] _backupTable;
    private byte[] _backupAppData;
    private int _backupMode;
    private bool _snapshotTaken;
    private bool _restored;

    public ControllerState State { get; private set; } = ControllerState.Idle;
    public int TargetMode { get; private set; } = -1;   // 用户选择的模式
    public int ActiveMode { get; private set; } = -1;   // 当前实际生效模式（含高温回退）
    public OemTelemetry Last;
    public string LastAlert { get; private set; } = "";
    public event Action<string> Alert;

    public FanController(EcConfig cfg) => _cfg = cfg;

    public bool Start(int mode)
    {
        lock (_sync)
        {
            if (!OemChannel.Init()) return false;
            TargetMode = mode;
            if (!OemChannel.SetFanMode(mode)) return false;
            ActiveMode = mode;
            State = ControllerState.Managed;
            LastAlert = "";
            if (_loop == null || !_loop.IsAlive)
            {
                _running = true;
                _loop = new Thread(MonitorLoop) { IsBackground = true, Name = "FanMonitor" };
                _loop.Start();
            }
            Logger.Info($"风扇模式已切换为 {ModeName(mode)}，监护启动");
            return true;
        }
    }

    public void SetMode(int mode)
    {
        lock (_sync)
        {
            if (!OemChannel.Init()) return;
            if (OemChannel.SetFanMode(mode))
            {
                TargetMode = mode;
                ActiveMode = mode;
                _hotTriggered = false;
                LastAlert = "";
                if (State == ControllerState.Idle && _loop == null)
                {
                    _running = true;
                    _loop = new Thread(MonitorLoop) { IsBackground = true, Name = "FanMonitor" };
                    _loop.Start();
                }
                State = ControllerState.Managed;
            }
        }
    }

    /// <summary>应用自定义曲线（官方序列：package 14 + 模式 6 + AppData）并启动监护。</summary>
    public bool ApplyCurve(FanCurve cpu, FanCurve gpu)
    {
        lock (_sync)
        {
            if (!OemChannel.Init()) return false;
            int cpuT = _ctlCpu >= 0 ? _ctlCpu : 60;
            int gpuT = _ctlGpu >= 0 ? _ctlGpu : 60;
            if (!CurveManager.Apply(cpu, gpu, cpuT, gpuT)) return false;
            TargetMode = 6;
            ActiveMode = 6;
            _hotTriggered = false;
            LastAlert = "";
            State = ControllerState.Managed;
            EnsureLoop();
            return true;
        }
    }

    /// <summary>恢复固件默认曲线 + 自动模式，停止监护。同时作废 CC3.0 快照（用户已显式选择原厂）。</summary>
    public bool RestoreStock()
    {
        lock (_sync)
        {
            if (!OemChannel.Init()) return false;
            _running = false;
            bool ok = CurveManager.RestoreStock();
            TargetMode = 0;
            ActiveMode = 0;
            State = ControllerState.Idle;
            _snapshotTaken = false; // 用户已显式回原厂，退出时不再回放旧快照
            Logger.Info("已恢复固件默认风扇表 + 自动模式（快照已作废）");
            return ok;
        }
    }

    private void EnsureLoop()
    {
        if (_loop == null || !_loop.IsAlive)
        {
            _running = true;
            _loop = new Thread(MonitorLoop) { IsBackground = true, Name = "FanMonitor" };
            _loop.Start();
        }
    }

    /// <summary>恢复固件自动模式并停止监护（固件模式本身安全，无残留风险）。</summary>
    public void Release(string reason)
    {
        lock (_sync)
        {
            if (State != ControllerState.Managed) return;
            OemChannel.SetFanMode(0);
            ActiveMode = 0;
            TargetMode = 0;
            State = ControllerState.Idle;
            LastAlert = reason;
            Logger.Info("已恢复固件自动模式: " + reason);
        }
    }

    /// <summary>启动常驻遥测循环（只读，1Hz）。不改变任何风扇状态。</summary>
    public void StartTelemetry()
    {
        lock (_sync)
        {
            if (_loop != null && _loop.IsAlive) return;
            if (!OemChannel.Available && !OemChannel.Init()) return;
            _running = true;
            _loop = new Thread(MonitorLoop) { IsBackground = true, Name = "FanMonitor" };
            _loop.Start();
            Logger.Info("遥测监护循环已启动（只读）");
        }
    }

    /// <summary>认领固件当前已处于的模式（零写入），让监护与状态显示和实际一致。</summary>
    public void AdoptMode(int mode)
    {
        lock (_sync)
        {
            TargetMode = mode;
            ActiveMode = mode;
            State = mode is 3 or 6 ? ControllerState.Managed : ControllerState.Idle;
            if (State == ControllerState.Managed) EnsureLoop();
        }
    }

    /// <summary>
    /// 快照 Control Center 当前的调度（必须在本次会话任何写入之前调用）。
    /// 备份 EC 风扇表 + AppData + 模式字节，并落盘备查。
    /// </summary>
    public void CaptureControlCenterState()
    {
        lock (_sync)
        {
            try
            {
                var table = new byte[256];
                bool tableOk = OemChannel.GetBuffer(OemChannel.CmdFanTable, table) != null;
                var appData = OemChannel.GetAppData(4, 0, 256);
                _backupMode = appData != null && appData.Length > 5 ? appData[5] : 0;
                _backupTable = tableOk ? table : null;
                _backupAppData = appData;
                _snapshotTaken = true;
                _restored = false;
                if (_backupTable != null)
                    File.WriteAllBytes(Path.Combine(Logger.LogDir, "cc-backup-table.bin"), _backupTable);
                if (_backupAppData != null)
                    File.WriteAllBytes(Path.Combine(Logger.LogDir, "cc-backup-appdata.bin"), _backupAppData);
                Logger.Info($"已备份接管前调度（CC3.0 快照）: 模式={_backupMode}，表={( _backupTable != null ? "OK" : "读取失败")}");
            }
            catch (Exception ex)
            {
                _snapshotTaken = true; // 标记已尝试，退出时走保守路径
                Logger.Error("备份 CC3.0 调度失败: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// 退出时回放快照，把 Control Center 的调度原样复原。幂等：重复调用无副作用。
    /// </summary>
    public void RestoreControlCenterState()
    {
        lock (_sync)
        {
            _running = false;
            State = ControllerState.Idle;
            if (!_snapshotTaken)
            {
                Logger.Info("本会话无快照，退出时不改写风扇状态");
                return;
            }
            if (_restored) return;
            _restored = true;
            try
            {
                if (_backupTable != null || _backupAppData != null)
                    CurveManager.RestoreSnapshot(_backupTable, _backupAppData, _backupMode);
                else
                    OemChannel.SetFanMode(0); // 备份时读失败过，保守切回自动
            }
            catch (Exception ex)
            {
                Logger.Error("回放快照异常: " + ex.Message);
            }
            _backupTable = null;
            _backupAppData = null;
            TargetMode = -1;
            ActiveMode = -1;
            Logger.Info("退出：已复原接管前的 Control Center 调度");
        }
    }

    private void MonitorLoop()
    {
        while (_running)
        {
            try
            {
                Last = OemTelemetry.Read();
                _tickCount++;
                FilterGlitch(Last.Valid, Last.CpuTemp, ref _ctlCpu, ref _pendCpu, "CPU");
                FilterGlitch(Last.Valid, Last.Gpu1Temp, ref _ctlGpu, ref _pendGpu, "GPU");

                if (State == ControllerState.Managed && TargetMode == 6 && !_hotTriggered)
                {
                    int cpuT = _ctlCpu >= 0 ? _ctlCpu : 60;
                    int gpuT = _ctlGpu >= 0 ? _ctlGpu : 60;

                    // 压制外部调度：CC3.0 / 热键改了模式 → 3 秒内顶回我们的曲线
                    if (_tickCount % 3 == 0)
                    {
                        var ad = OemChannel.GetAppData(4, 0, 8);
                        if (ad != null && ad.Length > 5 && ad[5] != 6)
                        {
                            Logger.Info($"检测到外部模式变更(AppData={ad[5]})，重新压制为静音曲线");
                            CurveManager.Apply(CurveManager.Cpu, CurveManager.Gpu, cpuT, gpuT);
                        }
                    }
                    // 每 30 秒无条件重申一次（覆盖表格被改但模式未变的情况）
                    if (_tickCount % 30 == 0)
                    {
                        CurveManager.Apply(CurveManager.Cpu, CurveManager.Gpu, cpuT, gpuT);
                        Logger.Info("定期重申静音曲线（保持对 Control Center 的覆盖）");
                    }
                    // 多点曲线：温度跨区间时把所在区间的两点窗口写进 EC 表（偏差 ≥3% 才写）
                    CurveManager.RefreshWindows(cpuT, gpuT);
                }

                if (State == ControllerState.Managed && TargetMode != 0 && TargetMode != 1)
                {
                    // 高温监护（带去抖，用滤波后温度）：CPU 或 GPU 过热 → 全速；双双回落后再恢复
                    bool hot = _ctlCpu >= _cfg.HotCpuTemp || _ctlGpu >= _cfg.HotGpuTemp;
                    bool cool = _ctlCpu >= 0 && _ctlGpu >= 0 &&
                                _ctlCpu <= _cfg.CoolCpuTemp && _ctlGpu <= _cfg.CoolGpuTemp;
                    if (hot)
                    {
                        if (++_hotStreak >= 3 && !_hotTriggered)
                        {
                            _hotTriggered = true;
                            _coolStreak = 0;
                            if (OemChannel.SetFanMode(1))
                            {
                                ActiveMode = 1;
                                string msg = $"CPU {Last.CpuTemp}°C / GPU {Last.Gpu1Temp}°C 过热，已自动切到全速散热";
                                Logger.Info(msg);
                                LastAlert = msg;
                                RaiseAlert(msg);
                            }
                        }
                    }
                    else _hotStreak = 0;

                    if (_hotTriggered && cool)
                    {
                        // 回落确认从 10 秒收紧到 4 秒：退游戏后温度骤降时，全速不再拖十几秒
                        if (++_coolStreak >= 4)
                        {
                            _hotTriggered = false;
                            if (OemChannel.SetFanMode(TargetMode))
                            {
                                ActiveMode = TargetMode;
                                if (TargetMode == 6)
                                    CurveManager.RefreshWindows(_ctlCpu, _ctlGpu, force: true);
                                Logger.Info($"温度回落至 CPU {_ctlCpu}°C / GPU {_ctlGpu}°C，已恢复 {ModeName(TargetMode)}");
                                RaiseAlert($"温度回落，已恢复{ModeName(TargetMode)}模式");
                            }
                        }
                    }
                    else _coolStreak = 0;
                }
            }
            catch (Exception ex)
            {
                Logger.Error("遥测循环异常: " + ex.Message);
            }
            Thread.Sleep(Math.Max(500, _cfg.TelemetryPollMs));
        }
    }

    /// <summary>
    /// 温度单帧毛刺滤波：EC 遥测偶发一帧离谱值（如瞬时 119°C——真实硅温到不了，
    /// 到了固件也会立即强制降频/关机）。偏离上一可信值 &gt;15°C 的帧先不采信，
    /// 连续两帧相互印证（真实满载爬升会持续偏高）才整体接受；
    /// 风扇实际转速由 EC 按其自身传感器与曲线表实时控制，本滤波只影响
    /// 窗口选择、软件兜底判断与界面显示。
    /// </summary>
    private void FilterGlitch(bool valid, int raw, ref int ctl, ref int pending, string fan)
    {
        if (!valid) return;
        if (ctl < 0) { ctl = raw; return; }                        // 首帧直接采信
        if (Math.Abs(raw - ctl) <= 15)                             // 正常波动
        {
            pending = int.MinValue;
            ctl = raw;
            return;
        }
        if (pending != int.MinValue && Math.Abs(raw - pending) <= 15)
        {
            ctl = raw;                                             // 连续两帧一致 → 真实突变
            pending = int.MinValue;
            return;
        }
        pending = raw;                                             // 疑似毛刺：挂起待下一帧印证
        if ((DateTime.Now - _lastGlitchLog).TotalSeconds >= 60)
        {
            _lastGlitchLog = DateTime.Now;
            Logger.Info($"忽略 {fan} 温度单帧毛刺: {raw}°C（上一可信 {ctl}°C）");
        }
    }

    public static string ModeName(int mode) => mode switch
    {
        0 => "自动",
        1 => "全速",
        3 => "固件静音",
        5 => "Max-Q",
        6 => "静音曲线",
        _ => $"模式{mode}"
    };

    private void RaiseAlert(string msg)
    {
        try { Alert?.Invoke(msg); } catch { }
    }

    public void Dispose()
    {
        _running = false;
        RestoreControlCenterState();
    }
}
