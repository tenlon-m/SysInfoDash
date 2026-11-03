using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace SysInfoDash
{
    internal enum DockEdge { Left, Right, Top, Bottom }
    internal enum DockState { Free, DockedExpanded, DockedHandle }

    /// <summary>主题配色。</summary>
    internal class Palette
    {
        public Color Bg, Border, Text, Dim, Slot, Warn;
        public Color Cpu, Mem, Gpu, GpuMem, Net, Temp, Disk, ReadLed, WriteLed;
    }

    /// <summary>桌面小窗口：紧凑信息面板 + 贴边吸附 + 悬浮显隐 + 托盘。</summary>
    public class MainForm : Form
    {
        // ---------- 布局尺寸（随字号缩放，由 ComputeLayout 计算） ----------
        private int PanelW = 268;
        private int RowH = 20;
        private int RowGap = 5;
        private int PadX = 10;
        private int PadY = 10;
        private int LabelW = 46;
        private int BarW = 70;
        private int HandleLen = 120;      // 把手长度
        private int HandleThick = 8;     // 把手厚度
        private int DockEdgePx = 14;     // 吸附判定阈值
        private float _s = 1f;           // 字号缩放系数 = FontSize / 9
        private int _barH = 8;           // 条形高度（随字号缩放）
        private int _textOffY = 3;       // 文字纵向偏移（随字号缩放）
        private int _ledOffY = 6;        // 指示灯纵向偏移（随字号缩放）

        private SysInfoCollector _collector;
        private AppConfig _config;
        private NotifyIcon _tray;
        private ContextMenuStrip _menu;
        private System.Windows.Forms.Timer _uiTimer;
        private int _tick;

        private Palette _p;
        private Font _font;
        private Font _smallFont;
        private float _dpi = 1f;               // 实际DPI / 96，用于物理像素换算

        private DockState _dock = DockState.Free;
        private DockEdge _dockEdge = DockEdge.Right;
        private bool _dragging;
        private Point _dragScreenStart;   // 拖动起点（屏幕坐标，避免客户端坐标反馈导致滞后）
        private Point _winStart;

        // 菜单引用（用于刷新勾选状态）
        private ToolStripMenuItem _miToggle;
        private ToolStripMenuItem _miAuto;
        private ToolStripMenuItem _miStartHidden;
        private ToolStripMenuItem _miUndock;
        private Dictionary<string, ToolStripMenuItem> _itemChecks = new Dictionary<string, ToolStripMenuItem>();
        private Dictionary<string, ToolStripMenuItem[]> _modeChecks = new Dictionary<string, ToolStripMenuItem[]>();
        private ToolStripMenuItem[] _unitChecks = new ToolStripMenuItem[3];
        private ToolStripMenuItem[] _themeChecks = new ToolStripMenuItem[2];
        private ToolStripMenuItem[] _fontChecks;   // 字号菜单项（8~12）
        private ToolStripMenuItem _miTopMost;       // 置顶显示
        private ToolStripMenuItem _miClickThrough;  // 鼠标穿透
        private ToolStripMenuItem[] _opacityChecks = new ToolStripMenuItem[5];   // 透明度 100/80/60/40/20
        private Dictionary<string, ToolStripMenuItem> _dockMetricChecks = new Dictionary<string, ToolStripMenuItem>();   // 把手显示指标勾选项

        public MainForm()
        {
            Text = "SysInfoDash";
            _config = AppConfig.Load();
            ComputeLayout();
            ApplyTheme();
            _font = new Font("Segoe UI", _config.FontSize);
            _smallFont = new Font("Segoe UI", Math.Max(6f, _config.FontSize * 0.833f));

            // 进程为 DPI aware，所有窗口坐标均为物理像素；
            // 布局按 96 基准设计，这里取实际缩放系数（>1 放大，=1 不变）
            try { using (Graphics g = CreateGraphics()) _dpi = g.DpiX / 96f; } catch { }
            if (_dpi <= 0f) _dpi = 1f;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = _config.TopMost;          // 按配置决定是否置顶（默认置顶）
            Opacity = _config.Opacity;          // 按配置设置不透明度
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;
            AutoScaleMode = AutoScaleMode.None;

            BuildMenu();
            ContextMenuStrip = _menu;

            _collector = new SysInfoCollector();

            _uiTimer = new System.Windows.Forms.Timer();
            _uiTimer.Interval = 200;
            _uiTimer.Tick += delegate(object s, EventArgs e) { OnUiTick(); };
            _uiTimer.Start();

            BuildTray();
        }

        // ================= 窗口生命周期 =================

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            SyncAutoStart();   // 自愈：使开机启动项与当前 exe 位置保持一致
            Size sz = DesiredSize();
            SetBounds(0, 0, sz.Width, sz.Height);
            ApplyStartupPosition();

            // 通过开机启动项（带 /tray 参数）拉起时，直接隐藏到托盘
            if (LaunchedToTray())
                Hide();

            // 应用不透明度与鼠标穿透（需在窗口句柄创建后设置）
            this.Opacity = _config.Opacity;
            ApplyClickThrough();
        }

        private static bool LaunchedToTray()
        {
            foreach (string a in Environment.GetCommandLineArgs())
                if (a.Equals("/tray", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // ================= 鼠标穿透（点击穿透到下层窗口） =================
        // 通过 WS_EX_TRANSPARENT 扩展样式实现：窗口照常显示，但鼠标点击会穿透到后面的窗口。
        // 适合做桌面挂件；开启后本窗口无法接收鼠标事件（不能拖动/右键），
        // 需通过托盘图标右键菜单关闭“鼠标穿透”后再操作窗口。
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        private void ApplyClickThrough()
        {
            if (IsDisposed) return;
            IntPtr h = this.Handle;
            if (h == IntPtr.Zero) return;
            int ex = GetWindowLong(h, GWL_EXSTYLE);
            if (_config.ClickThrough)
                ex |= WS_EX_TRANSPARENT;
            else
                ex &= ~WS_EX_TRANSPARENT;
            SetWindowLong(h, GWL_EXSTYLE, ex);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            Cleanup();
        }

        private void Cleanup()
        {
            try
            {
                if (_dock == DockState.Free)
                {
                    _config.PosX = Left;
                    _config.PosY = Top;
                }
                _config.Save();
                if (_collector != null) { _collector.Dispose(); _collector = null; }
                if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
                if (_uiTimer != null) { _uiTimer.Stop(); _uiTimer.Dispose(); _uiTimer = null; }
                if (_font != null) _font.Dispose();
                if (_smallFont != null) _smallFont.Dispose();
            }
            catch { }
        }

        private void ExitApp()
        {
            try
            {
                _config.PosX = Left;
                _config.PosY = Top;
                _config.Save();
                if (_collector != null) { _collector.Dispose(); _collector = null; }
                if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
                if (_uiTimer != null) { _uiTimer.Stop(); _uiTimer.Dispose(); _uiTimer = null; }
            }
            catch { }
            Application.Exit();
        }

        // ================= 尺寸与位置 =================

        private bool Show(string id) { return _config.Show.ContainsKey(id) && _config.Show[id]; }
        private string Mode(string id) { return _config.Mode.ContainsKey(id) ? _config.Mode[id] : AppConfig.ModeBoth; }

        private int CountRows()
        {
            SysInfoSnapshot snap = (_collector != null) ? _collector.Current : null;
            int n = 0;
            if (Show(AppConfig.ItemCpu)) n++;
            if (Show(AppConfig.ItemCpuSpeed)) n++;
            if (Show(AppConfig.ItemCpuTemp)) n++;
            if (Show(AppConfig.ItemMem)) n++;
            if (Show(AppConfig.ItemGpu)) n++;
            if (Show(AppConfig.ItemGpuMem))
            {
                n++;
                if (snap != null && snap.GpuSharedTotalMB > 0) n++;
            }
            if (Show(AppConfig.ItemNet)) n++;
            if (Show(AppConfig.ItemDisk))
                n += (snap != null && snap.Disks.Count > 0) ? snap.Disks.Count : 1;
            if (Show(AppConfig.ItemDiskIo)) n++;
            return n;
        }

        private Size DesiredSize()
        {
            int rows = CountRows();
            int h = PadY * 2 + rows * RowH + (rows > 0 ? (rows - 1) * RowGap : 0);
            if (h < PadY * 2 + RowH) h = PadY * 2 + RowH;
            return new Size(ScalePx(PanelW), ScalePx(h));
        }

        // 依据字号计算所有布局尺寸，使整个窗口随字号整体缩放
        private void ComputeLayout()
        {
            _s = (_config != null && _config.FontSize > 0) ? _config.FontSize / 9f : 1f;
            if (_s <= 0) _s = 1f;
            PanelW = (int)Math.Round(268 * _s);
            RowH = (int)Math.Round(20 * _s);
            RowGap = (int)Math.Round(5 * _s);
            PadX = (int)Math.Round(10 * _s);
            PadY = (int)Math.Round(10 * _s);
            LabelW = (int)Math.Round(46 * _s);
            BarW = (int)Math.Round(70 * _s);
            HandleLen = (int)Math.Round(120 * _s);
            HandleThick = (int)Math.Round(8 * _s);
            DockEdgePx = (int)Math.Round(14 * _s);
            _barH = (int)Math.Round(8 * _s);
            _textOffY = (int)Math.Round(3 * _s);
            _ledOffY = (int)Math.Round(6 * _s);
        }

        // 96 基准逻辑像素 → 物理像素
        private int ScalePx(int logical) { return (int)Math.Round(logical * _dpi); }

        private void ResizeToContent()
        {
            Size want = DesiredSize();
            if (_dock == DockState.Free)
            {
                if (Width != want.Width || Height != want.Height)
                    SetBounds(Left, Top, want.Width, want.Height);
            }
            else if (_dock == DockState.DockedExpanded)
            {
                Rectangle r = GetDockedRect(_dockEdge, false);
                if (Width != r.Width || Height != r.Height)
                    SetBounds(r.X, r.Y, r.Width, r.Height);
            }
        }

        private static bool IsOnScreen(Point p)
        {
            foreach (Screen s in Screen.AllScreens)
                if (s.Bounds.Contains(p)) return true;
            return false;
        }

        private void ApplyStartupPosition()
        {
            if (_config.PosX >= 0 && _config.PosY >= 0 && IsOnScreen(new Point(_config.PosX, _config.PosY)))
            {
                Location = new Point(_config.PosX, _config.PosY);
            }
            else
            {
                Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                Location = new Point(wa.Right - Width - 20, wa.Bottom - Height - 20);
            }
        }

        // ================= 定时刷新 =================

        private void OnUiTick()
        {
            _tick++;
            if (_tick % 5 == 0)
            {
                ResizeToContent();
                Invalidate();
            }

            // 悬浮托盘图标时显示系统信息：定期把最新快照写入托盘 ToolTip
            UpdateTrayTooltip();

            // 贴边展开时，鼠标移出窗口则收回为把手
            if (_dock == DockState.DockedExpanded && !_dragging && (_menu == null || !_menu.Visible))
            {
                Rectangle r = new Rectangle(PointToScreen(Point.Empty), Size);
                r.Inflate(2, 2);
                if (!r.Contains(Cursor.Position)) CollapseToHandle();
            }
        }

        // ================= 贴边吸附 / 悬浮显隐 =================

        private Rectangle GetDockedRect(DockEdge edge, bool handle)
        {
            Rectangle wa = Screen.FromControl(this).WorkingArea;
            Size want = DesiredSize();
            if (handle)
            {
                int hlen = ScalePx(HandleLen);
                int hthick = ScalePx(HandleThick);
                if (edge == DockEdge.Left)
                    return new Rectangle(wa.Left, wa.Top + (wa.Height - hlen) / 2, hthick, hlen);
                if (edge == DockEdge.Right)
                    return new Rectangle(wa.Right - hthick, wa.Top + (wa.Height - hlen) / 2, hthick, hlen);
                if (edge == DockEdge.Top)
                    return new Rectangle(wa.Left + (wa.Width - hlen) / 2, wa.Top, hlen, hthick);
                return new Rectangle(wa.Left + (wa.Width - hlen) / 2, wa.Bottom - hthick, hlen, hthick);
            }
            if (edge == DockEdge.Left)
                return new Rectangle(wa.Left, wa.Top + (wa.Height - want.Height) / 2, want.Width, want.Height);
            if (edge == DockEdge.Right)
                return new Rectangle(wa.Right - want.Width, wa.Top + (wa.Height - want.Height) / 2, want.Width, want.Height);
            if (edge == DockEdge.Top)
                return new Rectangle(wa.Left + (wa.Width - want.Width) / 2, wa.Top, want.Width, want.Height);
            return new Rectangle(wa.Left + (wa.Width - want.Width) / 2, wa.Bottom - want.Height, want.Width, want.Height);
        }

        private void DockTo(DockEdge edge)
        {
            _dockEdge = edge;
            Rectangle r = GetDockedRect(edge, false);
            _dock = DockState.DockedExpanded;
            SetBounds(r.X, r.Y, r.Width, r.Height);
        }

        private void TryDock()
        {
            Rectangle wa = Screen.FromControl(this).WorkingArea;
            if (Left <= wa.Left + DockEdgePx) DockTo(DockEdge.Left);
            else if (Right >= wa.Right - DockEdgePx) DockTo(DockEdge.Right);
            else if (Top <= wa.Top + DockEdgePx) DockTo(DockEdge.Top);
            else if (Bottom >= wa.Bottom - DockEdgePx) DockTo(DockEdge.Bottom);
        }

        private void ExpandFromHandle()
        {
            Rectangle r = GetDockedRect(_dockEdge, false);
            _dock = DockState.DockedExpanded;
            SetBounds(r.X, r.Y, r.Width, r.Height);
            Invalidate();
        }

        private void CollapseToHandle()
        {
            Rectangle r = GetDockedRect(_dockEdge, true);
            _dock = DockState.DockedHandle;
            SetBounds(r.X, r.Y, r.Width, r.Height);
            Invalidate();
        }

        private void UndockToFree()
        {
            _dock = DockState.Free;
            Size want = DesiredSize();
            SetBounds(Left, Top, want.Width, want.Height);
            Invalidate();
        }

        // ================= 鼠标交互 =================

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                if (_dock != DockState.Free)
                {
                    _dock = DockState.Free;
                    Size want = DesiredSize();
                    SetBounds(Left, Top, want.Width, want.Height);
                }
                _dragging = true;
                _dragScreenStart = Cursor.Position;
                _winStart = new Point(Left, Top);
                Capture = true;   // 捕获鼠标，拖动时即使光标移出窗口也能持续收到 MouseMove，避免卡顿/不跟手
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragging)
            {
                // 用屏幕坐标计算，避免客户端坐标随窗口移动产生反馈滞后
                Point p = Cursor.Position;
                Left = _winStart.X + (p.X - _dragScreenStart.X);
                Top = _winStart.Y + (p.Y - _dragScreenStart.Y);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_dragging)
            {
                _dragging = false;
                Capture = false;
                TryDock();
            }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            if (_dock == DockState.DockedHandle) ExpandFromHandle();
        }

        // ================= 绘制 =================

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            // 把 96 基准逻辑坐标映射到物理像素（高DPI时放大，100%时不变）
            g.ScaleTransform(_dpi, _dpi);
            Palette p = _p;

            if (_dock == DockState.DockedHandle)
            {
                DrawHandle(g, p);
                return;
            }

            // 客户区物理像素 → 逻辑像素
            int w = Math.Max(1, (int)Math.Round(ClientSize.Width / _dpi));
            int h = Math.Max(1, (int)Math.Round(ClientSize.Height / _dpi));
            using (GraphicsPath path = RoundedPath(1, 1, w - 2, h - 2, 8))
            using (Brush bg = new SolidBrush(p.Bg))
            using (Pen border = new Pen(p.Border))
            {
                g.FillPath(bg, path);
                g.DrawPath(border, path);
            }

            SysInfoSnapshot s = (_collector != null) ? _collector.Current : null;
            int y = PadY;
            using (Brush text = new SolidBrush(p.Text))
            {
                if (Show(AppConfig.ItemCpu)) y = DrawCpuRow(g, s, y, text);
                if (Show(AppConfig.ItemCpuSpeed)) y = DrawFreqRow(g, s, y, text);
                if (Show(AppConfig.ItemCpuTemp)) y = DrawTempRow(g, s, y, text);
                if (Show(AppConfig.ItemMem)) y = DrawMemRow(g, s, y, text);
                if (Show(AppConfig.ItemGpu)) y = DrawGpuRow(g, s, y, text);
                if (Show(AppConfig.ItemGpuMem))
                {
                    y = DrawGpuMemRow(g, s, y, text);
                    if (s != null && s.GpuSharedTotalMB > 0)
                        y = DrawGpuSharedRow(g, s, y, text);
                }
                if (Show(AppConfig.ItemNet)) y = DrawNetRow(g, s, y, text);
                if (Show(AppConfig.ItemDisk))
                {
                    if (s != null && s.Disks.Count > 0)
                        foreach (DiskInfo d in s.Disks)
                            y = DrawDiskRow(g, d, y, text);
                    else
                        y = DrawDiskRow(g, null, y, text);
                }
                if (Show(AppConfig.ItemDiskIo)) y = DrawIoRow(g, s, y, text);
            }
        }

        private static GraphicsPath RoundedPath(int x, int y, int w, int h, int r)
        {
            GraphicsPath path = new GraphicsPath();
            if (w <= 0 || h <= 0)
            {
                path.AddRectangle(new Rectangle(x, y, Math.Max(1, w), Math.Max(1, h)));
                return path;
            }
            int d = r * 2;
            path.AddArc(x, y, d, d, 180, 90);
            path.AddArc(x + w - d, y, d, d, 270, 90);
            path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            path.AddArc(x, y + h - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private void FillRoundedRect(Graphics g, Brush b, int x, int y, int w, int h, int r)
        {
            using (GraphicsPath path = RoundedPath(x, y, w, h, r))
                g.FillPath(b, path);
        }

        // 靠边把手可显示的候选指标（id 同时用于配置与菜单）
        private static readonly string[] DockCandidates = { "cpu", "mem", "gpu", "gpumem", "netdown", "netup" };

        private static string DockLabel(string id)
        {
            switch (id)
            {
                case "cpu": return "CPU 利用率";
                case "mem": return "内存占用";
                case "gpu": return "GPU 利用率";
                case "gpumem": return "GPU 显存";
                case "netdown": return "下载速度";
                case "netup": return "上传速度";
                default: return id;
            }
        }

        private static Color DockColor(string id, Palette p)
        {
            switch (id)
            {
                case "cpu": return p.Cpu;
                case "mem": return p.Mem;
                case "gpu": return p.Gpu;
                case "gpumem": return p.GpuMem;
                case "netdown": return p.Net;
                case "netup": return p.Temp;
                default: return p.Text;
            }
        }

        // 解析指标当前比例（0~1）
        private static double DockRatio(string id, SysInfoSnapshot s)
        {
            if (s == null) return 0;
            double r = 0;
            switch (id)
            {
                case "cpu": r = s.CpuUsage / 100.0; break;
                case "mem": r = (double)s.MemUsedMB / Math.Max(1UL, s.MemTotalMB); break;
                case "gpu": r = s.GpuUsage.HasValue ? s.GpuUsage.Value / 100.0 : 0; break;
                case "gpumem": r = (double)s.GpuDedUsedMB / Math.Max(1UL, s.GpuDedTotalMB); break;
                // 把手网速条按“链路利用率”显示（相对网卡速率），能真实反映速度大小并随速度变化；
                // 再用 sqrt 缩放，使低速也肉眼可见，同时避免稳态下载时把条“顶满”而看不出变化。
                // 若链路速率未知（为 0），回退到相对峰值（同样 sqrt），至少能显示活动变化。
                case "netdown":
                    r = (s.NetLinkBits > 0)
                        ? (s.NetDownBps * 8.0) / s.NetLinkBits
                        : (s.NetDownPeak > 0 ? s.NetDownBps / s.NetDownPeak : 0);
                    r = Math.Sqrt(r);
                    break;
                case "netup":
                    r = (s.NetLinkBits > 0)
                        ? (s.NetUpBps * 8.0) / s.NetLinkBits
                        : (s.NetUpPeak > 0 ? s.NetUpBps / s.NetUpPeak : 0);
                    r = Math.Sqrt(r);
                    break;
            }
            if (r < 0) r = 0; if (r > 1) r = 1;
            return r;
        }

        private List<string> ValidDockMetrics()
        {
            List<string> res = new List<string>();
            if (_config.DockMetrics != null)
                foreach (string id in _config.DockMetrics)
                    if (Array.IndexOf(DockCandidates, id) >= 0 && !res.Contains(id))
                        res.Add(id);
            while (res.Count > 2) res.RemoveAt(0);
            return res;
        }

        private void DrawHandle(Graphics g, Palette p)
        {
            int w = Math.Max(1, (int)Math.Round(ClientSize.Width / _dpi));
            int h = Math.Max(1, (int)Math.Round(ClientSize.Height / _dpi));
            bool vertical = (_dockEdge == DockEdge.Left || _dockEdge == DockEdge.Right);
            using (Brush bg = new SolidBrush(p.Bg))
            using (Pen border = new Pen(p.Border))
            {
                if (vertical)
                {
                    using (GraphicsPath path = RoundedPath(0, (h - HandleLen) / 2, w, HandleLen, 4))
                    {
                        g.FillPath(bg, path);
                        g.DrawPath(border, path);
                    }
                }
                else
                {
                    using (GraphicsPath path = RoundedPath((w - HandleLen) / 2, 0, HandleLen, h, 4))
                    {
                        g.FillPath(bg, path);
                        g.DrawPath(border, path);
                    }
                }
            }

            // 在把手上画最多 2 个迷你动态条（沿把手长度方向填充）
            List<string> ids = ValidDockMetrics();
            int n = ids.Count;
            if (n == 0) return;
            SysInfoSnapshot s = (_collector != null) ? _collector.Current : null;
            int len = vertical ? h : w;       // 把手长度方向尺寸（约 120）
            int center = len / 2;
            int half = len / 2;               // 每个指标最大可延伸长度（中心点向外的半长）
            for (int k = 0; k < n; k++)
            {
                double r = DockRatio(ids[k], s);
                if (r < 0) r = 0; if (r > 1) r = 1;
                Color c = DockColor(ids[k], p);
                int fl = (int)Math.Round(half * r);
                if (fl < 0) fl = 0;
                using (Brush b = new SolidBrush(c))
                {
                    if (vertical)
                    {
                        // 竖向把手：从中心向两端延伸（上条向上、下条向下）
                        if (n == 1)
                            FillRoundedRect(g, b, 0, center - fl, w, fl * 2, 2);
                        else if (k == 0)
                            FillRoundedRect(g, b, 0, center - fl, w, fl, 2);   // 上条：向上
                        else
                            FillRoundedRect(g, b, 0, center, w, fl, 2);        // 下条：向下
                    }
                    else
                    {
                        // 横向把手：从中心向两端延伸（左条向左、右条向右）
                        if (n == 1)
                            FillRoundedRect(g, b, center - fl, 0, fl * 2, h, 2);
                        else if (k == 0)
                            FillRoundedRect(g, b, center - fl, 0, fl, h, 2);   // 左条：向左
                        else
                            FillRoundedRect(g, b, center, 0, fl, h, 2);        // 右条：向右
                    }
                }
            }
        }

        private void DrawBar(Graphics g, int x, int y, int bw, Color color, double ratio)
        {
            int barH = _barH;
            int by = y + (RowH - barH) / 2;
            using (Brush slot = new SolidBrush(_p.Slot))
                FillRoundedRect(g, slot, x, by, bw, barH, 3);
            if (ratio <= 0.01) return;
            if (ratio > 1) ratio = 1;
            int fw = (int)Math.Round(bw * ratio);
            if (fw < 4) fw = 4;
            if (fw > bw) fw = bw;
            Color c = (ratio > 0.9) ? _p.Warn : color;
            using (Brush fb = new SolidBrush(c))
                FillRoundedRect(g, fb, x, by, fw, barH, 3);
        }

        private void DrawValue(Graphics g, int y, string value, Brush brush, string mode, Font font)
        {
            if (String.IsNullOrEmpty(value)) return;
            Rectangle r = new Rectangle(ValueX(mode), y, ValueW(mode), RowH);
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Far;
                sf.LineAlignment = StringAlignment.Center;
                g.DrawString(value, font, brush, r, sf);
            }
        }

        private void DrawRowCore(Graphics g, int y, string label, Color barColor, double ratio, string value, string mode, Brush text)
        {
            g.DrawString(label, _font, text, (float)PadX, (float)(y + _textOffY));
            if (mode == AppConfig.ModeBar || mode == AppConfig.ModeBoth)
                DrawBar(g, BarX(mode), y, BarW2(mode), barColor, ratio);
            if (mode == AppConfig.ModeNumber || mode == AppConfig.ModeBoth)
                DrawValue(g, y, value, text, mode, _font);
        }

        private int BarX(string mode) { return PadX + LabelW + 2; }

        private int BarW2(string mode)
        {
            return (mode == AppConfig.ModeBoth)
                ? BarW
                : (PanelW - PadX * 2 - LabelW - 4);
        }

        private int ValueX(string mode)
        {
            return (mode == AppConfig.ModeBoth)
                ? PadX + LabelW + BarW + 4
                : PadX + LabelW + 4;
        }

        private int ValueW(string mode)
        {
            return (mode == AppConfig.ModeBoth)
                ? PanelW - PadX * 2 - LabelW - BarW - 4
                : PanelW - PadX * 2 - LabelW - 4;
        }

        private static string GbText(ulong mb)
        {
            if (mb < 1024) return mb.ToString("0") + "MB";
            return (mb / 1024.0).ToString("0.0") + "GB";
        }

        private static string MemPair(ulong used, ulong total)
        {
            if (total < 1024 && used < 1024)
                return used.ToString("0") + "/" + total.ToString("0") + "MB";
            return (used / 1024.0).ToString("0.0") + "/" + (total / 1024.0).ToString("0.0") + "GB";
        }

        private static string GbTextBytes(ulong bytes)
        {
            return (bytes / 1073741824.0).ToString("0.0");
        }

        // 网速格式化：按量级自动选择 B/K/M/G 前缀并自适应小数位，
        // 避免低速时（<0.05 Mbps）一律显示“0.0”而看起来“数字不变”。
        // unit 仅决定“比特(b)/字节(B)”体系；具体 K/M/G 前缀自动。
        private static string FormatSpeed(double bps, string unit)
        {
            if (bps < 0) bps = 0;
            bool asBytes = (unit == "MBps");          // 字节体系；Mbps/GBps 为比特体系
            double v = asBytes ? bps : bps * 8;        // 字节时为 B/s，比特时为 b/s
            string[] suf = asBytes
                ? new[] { "B/s", "KB/s", "MB/s", "GB/s" }
                : new[] { "b/s", "Kb/s", "Mb/s", "Gb/s" };
            double div = asBytes ? 1024.0 : 1000.0;
            int i = 0;
            while (v >= div && i < suf.Length - 1) { v /= div; i++; }
            string fmt = (v >= 100.0) ? "0" : (v >= 10.0 ? "0.0" : "0.00");
            return v.ToString(fmt) + suf[i];
        }

        private static string FormatBps(double bps)
        {
            if (bps >= 1048576.0) return (bps / 1048576.0).ToString("0.0") + "M/s";
            if (bps >= 1024.0) return (bps / 1024.0).ToString("0") + "K/s";
            return bps.ToString("0") + "B/s";
        }

        // ---------- 各信息行 ----------

        private int DrawCpuRow(Graphics g, SysInfoSnapshot s, int y, Brush text)
        {
            string mode = Mode(AppConfig.ItemCpu);
            string val = (s != null) ? s.CpuUsage.ToString("0") + "%" : "…";
            double ratio = (s != null) ? s.CpuUsage / 100.0 : 0;
            DrawRowCore(g, y, "CPU", _p.Cpu, ratio, val, mode, text);
            return y + RowH + RowGap;
        }

        private int DrawFreqRow(Graphics g, SysInfoSnapshot s, int y, Brush text)
        {
            string mode = Mode(AppConfig.ItemCpuSpeed);
            string val = (s != null) ? (s.CpuMhz / 1000f).ToString("0.00") + "GHz" : "…";
            double ratio = (s != null && s.CpuMaxMhz > 0) ? s.CpuMhz / s.CpuMaxMhz : 0;
            DrawRowCore(g, y, "频率", _p.Cpu, ratio, val, mode, text);
            return y + RowH + RowGap;
        }

        private int DrawTempRow(Graphics g, SysInfoSnapshot s, int y, Brush text)
        {
            string mode = Mode(AppConfig.ItemCpuTemp);
            string val = "N/A";
            double ratio = 0;
            if (s != null && s.CpuTemp.HasValue)
            {
                val = s.CpuTemp.Value.ToString("0") + "°C";
                ratio = s.CpuTemp.Value / 90.0;
            }
            DrawRowCore(g, y, "温度", _p.Temp, ratio, val, mode, text);
            return y + RowH + RowGap;
        }

        private int DrawMemRow(Graphics g, SysInfoSnapshot s, int y, Brush text)
        {
            string mode = Mode(AppConfig.ItemMem);
            string val = "…";
            double ratio = 0;
            if (s != null && s.MemTotalMB > 0)
            {
                val = MemPair(s.MemUsedMB, s.MemTotalMB);
                ratio = s.MemUsedMB / (double)s.MemTotalMB;
            }
            DrawRowCore(g, y, "内存", _p.Mem, ratio, val, mode, text);
            return y + RowH + RowGap;
        }

        private int DrawGpuRow(Graphics g, SysInfoSnapshot s, int y, Brush text)
        {
            string mode = Mode(AppConfig.ItemGpu);
            string val = "N/A";
            double ratio = 0;
            if (s != null && s.GpuUsage.HasValue)
            {
                val = s.GpuUsage.Value.ToString("0") + "%";
                if (s.GpuTemp.HasValue)
                    val += " " + s.GpuTemp.Value.ToString("0") + "°C";
                ratio = s.GpuUsage.Value / 100.0;
            }
            DrawRowCore(g, y, "GPU", _p.Gpu, ratio, val, mode, text);
            return y + RowH + RowGap;
        }

        private int DrawGpuMemRow(Graphics g, SysInfoSnapshot s, int y, Brush text)
        {
            string mode = Mode(AppConfig.ItemGpuMem);
            string val = "N/A";
            double ratio = 0;
            if (s != null && s.GpuDedTotalMB > 0)
            {
                val = MemPair(s.GpuDedUsedMB, s.GpuDedTotalMB);
                ratio = s.GpuDedUsedMB / (double)s.GpuDedTotalMB;
            }
            DrawRowCore(g, y, "显存", _p.GpuMem, ratio, val, mode, text);
            return y + RowH + RowGap;
        }

        private int DrawGpuSharedRow(Graphics g, SysInfoSnapshot s, int y, Brush text)
        {
            string mode = Mode(AppConfig.ItemGpuMem);
            string val = "…";
            double ratio = 0;
            if (s != null && s.GpuSharedTotalMB > 0)
            {
                val = MemPair(s.GpuSharedUsedMB, s.GpuSharedTotalMB);
                ratio = s.GpuSharedUsedMB / (double)s.GpuSharedTotalMB;
            }
            DrawRowCore(g, y, "共享", _p.GpuMem, ratio, val, mode, text);
            return y + RowH + RowGap;
        }

        private int DrawNetRow(Graphics g, SysInfoSnapshot s, int y, Brush text)
        {
            string mode = Mode(AppConfig.ItemNet);
            string unit = _config.Unit;
            string down = (s != null) ? FormatSpeed(s.NetDownBps, unit) : "…";
            string up = (s != null) ? FormatSpeed(s.NetUpBps, unit) : "…";

            g.DrawString("网络", _font, text, (float)PadX, (float)(y + _textOffY));

            if (mode == AppConfig.ModeBar || mode == AppConfig.ModeBoth)
            {
                int bx = BarX(mode);
                int bw = BarW2(mode);
                int half = (bw - 3) / 2;
                double rd = (s != null && s.NetDownPeak > 0) ? s.NetDownBps / s.NetDownPeak : 0;
                double ru = (s != null && s.NetUpPeak > 0) ? s.NetUpBps / s.NetUpPeak : 0;
                DrawBar(g, bx, y, half, _p.Net, rd);
                DrawBar(g, bx + half + 3, y, half, _p.Temp, ru);
            }
            if (mode == AppConfig.ModeNumber || mode == AppConfig.ModeBoth)
                DrawNetValue(g, y, mode, down, up);

            return y + RowH + RowGap;
        }

        // 下载箭头用 _p.Net（青）、上传箭头用 _p.Temp（橙），与条形颜色一致，
        // 且与数字分色，避免把 ↓/↑ 误看成数字 1。
        private void DrawNetValue(Graphics g, int y, string mode, string down, string up)
        {
            Font font = _smallFont;
            string aDown = "↓", aUp = "↑", space = " ";
            float wAD = g.MeasureString(aDown, font).Width;
            float wDown = g.MeasureString(down, font).Width;
            float wAU = g.MeasureString(aUp, font).Width;
            float wUp = g.MeasureString(up, font).Width;
            float wSp = g.MeasureString(space, font).Width;
            float total = wAD + wDown + wSp + wAU + wUp;
            float x = (ValueX(mode) + ValueW(mode)) - total;
            float yc = y + RowH / 2f;
            using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center })
            using (Brush bNet = new SolidBrush(_p.Net))
            using (Brush bTmp = new SolidBrush(_p.Temp))
            using (Brush bTxt = new SolidBrush(_p.Text))
            {
                g.DrawString(aDown, font, bNet, x, yc, sf); x += wAD;
                g.DrawString(down, font, bTxt, x, yc, sf); x += wDown;
                g.DrawString(space, font, bTxt, x, yc, sf); x += wSp;
                g.DrawString(aUp, font, bTmp, x, yc, sf); x += wAU;
                g.DrawString(up, font, bTxt, x, yc, sf);
            }
        }

        private int DrawDiskRow(Graphics g, DiskInfo d, int y, Brush text)
        {
            string mode = Mode(AppConfig.ItemDisk);
            string label = (d != null) ? d.Name : "磁盘";
            string val = "…";
            double ratio = 0;
            if (d != null && d.TotalBytes > 0)
            {
                double used = d.TotalBytes - d.FreeBytes;
                ratio = used / d.TotalBytes;
                val = GbTextBytes((ulong)used) + "/" + GbTextBytes(d.TotalBytes) + "GB";
            }
            DrawRowCore(g, y, label, _p.Disk, ratio, val, mode, text);
            return y + RowH + RowGap;
        }

        private void DrawLed(Graphics g, int x, int y, bool on, Color color)
        {
            int size = 8;
            using (Brush off = new SolidBrush(_p.Slot))
                g.FillEllipse(off, x, y, size, size);
            if (on)
            {
                using (Brush b = new SolidBrush(color))
                    g.FillEllipse(b, x, y, size, size);
            }
        }

        private int DrawIoRow(Graphics g, SysInfoSnapshot s, int y, Brush text)
        {
            g.DrawString("读写", _font, text, (float)PadX, (float)(y + _textOffY));
            bool readOn = (s != null) && s.DiskReadActive;
            bool writeOn = (s != null) && s.DiskWriteActive;

            DrawLed(g, PadX + LabelW + (int)(2 * _s), y + _ledOffY, readOn, _p.ReadLed);
            g.DrawString("读", _font, text, (float)(PadX + LabelW + (int)(16 * _s)), (float)(y + _textOffY));

            DrawLed(g, PadX + LabelW + (int)(34 * _s), y + _ledOffY, writeOn, _p.WriteLed);
            g.DrawString("写", _font, text, (float)(PadX + LabelW + (int)(48 * _s)), (float)(y + _textOffY));

            string val = "…";
            if (s != null)
                val = "读" + FormatBps(s.DiskReadBps) + " 写" + FormatBps(s.DiskWriteBps);
            DrawValue(g, y, val, text, AppConfig.ModeBoth, _smallFont);

            return y + RowH + RowGap;
        }

        // ================= 主题 =================

        private void ApplyTheme()
        {
            _p = new Palette();
            if (_config.Theme == "light")
            {
                _p.Bg = Color.FromArgb(247, 248, 250);
                _p.Border = Color.FromArgb(205, 211, 220);
                _p.Text = Color.FromArgb(43, 47, 54);
                _p.Dim = Color.FromArgb(120, 126, 136);
                _p.Slot = Color.FromArgb(226, 231, 237);
                _p.Warn = Color.FromArgb(229, 57, 53);
                _p.Cpu = Color.FromArgb(30, 136, 229);
                _p.Mem = Color.FromArgb(67, 160, 71);
                _p.Gpu = Color.FromArgb(142, 36, 170);
                _p.GpuMem = Color.FromArgb(0, 172, 193);
                _p.Net = Color.FromArgb(0, 172, 193);
                _p.Temp = Color.FromArgb(230, 74, 25);
                _p.Disk = Color.FromArgb(245, 124, 0);
                _p.ReadLed = Color.FromArgb(67, 160, 71);
                _p.WriteLed = Color.FromArgb(230, 74, 25);
            }
            else
            {
                _p.Bg = Color.FromArgb(31, 34, 39);
                _p.Border = Color.FromArgb(62, 68, 78);
                _p.Text = Color.FromArgb(232, 234, 237);
                _p.Dim = Color.FromArgb(140, 147, 158);
                _p.Slot = Color.FromArgb(42, 47, 54);
                _p.Warn = Color.FromArgb(229, 115, 115);
                _p.Cpu = Color.FromArgb(79, 195, 247);
                _p.Mem = Color.FromArgb(129, 199, 132);
                _p.Gpu = Color.FromArgb(186, 104, 200);
                _p.GpuMem = Color.FromArgb(77, 208, 225);
                _p.Net = Color.FromArgb(77, 208, 225);
                _p.Temp = Color.FromArgb(255, 138, 101);
                _p.Disk = Color.FromArgb(255, 183, 77);
                _p.ReadLed = Color.FromArgb(129, 199, 132);
                _p.WriteLed = Color.FromArgb(255, 138, 101);
            }
        }

        // ================= 托盘 =================

        private void BuildTray()
        {
            _tray = new NotifyIcon();
            _tray.Icon = CreateAppIcon();
            _tray.Text = "SysInfoDash 系统信息";
            _tray.ContextMenuStrip = _menu;
            _tray.DoubleClick += delegate(object s, EventArgs e) { ToggleWindow(); };
            _tray.Visible = true;
            UpdateTrayTooltip();
        }

        // 把当前系统信息写入托盘图标 ToolTip（鼠标悬浮时显示）。
        // NotifyIcon.Text 经典限制为 63 个字符，超长会被截断/不显示，故裁剪到 63。
        private void UpdateTrayTooltip()
        {
            if (_tray == null) return;
            SysInfoSnapshot s = (_collector != null) ? _collector.Current : null;
            string t;
            if (s == null)
            {
                t = "SysInfoDash 系统信息（采集中…）";
            }
            else
            {
                System.Text.StringBuilder b = new System.Text.StringBuilder();
                b.Append("CPU ").Append(s.CpuUsage.ToString("0")).Append("% ")
                  .Append((s.CpuMhz / 1000f).ToString("0.0")).Append("GHz | 内存 ")
                  .Append(MemPair(s.MemUsedMB, s.MemTotalMB));
                if (s.NetDownBps >= 1024 || s.NetUpBps >= 1024)
                    b.Append(" | 网 ").Append(FormatBps(s.NetDownBps)).Append("/").Append(FormatBps(s.NetUpBps));
                if (s.GpuUsage.HasValue)
                    b.Append(" | GPU ").Append(s.GpuUsage.Value.ToString("0")).Append("%");
                t = b.ToString();
            }
            if (t.Length > 63) t = t.Substring(0, 63);
            if (_tray.Text != t) _tray.Text = t;
        }

        private static Icon CreateAppIcon()
        {
            using (Bitmap bmp = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (GraphicsPath p = RoundedPath(1, 1, 30, 30, 7))
                    using (Brush bg = new SolidBrush(Color.FromArgb(31, 34, 39)))
                        g.FillPath(bg, p);

                    using (Pen pen = new Pen(Color.FromArgb(79, 195, 247), 2f))
                        g.DrawRectangle(pen, 7, 7, 18, 18);
                    using (Brush chip = new SolidBrush(Color.FromArgb(79, 195, 247)))
                        g.FillRectangle(chip, 11, 11, 10, 10);
                    using (Brush pin = new SolidBrush(Color.FromArgb(129, 199, 132)))
                    {
                        g.FillRectangle(pin, 6, 9, 2, 3);
                        g.FillRectangle(pin, 24, 9, 2, 3);
                        g.FillRectangle(pin, 6, 20, 2, 3);
                        g.FillRectangle(pin, 24, 20, 2, 3);
                        g.FillRectangle(pin, 9, 6, 3, 2);
                        g.FillRectangle(pin, 20, 6, 3, 2);
                        g.FillRectangle(pin, 9, 24, 3, 2);
                        g.FillRectangle(pin, 20, 24, 3, 2);
                    }
                }
                using (Icon icon = Icon.FromHandle(bmp.GetHicon()))
                    return (Icon)icon.Clone();
            }
        }

        private void ToggleWindow()
        {
            if (Visible) Hide();
            else
            {
                if (_dock == DockState.Free) ResizeToContent();
                Show();
                TopMost = true;
            }
        }

        // ================= 右键菜单 =================

        private void BuildMenu()
        {
            _menu = new ContextMenuStrip();
            _menu.ShowImageMargin = false;
            // 关键：ShowImageMargin=false 会让勾选标记没有绘制区域，
            // 必须开启 ShowCheckMargin 才会预留“勾选列”，否则 √ 永远不显示。
            _menu.ShowCheckMargin = true;
            _menu.Opening += delegate(object s, System.ComponentModel.CancelEventArgs e)
            {
                RefreshMenuChecks();
            };

            _miToggle = new ToolStripMenuItem("隐藏到托盘(&H)");
            _miToggle.Click += delegate(object s, EventArgs e) { ToggleWindow(); };
            _menu.Items.Add(_miToggle);
            _menu.Items.Add(new ToolStripSeparator());

            // ---- 显示项目 ----
            ToolStripMenuItem miItems = new ToolStripMenuItem("显示项目(&I)");
            AddShowItem(miItems, AppConfig.ItemCpu, "CPU利用率");
            AddShowItem(miItems, AppConfig.ItemCpuSpeed, "CPU频率");
            AddShowItem(miItems, AppConfig.ItemCpuTemp, "CPU温度");
            AddShowItem(miItems, AppConfig.ItemMem, "内存");
            AddShowItem(miItems, AppConfig.ItemGpu, "GPU利用率");
            AddShowItem(miItems, AppConfig.ItemGpuMem, "显存(含共享)");
            AddShowItem(miItems, AppConfig.ItemNet, "网络速度");
            AddShowItem(miItems, AppConfig.ItemDisk, "磁盘容量");
            AddShowItem(miItems, AppConfig.ItemDiskIo, "磁盘读写灯");
            _menu.Items.Add(miItems);

            // ---- 显示方式 ----
            ToolStripMenuItem miModes = new ToolStripMenuItem("显示方式(&M)");
            AddModeItem(miModes, AppConfig.ItemCpu, "CPU利用率");
            AddModeItem(miModes, AppConfig.ItemCpuSpeed, "CPU频率");
            AddModeItem(miModes, AppConfig.ItemCpuTemp, "CPU温度");
            AddModeItem(miModes, AppConfig.ItemMem, "内存");
            AddModeItem(miModes, AppConfig.ItemGpu, "GPU利用率");
            AddModeItem(miModes, AppConfig.ItemGpuMem, "显存(含共享)");
            AddModeItem(miModes, AppConfig.ItemNet, "网络速度");
            AddModeItem(miModes, AppConfig.ItemDisk, "磁盘容量");
            miModes.DropDownItems.Add(new ToolStripSeparator());
            AddModeAll(miModes, "全部：数字", AppConfig.ModeNumber);
            AddModeAll(miModes, "全部：条形", AppConfig.ModeBar);
            AddModeAll(miModes, "全部：数字+条形", AppConfig.ModeBoth);
            _menu.Items.Add(miModes);

            // ---- 网速单位 ----
            ToolStripMenuItem miUnit = new ToolStripMenuItem("网速单位(&U)");
            AddUnit(miUnit, "Mbps（兆比特/秒）", "Mbps");
            AddUnit(miUnit, "MBps（兆字节/秒）", "MBps");
            AddUnit(miUnit, "GBps（吉字节/秒）", "GBps");
            _menu.Items.Add(miUnit);

            // ---- 主题 ----
            ToolStripMenuItem miTheme = new ToolStripMenuItem("主题(&T)");
            AddTheme(miTheme, "深色", "dark");
            AddTheme(miTheme, "浅色", "light");
            _menu.Items.Add(miTheme);

            // ---- 字号（窗口随字号整体缩放） ----
            ToolStripMenuItem miFont = new ToolStripMenuItem("字号(&F)");
            AddFontSize(miFont, "极小 (6)", 6f);
            AddFontSize(miFont, "特小 (7)", 7f);
            AddFontSize(miFont, "小 (8)", 8f);
            AddFontSize(miFont, "标准 (9)", 9f);
            AddFontSize(miFont, "大 (10)", 10f);
            AddFontSize(miFont, "特大 (11)", 11f);
            AddFontSize(miFont, "超大 (12)", 12f);
            _menu.Items.Add(miFont);

            _menu.Items.Add(new ToolStripSeparator());

            _miAuto = new ToolStripMenuItem("开机启动(&A)");
            _miAuto.CheckOnClick = true;   // 点击自动切换勾选状态（√）
            _miAuto.Click += delegate(object s, EventArgs e) { SetAutoStart(_miAuto.Checked); };
            _menu.Items.Add(_miAuto);

            _miStartHidden = new ToolStripMenuItem("开机启动后隐藏到托盘(&T)");
            _miStartHidden.CheckOnClick = true;   // 点击自动切换勾选状态（√）
            _miStartHidden.Click += delegate(object s, EventArgs e)
            {
                _config.StartHidden = _miStartHidden.Checked;
                if (_config.StartHidden) _config.AutoStart = true;   // 隐藏启动必须以开机启动为前提
                ApplyAutoStartRegistration();   // 重新登记注册表（决定要不要带 /tray 参数）
                _config.Save();
                RefreshMenuChecks();   // 立即刷新，使“开机启动”同步勾选
            };
            _menu.Items.Add(_miStartHidden);

            _miUndock = new ToolStripMenuItem("解除贴边(&D)");
            _miUndock.Click += delegate(object s, EventArgs e) { UndockToFree(); };
            _menu.Items.Add(_miUndock);

            // ---- 置顶显示 ----
            _miTopMost = new ToolStripMenuItem("置顶显示(&W)");
            _miTopMost.CheckOnClick = true;
            _miTopMost.Click += delegate(object s, EventArgs e)
            {
                _config.TopMost = _miTopMost.Checked;
                this.TopMost = _config.TopMost;
                _config.Save();
            };
            _menu.Items.Add(_miTopMost);

            // ---- 鼠标穿透（点击穿透到下层窗口） ----
            _miClickThrough = new ToolStripMenuItem("鼠标穿透(&C)");
            _miClickThrough.CheckOnClick = true;
            _miClickThrough.Click += delegate(object s, EventArgs e)
            {
                _config.ClickThrough = _miClickThrough.Checked;
                ApplyClickThrough();
                _config.Save();
            };
            _menu.Items.Add(_miClickThrough);

            // ---- 透明度（窗口半透明百分比） ----
            ToolStripMenuItem miOpacity = new ToolStripMenuItem("透明度(&O)");
            int[] pcts = { 100, 80, 60, 40, 20 };
            for (int i = 0; i < pcts.Length; i++)
            {
                int p = pcts[i];
                ToolStripMenuItem mi = new ToolStripMenuItem(p + "%");
                mi.CheckOnClick = true;
                mi.Click += delegate(object s, EventArgs e)
                {
                    double op = p / 100.0;
                    _config.Opacity = op;
                    this.Opacity = op;
                    _config.Save();
                    RefreshMenuChecks();   // 立即刷新其它透明度项的勾选
                };
                miOpacity.DropDownItems.Add(mi);
                _opacityChecks[i] = mi;
            }
            _menu.Items.Add(miOpacity);

            // ---- 靠边把手显示指标（最多 2 个） ----
            ToolStripMenuItem miDock = new ToolStripMenuItem("把手显示(&M)");
            foreach (string id in DockCandidates)
            {
                ToolStripMenuItem mi = new ToolStripMenuItem(DockLabel(id));
                mi.CheckOnClick = true;
                string cid = id;
                mi.Click += delegate(object s2, EventArgs e2) { ToggleDockMetric(cid, mi); };
                miDock.DropDownItems.Add(mi);
                _dockMetricChecks[id] = mi;
            }
            _menu.Items.Add(miDock);

            ToolStripMenuItem miExit = new ToolStripMenuItem("退出(&X)");
            miExit.Click += delegate(object s, EventArgs e) { ExitApp(); };
            _menu.Items.Add(miExit);
        }

        private void AddShowItem(ToolStripMenuItem parent, string id, string label)
        {
            ToolStripMenuItem mi = new ToolStripMenuItem(label);
            mi.CheckOnClick = true;
            mi.Checked = Show(id);
            string itemId = id;
            mi.Click += delegate(object s, EventArgs e)
            {
                _config.Show[itemId] = mi.Checked;
                _config.Save();
                ResizeToContent();
                Invalidate();
            };
            parent.DropDownItems.Add(mi);
            _itemChecks[id] = mi;
        }

        private void AddModeItem(ToolStripMenuItem parent, string id, string label)
        {
            ToolStripMenuItem group = new ToolStripMenuItem(label);
            ToolStripMenuItem n = new ToolStripMenuItem("数字");
            ToolStripMenuItem b = new ToolStripMenuItem("条形");
            ToolStripMenuItem both = new ToolStripMenuItem("数字+条形");
            n.Tag = AppConfig.ModeNumber;
            b.Tag = AppConfig.ModeBar;
            both.Tag = AppConfig.ModeBoth;

            string itemId = id;
            ToolStripMenuItem[] items = new ToolStripMenuItem[] { n, b, both };
            foreach (ToolStripMenuItem mi in items)
            {
                string m = (string)mi.Tag;
                mi.Click += delegate(object s, EventArgs e)
                {
                    _config.Mode[itemId] = m;
                    _config.Save();
                    Invalidate();
                };
                group.DropDownItems.Add(mi);
            }
            parent.DropDownItems.Add(group);
            _modeChecks[id] = items;
        }

        private void AddModeAll(ToolStripMenuItem parent, string label, string mode)
        {
            ToolStripMenuItem mi = new ToolStripMenuItem(label);
            string m = mode;
            mi.Click += delegate(object s, EventArgs e)
            {
                List<string> keys = new List<string>(_config.Mode.Keys);
                foreach (string key in keys)
                {
                    if (key != AppConfig.ItemDiskIo) _config.Mode[key] = m;
                }
                _config.Save();
                Invalidate();
            };
            parent.DropDownItems.Add(mi);
        }

        private void AddUnit(ToolStripMenuItem parent, string label, string unit)
        {
            ToolStripMenuItem mi = new ToolStripMenuItem(label);
            string u = unit;
            mi.Click += delegate(object s, EventArgs e)
            {
                _config.Unit = u;
                _config.Save();
                Invalidate();
            };
            parent.DropDownItems.Add(mi);
            if (unit == "Mbps") _unitChecks[0] = mi;
            else if (unit == "MBps") _unitChecks[1] = mi;
            else _unitChecks[2] = mi;
        }

        private void AddTheme(ToolStripMenuItem parent, string label, string theme)
        {
            ToolStripMenuItem mi = new ToolStripMenuItem(label);
            string t = theme;
            mi.Click += delegate(object s, EventArgs e)
            {
                _config.Theme = t;
                _config.Save();
                ApplyTheme();
                Invalidate();
            };
            parent.DropDownItems.Add(mi);
            if (theme == "dark") _themeChecks[0] = mi;
            else _themeChecks[1] = mi;
        }

        private void AddFontSize(ToolStripMenuItem parent, string label, float size)
        {
            ToolStripMenuItem mi = new ToolStripMenuItem(label);
            float sz = size;
            mi.Click += delegate(object s, EventArgs e) { SetFontSize(sz); };
            parent.DropDownItems.Add(mi);
            if (_fontChecks == null) _fontChecks = new ToolStripMenuItem[7];   // 6~12
            int idx = (int)Math.Round(size) - 6;
            if (idx >= 0 && idx < _fontChecks.Length) _fontChecks[idx] = mi;
        }

        private void SetFontSize(float size)
        {
            _config.FontSize = size;
            ComputeLayout();
            if (_font != null) _font.Dispose();
            if (_smallFont != null) _smallFont.Dispose();
            _font = new Font("Segoe UI", size);
            _smallFont = new Font("Segoe UI", Math.Max(6f, size * 0.833f));
            ResizeToContent();
            Invalidate();
            _config.Save();
        }

        private void ToggleDockMetric(string id, ToolStripMenuItem mi)
        {
            List<string> list = new List<string>(_config.DockMetrics ?? new string[0]);
            if (mi.Checked)
            {
                if (!list.Contains(id)) list.Add(id);
            }
            else
            {
                list.Remove(id);
            }
            while (list.Count > 2) list.RemoveAt(0);   // 最多保留 2 个（超出时移除最早选的）
            _config.DockMetrics = list.ToArray();
            _config.Save();
            RefreshMenuChecks();
        }

        private void RefreshMenuChecks()
        {
            _miToggle.Text = Visible ? "隐藏到托盘(&H)" : "显示窗口(&H)";
            _miUndock.Enabled = (_dock != DockState.Free);
            _miAuto.Checked = _config.AutoStart;          // 以配置为准，真实反映用户选择（不依赖注册表回读）
            _miStartHidden.Checked = _config.StartHidden;
            // 不再禁用“隐藏到托盘”：未开启开机启动时也可勾选，勾选时会自动连带开启开机启动

            _miTopMost.Checked = _config.TopMost;
            _miClickThrough.Checked = _config.ClickThrough;
            int curPct = (int)Math.Round(_config.Opacity * 100);
            for (int i = 0; i < _opacityChecks.Length; i++)
            {
                if (_opacityChecks[i] == null) continue;
                int p = 100 - i * 20;   // 100/80/60/40/20
                _opacityChecks[i].Checked = (p == curPct);
            }

            foreach (KeyValuePair<string, ToolStripMenuItem> kv in _dockMetricChecks)
                kv.Value.Checked = (_config.DockMetrics != null && Array.IndexOf(_config.DockMetrics, kv.Key) >= 0);

            foreach (KeyValuePair<string, ToolStripMenuItem> kv in _itemChecks)
                kv.Value.Checked = Show(kv.Key);

            foreach (KeyValuePair<string, ToolStripMenuItem[]> kv in _modeChecks)
            {
                if (kv.Value == null) continue;
                string cur = Mode(kv.Key);
                foreach (ToolStripMenuItem mi in kv.Value)
                    mi.Checked = (string.Equals((string)mi.Tag, cur, StringComparison.Ordinal));
            }

            for (int i = 0; i < 3; i++)
            {
                if (_unitChecks[i] == null) continue;
                string u = (i == 0) ? "Mbps" : (i == 1) ? "MBps" : "GBps";
                _unitChecks[i].Checked = (u == _config.Unit);
            }

            for (int i = 0; i < (_fontChecks == null ? 0 : _fontChecks.Length); i++)
            {
                if (_fontChecks[i] != null)
                    _fontChecks[i].Checked = (_config.FontSize == 6 + i);
            }
            for (int i = 0; i < 2; i++)
            {
                if (_themeChecks[i] == null) continue;
                string t = (i == 0) ? "dark" : "light";
                _themeChecks[i].Checked = (t == _config.Theme);
            }
        }

        // ================= 开机启动 =================

        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "SysInfoDash";

        // 注册表项内容：当前 exe 路径；若开启“开机启动后隐藏到托盘”，附带 /tray 参数
        private string RunValueTarget
        {
            get
            {
                string p = "\"" + Application.ExecutablePath + "\"";
                if (_config.StartHidden) p += " /tray";
                return p;
            }
        }

        // 判断开机启动是否指向“当前这个 exe”（而非其它副本或旧路径）
        private static bool AutoStartEnabled()
        {
            try
            {
                using (RegistryKey rk = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                {
                    if (rk == null) return false;
                    object v = rk.GetValue(RunValueName);
                    if (v == null) return false;
                    return v.ToString().IndexOf(Application.ExecutablePath,
                        StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch { return false; }
        }

        // 统一登记/撤除开机启动注册表项（依据 _config.AutoStart 与 RunValueTarget）
        private void ApplyAutoStartRegistration()
        {
            try
            {
                using (RegistryKey rk = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (rk == null) return;
                    string cur = RunValueTarget;
                    object existing = rk.GetValue(RunValueName);
                    if (_config.AutoStart)
                    {
                        if (existing == null || existing.ToString() != cur)
                            rk.SetValue(RunValueName, cur);
                    }
                    else if (existing != null && existing.ToString() == cur)
                    {
                        rk.DeleteValue(RunValueName, false);
                    }
                }
            }
            catch { }
        }

        // 启动自愈：保证注册表里的开机启动项与当前 exe 位置/参数一致。
        private void SyncAutoStart()
        {
            ApplyAutoStartRegistration();
        }

        private void SetAutoStart(bool enable)
        {
            _config.AutoStart = enable;
            if (!enable) _config.StartHidden = false;   // 关闭开机启动时一并关闭“隐藏到托盘”
            ApplyAutoStartRegistration();
            _config.Save();
        }
    }
}
