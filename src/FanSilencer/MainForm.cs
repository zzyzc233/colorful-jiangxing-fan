using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FanSilencer;

/// <summary>
/// 可拖动多点风扇曲线编辑器：横轴 35-100°C，纵轴 0-100%。
/// 两端（固件起点 40°C/28%、100°C/100%）固定，中间节点可拖动、双击空白加点、右键删点，
/// 拖动中实时保持温度/占空比单调递增。
/// </summary>
internal class CurveEditor : Control
{
    private List<FanPoint> _pts = new();
    public event Action Changed;
    public event Action EditSettled;   // 拖动松手/加点/删点后触发：用于自动持久化

    private int _drag = -1, _hover = -1;
    private const int PadL = 36, PadR = 14, PadT = 12, PadB = 24;

    public CurveEditor()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, true);
    }

    public void Load(FanCurve c)
    {
        _pts = c.Points.Select(p => p.Clone()).ToList();
        Invalidate();
    }

    public void SaveTo(FanCurve c)
    {
        if (_pts.Count >= 2)                       // 空编辑器不得清空现有曲线
            c.Points = _pts.Select(p => p.Clone()).ToList();
    }

    private List<FanPoint> Nodes()
    {
        var n = new List<FanPoint> { new(FanCurve.T1, FanCurve.D1) };
        n.AddRange(_pts);
        n.Add(new FanPoint(FanCurve.TEnd, FanCurve.DEnd));
        return n;
    }

    private int TempToX(int t) => PadL + (t - 35) * (Width - PadL - PadR) / 65;
    private int XToTemp(int x) => Math.Clamp(35 + (x - PadL) * 65 / Math.Max(1, Width - PadL - PadR), 35, 100);
    private int PctToY(int p) => PadT + (100 - p) * (Height - PadT - PadB) / 100;
    private int YToPct(int y) => Math.Clamp(100 - (y - PadT) * 100 / Math.Max(1, Height - PadT - PadB), 0, 100);

    private int HitPoint(int x, int y)
    {
        int best = -1, bestDist = 14;
        for (int i = 0; i < _pts.Count; i++)
        {
            int d = Dist(x, y, TempToX(_pts[i].T), PctToY(_pts[i].D));
            if (d <= bestDist) { bestDist = d; best = i; }
        }
        return best;
    }

    private static int Dist(int x1, int y1, int x2, int y2) =>
        (int)Math.Sqrt((x1 - x2) * (x1 - x2) + (y1 - y2) * (y1 - y2));

    protected override void OnPaint(PaintEventArgs e)
    {
        try
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.FromArgb(24, 24, 24));

            // 网格
            using var gridPen = new Pen(Color.FromArgb(55, 55, 55), 1);
            using var font = new Font("Microsoft YaHei UI", 7.5F);
            using var dimBrush = new SolidBrush(Color.FromArgb(150, 150, 150));
            for (int p = 0; p <= 100; p += 20)
            {
                int y = PctToY(p);
                g.DrawLine(gridPen, PadL, y, Width - PadR, y);
                g.DrawString(p + "%", font, dimBrush, 2, y - 7);
            }
            for (int t = 40; t <= 100; t += 15)
            {
                int x = TempToX(t);
                g.DrawLine(gridPen, x, PadT, x, Height - PadB);
                // 最后一个刻度左移，避免贴边被裁
                g.DrawString(t + "°", font, dimBrush, x - (t >= 100 ? 24 : 10), Height - PadB + 4);
            }

            // 曲线（多点分段线性，含固件两端）
            var nodes = Nodes();
            var pts = nodes.Select(p => new Point(TempToX(p.T), PctToY(p.D))).ToArray();
            using var curvePen = new Pen(Color.FromArgb(230, 200, 40), 2.4F);
            g.DrawLines(curvePen, pts);

            // 点：两端固定（灰），中间可拖（黄）
            // 注意：Pens.Xxx 是全局共享画笔，绝不能 using/Dispose
            using var fixedBrush = new SolidBrush(Color.Gainsboro);
            using var dragBrush = new SolidBrush(Color.Gold);
            var pen = Pens.DimGray;
            g.DrawEllipse(pen, pts[0].X - 5, pts[0].Y - 5, 10, 10); g.FillEllipse(fixedBrush, pts[0].X - 3, pts[0].Y - 3, 6, 6);
            for (int i = 1; i < pts.Length - 1; i++)
                g.FillEllipse(dragBrush, pts[i].X - 7, pts[i].Y - 7, 14, 14);
            g.DrawEllipse(pen, pts[^1].X - 5, pts[^1].Y - 5, 10, 10); g.FillEllipse(fixedBrush, pts[^1].X - 3, pts[^1].Y - 3, 6, 6);

            // 数值提示：只标注悬停/拖动中的点，避免多点时文字重叠
            int tipIdx = _drag >= 0 ? _drag + 1 : (_hover >= 0 ? _hover + 1 : -1);
            if (tipIdx >= 0 && tipIdx < nodes.Count)
            {
                var np = nodes[tipIdx];
                using var tip = new SolidBrush(Color.Gold);
                g.DrawString($"{np.T}°C {np.D}%", font, tip, pts[tipIdx].X - 20, pts[tipIdx].Y + 10);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("曲线绘制异常: " + ex.Message); // 绘图失败不应拖垮整个程序
        }
        base.OnPaint(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
            _drag = HitPoint(e.X, e.Y);
        else if (e.Button == MouseButtons.Right)
        {
            int hit = HitPoint(e.X, e.Y);
            if (hit >= 0 && _pts.Count > 2)   // 至少保留两个节点
            {
                _pts.RemoveAt(hit);
                _hover = -1;
                Invalidate();
                Changed?.Invoke();
                EditSettled?.Invoke();
            }
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && _pts.Count < FanCurve.MaxPoints)
        {
            int t = XToTemp(e.X);
            if (t > FanCurve.T1 + 1 && t < FanCurve.TEnd - 1)
            {
                // 初始占空比取当前曲线在该温度的值，插入后夹在左右邻之间
                int d = new FanCurve { Points = _pts.Select(p => p.Clone()).ToList() }.DutyAt(t);
                int idx = _pts.Count(p => p.T < t);
                var np = new FanPoint(t, d);
                _pts.Insert(idx, np);
                ClampPoint(idx);
                Invalidate();
                Changed?.Invoke();
                EditSettled?.Invoke();
            }
        }
        base.OnMouseDoubleClick(e);
    }

    private void ClampPoint(int idx)
    {
        var p = _pts[idx];
        int tMin = idx == 0 ? FanCurve.T1 + 1 : _pts[idx - 1].T + 1;
        int tMax = idx == _pts.Count - 1 ? FanCurve.TEnd - 1 : _pts[idx + 1].T - 1;
        p.T = Math.Clamp(p.T, tMin, Math.Max(tMin, tMax));
        int dMin = idx == 0 ? FanCurve.D1 + 1 : _pts[idx - 1].D + 1;
        int dMax = idx == _pts.Count - 1 ? FanCurve.DEnd - 1 : _pts[idx + 1].D - 1;
        p.D = Math.Clamp(p.D, dMin, Math.Max(dMin, dMax));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_drag >= 0)
        {
            _pts[_drag].T = XToTemp(e.X);          // 先落到鼠标位置，再夹回邻点之间
            _pts[_drag].D = YToPct(e.Y);
            ClampPoint(_drag);
            Invalidate();
            Changed?.Invoke();
        }
        else
        {
            int h = HitPoint(e.X, e.Y);
            if (h != _hover)
            {
                _hover = h;
                Invalidate();
            }
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_drag >= 0) EditSettled?.Invoke();   // 拖动结束：自动持久化本次调整
        _drag = -1;
        base.OnMouseUp(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hover != -1) { _hover = -1; Invalidate(); }
        base.OnMouseLeave(e);
    }
}

internal class MainForm : Form
{
    private readonly EcConfig _cfg;
    private readonly FanController _controller;
    private NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _uiTimer = new();
    private bool _reallyExit;

    private readonly Label _lblStatus = new();
    private readonly Label _lblCpu = new();
    private readonly Label _lblGpu = new();
    private readonly Label _lblRpm = new();
    private readonly Label _lblHint = new();
    private readonly RadioButton _rbAuto = new();
    private readonly RadioButton _rbFwSilent = new();
    private readonly RadioButton _rbCustom = new();
    private readonly RadioButton _rbFanCpu = new();
    private readonly RadioButton _rbFanGpu = new();
    private readonly CurveEditor _editor = new();
    private readonly CheckBox _chkAutoApply = new();
    private bool _updatingUi;

    private static readonly Font FontTitle = new("Microsoft YaHei UI", 10.5F, FontStyle.Bold);
    private static readonly Font FontBody = new("Microsoft YaHei UI", 9F);

    public MainForm()
    {
        string appDir = AppContext.BaseDirectory;
        Logger.Init(Path.Combine(appDir, "logs"));   // 日志跟着 exe 走，便携场景不写到上级目录
        Logger.Info("===== 将星风扇管家 v0.5（OEM 通道 + 多点曲线）启动 =====");
        SensorSource.Init();   // 软件传感器（CPU/GPU 温度与游戏加加同源），失败自动回退 EC

        _cfg = EcConfig.Load(Path.Combine(appDir, "config.json"));
        _controller = new FanController(_cfg);
        _controller.Alert += OnControllerAlert;

        SetupUi();
        SetupTray();
        SyncEditorToCurrentFan();   // 先让编辑器镜像当前曲线，避免应用时用空点覆盖

        _uiTimer.Interval = 1000;
        _uiTimer.Tick += (_, _) => RefreshUi();
        _uiTimer.Start();

        if (!OemChannel.Init())
        {
            MessageBox.Show(this,
                "未找到 Control Center 的 InsydeDCHU.dll（C:\\Program Files (x86)\\ControlCenter）。\n" +
                "没有它无法控制风扇，请确认 Control Center 3.0 已安装。",
                "OEM 通道不可用", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        else
        {
            // 1) 先快照 Control Center 当前的调度（此后我们的写入才能在退出时被复原）
            _controller.CaptureControlCenterState();

            // 2) 载入我们上次应用的曲线（没有就用当前生效曲线兜底）
            if (!CurveManager.LoadFromFile())
                CurveManager.LoadCurrent();
            _controller.StartTelemetry();   // 常驻只读遥测

            // 3) 认领固件当前模式（零写入）
            var appData = OemChannel.GetAppData(4, 0, 256);
            int fwMode = appData.Length > 5 ? appData[5] : 0;
            if (fwMode is 3 or 6)
                _controller.AdoptMode(fwMode);

            // 4) 按配置在启动时应用我们的曲线，覆盖 Control Center 的调度
            if (_cfg.ApplyModeOnStart)
                SelectMode(_cfg.DefaultMode);
        }
        SyncUiMode();
        SyncEditorToCurrentFan();
        RefreshUi();
    }

    // ---------- UI ----------

    private void SetupUi()
    {
        Text = "将星风扇管家 v0.5";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(500, 600);
        Font = FontBody;
        Icon = SystemIcons.Application;

        _lblStatus.SetBounds(20, 12, 460, 24);
        _lblStatus.Font = FontTitle;
        _lblStatus.Text = "状态：初始化…";
        _lblCpu.SetBounds(20, 42, 460, 18);
        _lblGpu.SetBounds(20, 63, 460, 18);
        _lblRpm.SetBounds(20, 84, 460, 40);

        var gb = new GroupBox { Text = "风扇模式", Bounds = new Rectangle(20, 130, 460, 52) };
        _rbAuto.SetBounds(12, 22, 0, 22); _rbAuto.AutoSize = true; _rbAuto.Text = "自动（原厂）";
        _rbFwSilent.SetBounds(150, 22, 0, 22); _rbFwSilent.AutoSize = true; _rbFwSilent.Text = "固件静音";
        _rbCustom.SetBounds(280, 22, 0, 22); _rbCustom.AutoSize = true; _rbCustom.Text = "静音曲线（推荐）";
        _rbAuto.CheckedChanged += (_, _) => { if (!_updatingUi && _rbAuto.Checked) SelectMode(0); };
        _rbFwSilent.CheckedChanged += (_, _) => { if (!_updatingUi && _rbFwSilent.Checked) SelectMode(3); };
        _rbCustom.CheckedChanged += (_, _) => { if (!_updatingUi && _rbCustom.Checked) ApplyEditorCurve(); };
        gb.Controls.AddRange(new Control[] { _rbAuto, _rbFwSilent, _rbCustom });

        _rbFanCpu.SetBounds(20, 190, 0, 22); _rbFanCpu.AutoSize = true; _rbFanCpu.Text = "CPU 风扇";
        _rbFanGpu.SetBounds(160, 190, 0, 22); _rbFanGpu.AutoSize = true; _rbFanGpu.Text = "GPU 风扇";
        _rbFanCpu.Checked = true;
        _rbFanCpu.CheckedChanged += (_, _) => { if (_rbFanCpu.Checked) SyncEditorToCurrentFan(); };
        _rbFanGpu.CheckedChanged += (_, _) => { if (_rbFanGpu.Checked) SyncEditorToCurrentFan(); };
        var lblDrag = new Label { Bounds = new Rectangle(286, 192, 206, 20), ForeColor = Color.DimGray, Text = "拖动调整 · 双击加点 · 右键删点" };

        _editor.Bounds = new Rectangle(20, 216, 460, 218);
        _editor.Changed += () => SaveEditorToCurrentFan();
        _editor.EditSettled += () =>
        {
            SaveEditorToCurrentFan();
            CurveManager.SaveToFile();   // 拖动/加点/删点即持久化，重启不丢
        };

        var btnPreset = new Button { Bounds = new Rectangle(20, 442, 140, 32), Text = "均衡预置" };
        btnPreset.Click += (_, _) => LoadPreset();
        var btnApply = new Button { Bounds = new Rectangle(175, 442, 145, 32), Text = "应用曲线", BackColor = Color.FromArgb(210, 230, 160) };
        btnApply.Click += (_, _) => ApplyEditorCurve();
        var btnStock = new Button { Bounds = new Rectangle(335, 442, 145, 32), Text = "还原原厂默认" };
        btnStock.Click += (_, _) => SelectMode(0);

        _chkAutoApply.SetBounds(20, 484, 0, 22); _chkAutoApply.AutoSize = true;
        _chkAutoApply.Text = "开机后自动应用当前曲线";
        _chkAutoApply.Checked = _cfg.ApplyModeOnStart;
        _chkAutoApply.CheckedChanged += (_, _) => { _cfg.ApplyModeOnStart = _chkAutoApply.Checked; SaveConfig(); };

        var chkAutostart = new CheckBox { Bounds = new Rectangle(255, 484, 0, 22), AutoSize = true, Text = "开机自启" };
        chkAutostart.Checked = AutostartTaskExists();
        chkAutostart.CheckedChanged += (_, _) => ToggleAutostart(chkAutostart.Checked);

        var linkLog = new LinkLabel { Bounds = new Rectangle(390, 484, 95, 22), Text = "打开日志" };
        linkLog.LinkClicked += (_, _) => { try { Process.Start("explorer.exe", Logger.LogDir); } catch { } };

        _lblHint.SetBounds(20, 512, 465, 76);
        _lblHint.ForeColor = Color.Gray;
        _lblHint.Text = "多点曲线按温度自动换区写入 EC；拖动自动保存，点「应用曲线」立即生效；\r\n" +
                        "外部改动 3 秒内顶回；97° 兜底、回落 4 秒恢复；还原原厂默认会作废快照。\r\n" +
                        "请勿与 CC3.0 同时调风扇。主显示为游戏加加口径，括号 EC 为风扇实际依据。";

        Controls.AddRange(new Control[]
        {
            _lblStatus, _lblCpu, _lblGpu, _lblRpm, gb,
            _rbFanCpu, _rbFanGpu, lblDrag, _editor,
            btnPreset, btnApply, btnStock,
            _chkAutoApply, chkAutostart, linkLog, _lblHint
        });
    }

    private void SetupTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开主窗口", null, (_, _) => ShowAndActivate());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("应用静音曲线（覆盖 CC3.0）", null, (_, _) => { ShowAndActivate(); ApplyEditorCurve(); });
        menu.Items.Add("恢复接管前的 CC3.0 调度", null, (_, _) => { _controller.RestoreControlCenterState(); SyncUiMode(); RefreshUi(); });
        menu.Items.Add("自动（原厂）", null, (_, _) => SelectMode(0));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出（自动复原 CC3.0）", null, (_, _) => ReallyExit());

        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "将星风扇管家",
            Visible = true,
            ContextMenuStrip = menu
        };
        _tray.DoubleClick += (_, _) => ShowAndActivate();
    }

    // ---------- 曲线 ----------

    private FanCurve CurrentFanCurve => _rbFanGpu.Checked ? CurveManager.Gpu : CurveManager.Cpu;

    private void SyncEditorToCurrentFan() => _editor.Load(CurrentFanCurve);

    private void SaveEditorToCurrentFan() => _editor.SaveTo(CurrentFanCurve);

    private void LoadPreset()
    {
        CurveManager.ReplaceCurves(CurveManager.DefaultCpu(), CurveManager.DefaultGpu());
        SyncEditorToCurrentFan();
    }

    private void ApplyEditorCurve()
    {
        if (!OemChannel.Available && !OemChannel.Init())
        {
            MessageBox.Show(this, "OEM 通道不可用。", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        SaveEditorToCurrentFan();
        Cursor = Cursors.WaitCursor;
        try
        {
            if (_controller.ApplyCurve(CurveManager.Cpu, CurveManager.Gpu))
            {
                CurveManager.SaveToFile(); // 记住我们的曲线，下次启动覆盖时用
                _tray.ShowBalloonTip(2500, "曲线已应用",
                    $"多点曲线已应用（CPU {CurveManager.Cpu.Points.Count} 点 / GPU {CurveManager.Gpu.Points.Count} 点，随温度自动换区）", ToolTipIcon.Info);
            }
            else
            {
                MessageBox.Show(this, "曲线应用失败，详见日志。", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        finally { Cursor = Cursors.Default; }
        SyncUiMode();
        RefreshUi();
    }

    private void SelectMode(int mode)
    {
        if (!OemChannel.Available && !OemChannel.Init())
        {
            MessageBox.Show(this, "OEM 通道不可用，无法切换模式。", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        Cursor = Cursors.WaitCursor;
        try
        {
            switch (mode)
            {
                case 0:
                    _controller.RestoreStock();
                    break;
                case 6:
                    ApplyEditorCurve();
                    return; // ApplyEditorCurve 内部已刷新
                default:
                    _controller.SetMode(mode);
                    break;
            }
        }
        finally { Cursor = Cursors.Default; }
        SyncUiMode();
        RefreshUi();
    }

    /// <summary>让模式单选钮与控制器状态一致（抑制事件，避免递归触发应用）。</summary>
    private void SyncUiMode()
    {
        _updatingUi = true;
        try
        {
            int m = _controller.TargetMode;
            _rbAuto.Checked = m == 0;
            _rbFwSilent.Checked = m == 3;
            _rbCustom.Checked = m == 6;
        }
        finally { _updatingUi = false; }
    }

    private void OnControllerAlert(string msg)
    {
        if (InvokeRequired) BeginInvoke(() =>
        {
            _tray.ShowBalloonTip(5000, "风扇保护动作", msg, ToolTipIcon.Warning);
            RefreshUi();
        });
        else
        {
            _tray.ShowBalloonTip(5000, "风扇保护动作", msg, ToolTipIcon.Warning);
            RefreshUi();
        }
    }

    private void RefreshUi()
    {
        var t = _controller.Last;
        var st = _controller.State;
        string modeText = _controller.TargetMode < 0 ? "未设置"
            : FanController.ModeName(_controller.TargetMode) +
              (_controller.ActiveMode != _controller.TargetMode
                  ? $"（过热保护中，实际：{FanController.ModeName(_controller.ActiveMode)}）" : "");
        string statusText = st switch
        {
            ControllerState.Managed => $"状态：静音曲线管理中 —— {modeText}",
            ControllerState.Fault => "状态：OEM 通道异常",
            _ => "状态：未管理（固件自动）"
        };
        if (_controller.TelemetryStale)
            statusText += "　⚠ 遥测停滞，正在自动恢复…";
        else if (_controller.CurveOverridden)
            statusText += "　⚠ 曲线被外部覆盖（CC3.0 性能模式？）";
        _lblStatus.Text = statusText;
        _lblStatus.ForeColor = _controller.TelemetryStale || _controller.CurveOverridden ? Color.Orange :
                               st == ControllerState.Managed ? Color.Green :
                               st == ControllerState.Fault ? Color.Red : Color.Black;

        if (t is { Valid: true })
        {
            int ec = _controller.CpuTempFiltered, dts = _controller.CpuTempDts, gpuT = _controller.GpuTempFiltered;
            string cpuShown = dts >= 0
                ? (ec >= 0 && Math.Abs(dts - ec) >= 3 ? $"{dts}°C（EC {ec}°）" : $"{dts}°C")
                : (ec >= 0 ? $"{ec}°C" : "--");
            string cpuTray = dts >= 0 ? dts.ToString() : (ec >= 0 ? ec.ToString() : "--");
            _lblCpu.Text = $"CPU：{cpuShown}   风扇 {t.CpuDuty * 100 / 255}%   约 {OemTelemetry.DisplayRpm(t.CpuRpmRaw)} RPM";
            _lblGpu.Text = $"GPU：{(gpuT >= 0 ? gpuT.ToString() : "--")}°C   风扇 {t.Gpu1Duty * 100 / 255}%   约 {OemTelemetry.DisplayRpm(t.Gpu1RpmRaw)} RPM";
            _lblRpm.ForeColor = Color.DimGray;
            _lblRpm.Text = $"CPU 曲线 {Pts(CurveManager.Cpu)}（EC 口径）\r\nGPU 曲线 {Pts(CurveManager.Gpu)}";
            _tray.Text = $"将星风扇管家  CPU {cpuTray}°C / GPU {(gpuT >= 0 ? gpuT.ToString() : "--")}°C";
        }
        else
        {
            _lblCpu.Text = "CPU：--（等待遥测）";
            _lblGpu.Text = "GPU：--";
            _lblRpm.Text = "";
        }
    }

    private static string Pts(FanCurve c) => string.Join(" ", c.Points.Select(p => $"{p.T}>{p.D}%"));

    private void SaveConfig()
    {
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(_cfg,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "config.json"), json);
        }
        catch (Exception ex) { Logger.Error("保存配置失败: " + ex.Message); }
    }

    private bool AutostartTaskExists()
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks", "/Query /TN FanSilencer")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            using var p = Process.Start(psi);
            p.WaitForExit(3000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    private void ToggleAutostart(bool enable)
    {
        try
        {
            string exe = Path.Combine(AppContext.BaseDirectory, "FanSilencer.exe");
            string args = enable
                ? $"/Create /TN FanSilencer /TR \"{exe}\" /SC ONLOGON /RL HIGHEST /F"
                : "/Delete /TN FanSilencer /F";
            var psi = new ProcessStartInfo("schtasks", args)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            using var p = Process.Start(psi);
            p.WaitForExit(5000);
            Logger.Info($"开机自启{(enable ? "已开启" : "已关闭")}，schtasks 退出码 {p.ExitCode}");
        }
        catch (Exception ex) { Logger.Error("设置开机自启失败: " + ex.Message); }
    }

    private void ShowAndActivate()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void ReallyExit()
    {
        _reallyExit = true;
        // 退出时自动回放快照，把 Control Center 的调度复原
        _controller.RestoreControlCenterState();
        _tray.Visible = false;
        Application.Exit();
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_POWERBROADCAST = 0x0218;
        const int PBT_APMRESUMESUSPEND = 0x07;      // 睡眠唤醒
        const int PBT_APMRESUMEAUTOMATIC = 0x12;    // 自动唤醒
        if (m.Msg == WM_POWERBROADCAST && (m.WParam.ToInt64() is PBT_APMRESUMESUSPEND or PBT_APMRESUMEAUTOMATIC))
        {
            OemChannel.Reset();   // 唤醒后驱动句柄失效，重置防止遥测冻结
            Logger.Info("系统从睡眠唤醒，OEM 通道已重置");
        }
        base.WndProc(ref m);
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_POWERBROADCAST = 0x0218;
        const int PBT_APMRESUMESUSPEND = 0x07;      // 睡眠唤醒
        const int PBT_APMRESUMEAUTOMATIC = 0x12;    // 自动唤醒
        if (m.Msg == WM_POWERBROADCAST && (m.WParam.ToInt64() is PBT_APMRESUMESUSPEND or PBT_APMRESUMEAUTOMATIC))
        {
            OemChannel.Reset();   // 唤醒后驱动句柄失效，重置防止遥测冻结
            Logger.Info("系统从睡眠唤醒，OEM 通道已重置");
        }
        const int WM_QUERYENDSESSION = 0x0011;      // 系统即将关机/注销：记录现场，便于事后排查"不明关机"
        if (m.Msg == WM_QUERYENDSESSION)
        {
            try
            {
                string fg = "未知";
                var h = Forensics.GetForegroundWindow();
                if (h != IntPtr.Zero && Forensics.GetWindowThreadProcessId(h, out uint procId) != 0)
                    fg = Process.GetProcessById((int)procId)?.ProcessName ?? "未知";
                var apps = Process.GetProcesses()
                    .Where(pr => !string.IsNullOrWhiteSpace(pr.MainWindowTitle))
                    .Select(pr => pr.ProcessName)
                    .Take(10).ToList();
                Logger.Info($"系统即将关机/注销（ lParam=0x{m.LParam.ToInt64():X} ）现场: 前台={fg}；带窗口进程={string.Join("/", apps)}");
            }
            catch (Exception ex) { Logger.Error("关机现场记录失败: " + ex.Message); }
        }
        base.WndProc(ref m);
    }

    private static class Forensics
    {
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_reallyExit && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _uiTimer?.Dispose();
            _tray?.Dispose();
            _controller?.Dispose();
        }
        base.Dispose(disposing);
    }
}
