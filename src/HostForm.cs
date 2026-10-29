// TaskHarbor: top-anchored hover workspace with a constant-size WebView viewport.
// The native timer translates WebView content and reveals its rounded window region.
// Never resize/scale a ULW handle bitmap during motion. Switch rendering mode only at
// transition boundaries; never call SetAlpha while _pixelBall is true (see Native.cs).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace TaskHarbor
{
    internal class HostForm : Form
    {
        // ---------------- 尺寸（逻辑像素，按 DPI 缩放后使用） ----------------
        private const int BALL = 68;
        private const int PANEL_W = 860;
        private const int PANEL_H = 780;
        private const int PANEL_RADIUS = 16;

        // ---------------- 交互节奏 ----------------
        // v1.6：IDLE_MS 从 5000 一路收到 1000 —— 用户先要「静默 2 秒就吸附边缘」，
        // 试过之后觉得还是慢，改成 1 秒。
        // v1.7：只剩一个地方用它了 —— **面板态**「1 秒无操作 → 收起成贴边一小截」。
        //       贴边态是稳态，**永远不超时**（WatchTick 里 `if (_docked) return;`），
        //       所以这里不再需要「球态」那条分支。
        // ⚠ 「1 秒」对面板来说已经相当急 —— 如果之后觉得打字/看列表时面板老被收走，
        // 把它调大，或者拆成 PANEL_IDLE_MS / DOCK_IDLE_MS 两个常量。
        private const int IDLE_MS = 1000;        // 面板无操作多久 → 收起成贴边一小截
        private const double DOCK_VIS = 0.42;    // 吸附时露出部分占球直径的比例
        private const int CLICK_SLOP = 6;        // 位移小于这个值（逻辑像素）算点击
        // ⚠ v1.7 删除 NEAR_PAD：它原本用于「鼠标靠近贴边球就弹回」，
        //   现在贴边态是唯一的收起形态、不再弹回，所以不需要这个距离阈值。

        private float _scale = 1f;
        private int _ballSize, _panelW, _panelH, _panelRadius;

        // ---------------- 状态 ----------------
        private WebView2 _wv;
        private bool _ready;
        private bool _expanded;
        private bool _docked;
        private bool _dragging;
        private bool _pinned;           // 钉住：不自动收起
        private bool _interacting;      // 编辑、设置、确认或拖动任务期间保持展开
        private DesktopServices _desktop;
        private bool _hotkeyRegistered;
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
        private bool _suppressHide;     // 弹对话框期间不自动收起
        private bool _translucent = true;
        private bool _pixelBall;        // 稳态（贴边态）：正在用 GDI+ 逐像素绘制（WebView2 已隐藏）
        private int _pixAlpha = -1;     // 当前已经烘进那张位图里的不透明度（-1 = 没有位图）
        private int _pixN = -1;         // 上次 ULW 用的位图边长；窗口尺寸一变就必须重画
        private int _badge;             // 角标数字，由网页报上来
        private ContextMenuStrip _ballMenu;
        private bool _menuOpen;
        private string _dockSide = "";  // left / right / top / bottom
        private Point _anchor;          // 球心（贴边锚点）
        private PointF _anchorOffset;   // 面板左上角 → 锚点的偏移（拖动面板时用）
        private long _lastActive;       // 最后一次「有操作」的时刻
        private readonly EdgeHoverState _edgeHover = new EdgeHoverState();
        private Rectangle _hoverHandleBounds;
        private readonly Timer _slide = new Timer();
        private bool _sliding, _slideOpening, _slideAutomatic;
        private int _slideFrom, _slideTo, _slideVisible;
        private long _slideStarted;
        private double _slideDuration;


        private readonly Timer _watch = new Timer();

        // 进入逐像素模式后不立刻藏网页：先让网页把 #ball 收起来、画完一帧，
        // 再藏（见 EnterPixelBall）。这样切换的瞬间不会闪一下网页画的球。
        private readonly Timer _wvHide = new Timer();


        private Region _region;
        private Dictionary<string, object> _cfg;
        private AiJob _job;

        private static readonly System.Web.Script.Serialization.JavaScriptSerializer JS =
            new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };

        // ============================================================
        public HostForm()
        {
            _scale = 1f;
            try
            {
                using (Graphics g = this.CreateGraphics())
                {
                    _scale = g.DpiX / 96f;
                }
            }
            catch { }
            if (_scale < 0.8f || _scale > 3f) _scale = 1f;

            _ballSize = (int)Math.Round(BALL * _scale);
            _panelW = (int)Math.Round(PANEL_W * _scale);
            _panelH = (int)Math.Round(PANEL_H * _scale);
            _panelRadius = (int)Math.Round(PANEL_RADIUS * _scale * 1.6f);

            this.FormBorderStyle = FormBorderStyle.None;
            this.ShowInTaskbar = false;
            this.TopMost = true;
            this.StartPosition = FormStartPosition.Manual;
            this.BackColor = Color.FromArgb(252, 252, 254);
            this.Text = "TaskHarbor";
            this.DoubleBuffered = true;
            this.KeyPreview = true;

            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            _anchor = new Point(TopPanelMotion.RightAnchor(wa, _ballSize, (int)Math.Round(24 * _scale)), wa.Top + _ballSize / 2);
            _lastActive = Now;

            _wv = new WebView2();
            _wv.Dock = DockStyle.Fill;
            this.Controls.Add(_wv);

            _wvHide.Interval = 140;                 // 够网页收球并渲染完一帧
            _wvHide.Tick += new EventHandler(WvHideTick);
            _watch.Interval = 110;
            _watch.Tick += new EventHandler(WatchTick);
            _slide.Interval = 15;
            _slide.Tick += new EventHandler(SlideTick);
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
        }

        private void DisplaySettingsChanged(object sender, EventArgs e)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(new Action(delegate {
                if (IsDisposed || !_ready) return;
                ClampAnchor();
                if (_expanded) { FinishSlideOpen(); ApplyBounds(PanelRectFromDock("top"), _panelRadius); }
                else ApplyBallBounds(false);
                SaveAnchor();
            })); } catch { }
        }

        // ============================================================
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.MakeToolWindow(this.Handle);
            _translucent = CB("glass", true);
            MigrateV13();
            Native.SetAlpha(this.Handle, BallAlpha());   // 必须！分层窗口不设 Alpha 会不显示
            ApplyAppIcon();
            ApplyBallBounds(false);
        }

        /// <summary>
        /// 运行时把圆形图标塞给窗口（WM_SETICON）。
        /// 编译参数 /win32icon 失败时 exe 里没有图标资源，任务栏就会显示白纸默认图标；
        /// 这里自己设一遍最稳。失败无所谓，不影响功能。
        /// </summary>
        private void ApplyAppIcon()
        {
            try
            {
                string ico = WebAssets.IconPath();
                if (ico != null) Native.ApplyIcon(this.Handle, ico);
            }
            catch { }
        }

        /// <summary>
        /// v1.3 一次性迁移。
        /// 悬浮球这版从「网页渲染 + Region 裁剪」改成宿主逐像素绘制（圆边才有抗锯齿），
        /// 球的样子也按你给的参考图定成实心白球：旧配置里 ballAlpha 存的是 227（89%），
        /// 那是老版本的默认值，谁都没主动调过，但它会让新球看着还是发灰。
        /// 只迁一次并打标记 —— 你之后自己把滑块拉低，不会被再改回去。
        /// </summary>
        private void MigrateV13()
        {
            try
            {
                if (CS("ballV3", "") == "1") return;

                // SaveConfig 会把明文 Key 换成密文并把明文键删掉。这里先留一份，
                // 存完补回内存，免得「设置」页里的 API Key 变成空白。
                string k = CS("apiKey", "");
                string ik = CS("imgApiKey", "");

                _cfg["ballAlpha"] = "255";
                _cfg["ballV3"] = "1";
                Store.SaveConfig(_cfg);

                if (k.Length > 0) _cfg["apiKey"] = k;
                if (ik.Length > 0) _cfg["imgApiKey"] = ik;
            }
            catch { }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _hotkeyRegistered = RegisterHotKey(this.Handle, 0x5448, 0x4000 | 0x0002 | 0x0004, 0x54);
            _desktop = new DesktopServices(
                delegate { SetLayout("workbench"); ShowPanel(); },
                delegate { SetLayout("compact"); ShowPanel(); },
                delegate { Nav("askquit"); },
                delegate(string taskId) { ShowPanel(); Emit("{\"evt\":\"nav\",\"to\":\"task\",\"taskId\":" + Q(taskId) + "}"); });
            InitWeb();
            _watch.Start();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SaveAnchor();
            base.OnFormClosing(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0312 && m.WParam.ToInt32() == 0x5448) { ShowPanel(); Emit("{\"evt\":\"nav\",\"to\":\"capture\"}"); }
            base.WndProc(ref m);
        }

        private void SetLayout(string layout)
        {
            if (_cfg == null) _cfg = Store.LoadConfig();
            _cfg["layout"] = layout == "compact" ? "compact" : "workbench";
            if (!Store.SaveConfig(_cfg)) throw new IOException("窗口偏好未能保存");
            if (_expanded) { FinishSlideOpen(); ApplyBounds(PanelRectFromDock("top"), _panelRadius); }
            Emit("{\"evt\":\"layout\",\"layout\":" + Q(_cfg["layout"].ToString()) + "}");
        }

        // ---------------- WebView2 ----------------
        private async void InitWeb()
        {
            try
            {
                string webDir = WebAssets.Ensure();
                string udf = Path.Combine(Store.AppDir, "WebView2");
                try { if (!Directory.Exists(udf)) Directory.CreateDirectory(udf); }
                catch { }

                CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(null, udf, null);
                await _wv.EnsureCoreWebView2Async(env);

                CoreWebView2 cw = _wv.CoreWebView2;
                cw.Settings.AreDefaultContextMenusEnabled = false;
                cw.Settings.AreDevToolsEnabled = false;
                cw.Settings.IsStatusBarEnabled = false;
                cw.Settings.IsZoomControlEnabled = false;
                cw.Settings.AreBrowserAcceleratorKeysEnabled = false;
                cw.Settings.IsPasswordAutosaveEnabled = false;
                cw.Settings.IsGeneralAutofillEnabled = false;

                // 页面本身透明，透出来的部分交给窗口 Alpha 决定
                try { _wv.DefaultBackgroundColor = Color.Transparent; } catch { }

                cw.WebMessageReceived += new EventHandler<CoreWebView2WebMessageReceivedEventArgs>(OnWebMessage);

                cw.SetVirtualHostNameToFolderMapping("taskharbor.local", webDir, CoreWebView2HostResourceAccessKind.Allow);
                cw.Navigate("https://taskharbor.local/panel.html");

                _ready = true;
                this.Visible = true;
                this.Activate();
                Emit("{\"evt\":\"mode\",\"mode\":\"ball\",\"side\":\"\"}");
                EnterPixelBall();     // 启动就切成逐像素绘制，球的圆边才是平滑的
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "界面组件初始化失败。\n\n" + ex.Message +
                    "\n\n如果这台电脑没装过 WebView2 运行时，可以在微软官网搜 “WebView2 Runtime” 安装一次（Windows 11 一般已自带）。",
                    "TaskHarbor", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                Application.Exit();
            }
        }

        // ---------------- 与网页通信 ----------------
        private void Send(string json)
        {
            try
            {
                if (_ready && _wv != null && _wv.CoreWebView2 != null)
                    _wv.CoreWebView2.PostWebMessageAsJson(json);
            }
            catch { }
        }

        private static string Q(string s)
        {
            if (s == null) s = "";
            StringBuilder sb = new StringBuilder(s.Length + 8);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return "\"" + sb.ToString() + "\"";
        }

        private void Reply(int id, string extra)
        {
            string s = "{\"id\":" + id.ToString();
            if (!string.IsNullOrEmpty(extra)) s += "," + extra;
            s += "}";
            Send(s);
        }

        private void Emit(string json) { Send(json); }

        private void EmitMode()
        {
            // v1.7：只有 panel / dock 两种。不再有 "ball" —— 网页侧对应的分支
            // 已经在 panel.js 里删掉；这里也保留兜底，万一状态没摆好就报 dock。
            string m = _expanded ? "panel" : "dock";
            Emit("{\"evt\":\"mode\",\"mode\":\"" + m + "\",\"side\":\"" + _dockSide + "\"}");
        }

        // ---------------- 配置读取 ----------------
        private Dictionary<string, object> Cfg
        {
            get
            {
                if (_cfg == null) _cfg = Store.LoadConfig();
                return _cfg;
            }
        }

        private string CS(string key, string def)
        {
            if (_cfg == null) _cfg = Store.LoadConfig();
            try
            {
                if (_cfg != null && _cfg.ContainsKey(key) && _cfg[key] != null)
                {
                    string v = _cfg[key].ToString();
                    if (v.Length > 0) return v;
                }
            }
            catch { }
            return def;
        }

        private bool CB(string key, bool def)
        {
            if (_cfg == null) _cfg = Store.LoadConfig();
            try
            {
                if (_cfg != null && _cfg.ContainsKey(key)) return Store.Bool(_cfg, key, def);
            }
            catch { }
            return def;
        }

        private int CI(string key, int def)
        {
            try { return int.Parse(CS(key, def.ToString())); }
            catch { return def; }
        }

        /// <summary>贴边那一小截当前的不透明度（0-255）。面板永远是 255。
        /// v1.7：改名前的语义是「悬浮球」，现在球那一态没了，作用对象是贴边形态。
        /// 配置键仍叫 ballAlpha（改动它会让老用户的设置失效，不值当）。</summary>
        private int BallAlpha()
        {
            if (!_translucent) return 255;
            int a = CI("ballAlpha", 255);
            if (a < 90) a = 90;
            if (a > 255) a = 255;
            return a;
        }

        private void ApplyAlpha()
        {
            // 逐像素模式下跟 SetLayeredWindowAttributes 不是一个模式（混用会让
            // UpdateLayeredWindow 失效），透明度得重画一遍烘进像素里
            if (_pixelBall) { RepaintBall(); return; }
            Native.SetAlpha(this.Handle, _expanded ? 255 : BallAlpha());
        }

        // ---------------- 消息分发 ----------------
        private void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string json = null;
            try { json = e.WebMessageAsJson; }
            catch { return; }
            if (string.IsNullOrEmpty(json)) return;

            Dictionary<string, object> m;
            try { m = JS.Deserialize<Dictionary<string, object>>(json); }
            catch { return; }
            if (m == null || !m.ContainsKey("cmd")) return;

            string cmd = m["cmd"] == null ? "" : m["cmd"].ToString();
            int id = 0;
            if (m.ContainsKey("id") && m["id"] != null) int.TryParse(m["id"].ToString(), out id);

            // 网页那边有任何动作都算「有操作」，避免打字时把面板收走
            if (cmd != "alive") _lastActive = Now;

            try
            {
                Dispatch(cmd, id, m);
            }
            catch (Exception ex)
            {
                Reply(id, "\"ok\":false,\"err\":" + Q(ex.Message));
            }
        }

        private static string MS(Dictionary<string, object> m, string key)
        {
            if (m.ContainsKey(key) && m[key] != null) return m[key].ToString();
            return "";
        }

        private void Dispatch(string cmd, int id, Dictionary<string, object> m)
        {
            switch (cmd)
            {
                case "load":
                    {
                        // 必须先读配置：自定义存储路径存在配置里，
                        // 先读任务的话第一次启动会读错文件（读到默认目录）。
                        _cfg = Store.LoadConfig();
                        if (Program.DemoMode && !_cfg.ContainsKey("scope")) { _cfg["scope"] = "all"; _cfg["pinned"] = true; }
                        _cfg["migrationNote"] = Store.MigrationNote;
                        string tasks = Store.LoadTasks();
                        _translucent = CB("glass", true);
                        _pinned = CB("pinned", false);
                        _lastActive = Now;
                        try
                        {
                            string bx = CS("ballX", "");
                            string by = CS("ballY", "");
                            if (bx.Length > 0 && by.Length > 0)
                            {
                                Point pt = new Point(int.Parse(bx), int.Parse(by));
                                if (IsOnScreen(pt)) _anchor = pt;
                            }
                        }
                        catch { }
                        ClampAnchor();
                        ApplyAlpha();      // 配置里的通透度可能变了（必须在切逐像素模式之前）
                        // v1.7：贴边态也要重新摆一次 —— 锚点/通透度可能都改了
                        if (!_expanded) ApplyBallBounds(false);

                        Reply(id, "\"tasks\":" + Q(tasks) +
                                 ",\"cfg\":" + Q(JS.Serialize(_cfg)) +
                                 ",\"dataPath\":" + Q(Store.TasksPath) +
                                 ",\"autoStart\":" + (Store.GetAutoStart() ? "true" : "false") +
                                 ",\"version\":" + Q(AppVersion) +
                                 ",\"demo\":" + (Program.DemoMode ? "true" : "false") +
                                 ",\"hotkey\":" + (_hotkeyRegistered ? "true" : "false"));
                        if (Program.DemoMode && !_expanded) BeginInvoke(new Action(delegate { ShowPanel(); }));
                    }
                    break;

                case "saveTasks":
                    Reply(id, Store.SaveTasks(MS(m, "tasks")) ? "\"ok\":true" : "\"ok\":false,\"err\":\"任务保存失败\"");
                    break;

                // ---------------- 音效 ----------------
                //  网页只负责「什么时候响」，音色一律由宿主这边合成播放。
                //  这样音色表只维护一处（Sound.cs），也不用给网页塞一堆音频文件。
                case "sound":
                    {
                        string sid = MS(m, "soundId");
                        //  网页没传 id 就用配置里的默认音色
                        if (sid.Length == 0) sid = CS("soundId", "soft");
                        Sound.Play(sid);
                        Reply(id, "\"ok\":true,\"sound\":" + Q(sid) + ",\"muted\":" +
                            (Sound.IsMuted() ? "true" : "false"));
                    }
                    break;

                //  设置页「试听」：无论配置里选的什么，直接播传进来的那个
                case "soundPreview":
                    {
                        string sid = MS(m, "soundId");
                        if (sid.Length == 0) sid = "soft";
                        Sound.Play(sid);
                        Reply(id, "\"ok\":true,\"sound\":" + Q(sid));
                    }
                    break;

                //  音色列表 / 系统音量 / 是否静音 —— 让设置页能把真实状态显示出来
                case "soundInfo":
                    {
                        StringBuilder sb = new StringBuilder();
                        sb.Append("\"ids\":[");
                        for (int i = 0; i < Sound.IDS.Length; i++)
                        {
                            if (i > 0) sb.Append(",");
                            sb.Append("{\"id\":").Append(Q(Sound.IDS[i]))
                              .Append(",\"name\":").Append(Q(Sound.NAMES[i]))
                              .Append(",\"tip\":").Append(Q(Sound.TIPS[i])).Append("}");
                        }
                        sb.Append("],\"cur\":").Append(Q(CS("soundId", "soft")))
                          .Append(",\"vol\":").Append(Sound.SystemVolume())
                          .Append(",\"muted\":").Append(Sound.IsMuted() ? "true" : "false")
                          .Append(",\"devices\":").Append(Native.WaveOutDeviceCount());
                        Reply(id, sb.ToString());
                    }
                    break;

                //  把当前选中的音色导成 wav 落到数据目录，方便自己听波形对不对
                case "soundDump":
                    {
                        string sid = MS(m, "soundId");
                        if (sid.Length == 0) sid = CS("soundId", "soft");
                        string p = Path.Combine(Store.AppDir, "sound-" + sid + ".wav");
                        bool ok = Sound.DumpWav(sid, p);
                        Reply(id, ok ? ("\"path\":" + Q(p)) : "\"err\":\"导出失败\"");
                    }
                    break;

                case "saveCfg":
                    {
                        string cfgJson = MS(m, "cfg");
                        if (cfgJson.Length > 0)
                        {
                            try
                            {
                                Dictionary<string, object> d = JS.Deserialize<Dictionary<string, object>>(cfgJson);
                                if (d != null)
                                {
                                    // 与已有配置合并，避免丢字段
                                    Dictionary<string, object> cur = Store.LoadConfig();
                                    foreach (KeyValuePair<string, object> kv in d) cur[kv.Key] = kv.Value;
                                    if (!Store.SaveConfig(cur)) throw new IOException("配置保存失败");
                                    _cfg = cur;
                                }
                            }
                            catch (Exception error) { Reply(id, "\"ok\":false,\"err\":" + Q(error.Message)); return; }
                        }
                        Reply(id, "\"ok\":true");
                    }
                    break;

                case "applyCfg":
                    {
                        _cfg = Store.LoadConfig();
                        _translucent = CB("glass", true);
                        bool wantTop = CB("topmost", true);
                        this.TopMost = wantTop;
                        Native.SetTopMost(this.Handle, wantTop);
                        ApplyAlpha();
                        Reply(id, "\"ok\":true");
                    }
                    break;

                case "setLayout":
                    SetLayout(MS(m, "layout"));
                    Reply(id, "\"ok\":true");
                    break;

                // 把手按下仅用于拖动；展开由鼠标进入透明区域触发。
                // （逐像素绘制模式下网页收不到鼠标事件，同样的逻辑由 OnMouseDown 走这里）
                case "ballDown":
                    HandleBallPress();
                    Reply(id, "\"ok\":true");
                    break;

                case "setBadge":
                    {
                        int n = 0;
                        int.TryParse(MS(m, "n"), out n);
                        if (n != _badge)
                        {
                            _badge = n;
                            if (_pixelBall) RepaintBall();   // 稳态球才需要自己重画
                        }
                        Reply(id, "\"ok\":true");
                    }
                    break;

                case "alive":
                    _lastActive = Now;
                    break;

                case "expand":
                    Expand();
                    Reply(id, "\"ok\":true");
                    break;

                case "collapse":
                    Collapse();
                    Reply(id, "\"ok\":true");
                    break;

                case "drag":
                    DoDragWindow();
                    Reply(id, "\"ok\":true");
                    break;

                case "setPinned":
                    _pinned = MS(m, "on") == "True" || MS(m, "on") == "true";
                    Reply(id, "\"ok\":true");
                    break;

                case "setInteracting":
                    _interacting = MS(m, "on") == "True" || MS(m, "on") == "true";
                    _lastActive = Now;
                    Reply(id, "\"ok\":true");
                    break;

                case "setGlass":          // 现在表示「半透明」开关
                    {
                        bool on = MS(m, "on") == "True" || MS(m, "on") == "true";
                        _translucent = on;
                        ApplyAlpha();
                        Reply(id, "\"ok\":true");
                    }
                    break;

                case "setAlpha":
                    {
                        int a = 228;
                        int.TryParse(MS(m, "a"), out a);
                        if (_cfg == null) _cfg = Store.LoadConfig();
                        _cfg["ballAlpha"] = a.ToString();
                        Store.SaveConfig(_cfg);
                        ApplyAlpha();
                        Reply(id, "\"ok\":true");
                    }
                    break;

                case "setTopmost":
                    {
                        bool on = MS(m, "on") == "True" || MS(m, "on") == "true";
                        this.TopMost = on;
                        Native.SetTopMost(this.Handle, on);
                        Reply(id, "\"ok\":true");
                    }
                    break;

                case "setAutoStart":
                    {
                        bool on = MS(m, "on") == "True" || MS(m, "on") == "true";
                        string err;
                        bool ok2 = Store.SetAutoStart(on, out err);
                        Reply(id, ok2 ? ("\"ok\":true,\"on\":" + (on ? "true" : "false")) : ("\"ok\":false,\"err\":" + Q(err)));
                    }
                    break;

                case "pickFolder":
                    {
                        _suppressHide = true;
                        try
                        {
                            using (FolderBrowserDialog d = new FolderBrowserDialog())
                            {
                                d.Description = "选择任务数据的保存文件夹";
                                d.ShowNewFolderButton = true;
                                string cur = Path.GetDirectoryName(Store.TasksPath);
                                if (!string.IsNullOrEmpty(cur) && Directory.Exists(cur)) d.SelectedPath = cur;
                                if (d.ShowDialog(this) == DialogResult.OK)
                                {
                                    string np = Path.Combine(d.SelectedPath, "tasks.json");
                                    string err2;
                                    if (Store.MoveTo(np, out err2))
                                    {
                                        if (_cfg == null) _cfg = Store.LoadConfig();
                                        Store.SaveConfig(_cfg);
                                        Reply(id, "\"path\":" + Q(np));
                                    }
                                    else Reply(id, "\"err\":" + Q(err2));
                                }
                                else Reply(id, "\"path\":\"\"");
                            }
                        }
                        finally { _suppressHide = false; }
                    }
                    break;

                case "openDataDir":
                    {
                        _suppressHide = true;
                        try
                        {
                            string dir = Path.GetDirectoryName(Store.TasksPath);
                            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                                System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\"");
                        }
                        catch { }
                        finally { _suppressHide = false; }
                        Reply(id, "\"ok\":true");
                    }
                    break;

                case "exportBackup":
                    {
                        _suppressHide = true;
                        try
                        {
                            using (SaveFileDialog d = new SaveFileDialog())
                            {
                                d.Filter = "JSON 文件 (*.json)|*.json";
                                d.FileName = "tasks-backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json";
                                if (d.ShowDialog(this) == DialogResult.OK)
                                {
                                    try
                                    {
                                        File.WriteAllText(d.FileName, Store.LoadTasks(), new UTF8Encoding(false));
                                        Reply(id, "\"path\":" + Q(d.FileName));
                                    }
                                    catch (Exception ex3) { Reply(id, "\"err\":" + Q(ex3.Message)); }
                                }
                                else Reply(id, "\"path\":\"\"");
                            }
                        }
                        finally { _suppressHide = false; }
                    }
                    break;

                case "importBackup":
                    {
                        _suppressHide = true;
                        try
                        {
                            using (OpenFileDialog d = new OpenFileDialog())
                            {
                                d.Filter = "JSON 文件 (*.json)|*.json";
                                if (d.ShowDialog(this) == DialogResult.OK)
                                {
                                    try
                                    {
                                        string txt = File.ReadAllText(d.FileName, new UTF8Encoding(false));
                                        JS.Deserialize<object>(txt);      // 校验是合法 JSON
                                        if (!txt.TrimStart().StartsWith("[")) throw new Exception("不是任务文件格式");
                                        if (!Store.ValidTasks(txt)) throw new Exception("备份包含无效任务");
                                        Store.BackupBeforeImport();
                                        if (!Store.SaveTasks(txt)) throw new Exception("任务未能写入，请检查存储目录权限");
                                        Reply(id, "\"ok\":true");
                                    }
                                    catch (Exception ex4) { Reply(id, "\"err\":" + Q("文件格式不对：" + ex4.Message)); }
                                }
                                else Reply(id, "\"ok\":false");
                            }
                        }
                        finally { _suppressHide = false; }
                    }
                    break;

                case "makeShortcut":
                    {
                        string p = Store.CreateShortcut();
                        Reply(id, p != null ? ("\"path\":" + Q(p)) : "\"err\":\"创建失败\"");
                    }
                    break;

                case "quit":
                    Reply(id, "\"ok\":true");
                    SaveAnchor();
                    Application.Exit();
                    break;

                case "aiRun":
                    StartAi(id, m);
                    break;

                case "aiCancel":
                    if (_job != null) _job.Cancel();
                    _job = null;
                    Reply(id, "\"ok\":true");
                    break;

                default:
                    Reply(id, "\"ok\":true");
                    break;
            }
        }

        private static string AppVersion
        {
            get
            {
                try { return Assembly.GetExecutingAssembly().GetName().Version.ToString(); }
                catch { return "1.0.0"; }
            }
        }

        private static bool IsOnScreen(Point p)
        {
            foreach (Screen screen in Screen.AllScreens)
                if (screen.WorkingArea.Contains(p)) return true;
            return false;
        }

        private Rectangle CurrentWorkArea { get { return Screen.FromPoint(_anchor).WorkingArea; } }

        // ---------------- AI ----------------
        private void StartAi(int id, Dictionary<string, object> m)
        {
            if (_job != null) { _job.Cancel(); _job = null; }

            string text = MS(m, "text");
            List<string> imgs = new List<string>();
            if (m.ContainsKey("images"))
            {
                System.Collections.IList arr = m["images"] as System.Collections.IList;
                if (arr != null)
                {
                    for (int i = 0; i < arr.Count && i < 3; i++)
                        if (arr[i] != null) imgs.Add(arr[i].ToString());
                }
            }

            bool useImg = imgs.Count > 0;
            bool separate = CB("imgSeparate", false);

            AiJob job = new AiJob();
            if (useImg && separate)
            {
                job.BaseUrl = CS("imgBaseUrl", CS("baseUrl", ""));
                job.ApiKey = CS("imgApiKey", CS("apiKey", ""));
                job.Model = CS("imgModel", CS("model", ""));
                job.Endpoint = CS("endpoint", "/chat/completions");
            }
            else
            {
                job.BaseUrl = CS("baseUrl", "");
                job.ApiKey = CS("apiKey", "");
                job.Model = CS("model", "");
                job.Endpoint = CS("endpoint", "/chat/completions");
            }
            job.Prompt = CB("customPrompt", false) ? CS("prompt", AiJob.DefaultPrompt) : AiJob.DefaultPrompt;
            job.Text = text;
            job.Images = imgs;

            job.OnDelta = delegate(string s)
            {
                BeginInvoke(new Action(delegate { if (_job == job) Emit("{\"evt\":\"aiDelta\",\"text\":" + Q(s) + "}"); }));
            };
            job.OnError = delegate(string msg)
            {
                BeginInvoke(new Action(delegate { if (_job != job) return; _job = null; Emit("{\"evt\":\"aiError\",\"message\":" + Q(msg) + "}"); }));
            };
            job.OnDone = delegate(List<AiItem> items, string raw)
            {
                BeginInvoke(new Action(delegate
                {
                    if (_job != job) return;
                    _job = null;
                    StringBuilder sb = new StringBuilder();
                    sb.Append("{\"evt\":\"aiDone\",\"items\":[");
                    for (int i = 0; i < items.Count; i++)
                    {
                        if (i > 0) sb.Append(",");
                        sb.Append("{\"title\":").Append(Q(items[i].title))
                          .Append(",\"date\":").Append(Q(items[i].date))
                          .Append(",\"period\":").Append(Q(items[i].period))
                          .Append(",\"due\":").Append(Q(items[i].due))
                          .Append(",\"pri\":").Append(Q(items[i].pri))
                          .Append(",\"quadrant\":").Append(Q(items[i].quadrant)).Append("}");
                    }
                    sb.Append("]}");
                    Emit(sb.ToString());
                }));
            };

            _job = job;
            job.Start();
            Reply(id, "\"ok\":true");
        }

        // ---------------- 拖动（面板标题栏） ----------------
        private void DoDragWindow()
        {
            if (_sliding) FinishSlideOpen();
            _suppressHide = true;
            _dragging = true;
            try
            {
                Native.DragWindow(this.Handle);
            }
            finally
            {
                _dragging = false;
                _suppressHide = false;
            }
            _lastActive = Now;
            // Dragging selects the monitor/horizontal anchor; always return to its top edge.
            if (_expanded)
            {
                _anchor = new Point(this.Left + (int)_anchorOffset.X, this.Top + (int)_anchorOffset.Y);
                ClampAnchor();
                ApplyBounds(PanelRectFromDock("top"), _panelRadius);
                _hoverHandleBounds = DockRect(out _dockSide);
                SaveAnchor();
            }
        }

        private void SaveAnchor()
        {
            try
            {
                if (_cfg == null) _cfg = Store.LoadConfig();
                _cfg["ballX"] = _anchor.X.ToString();
                _cfg["ballY"] = _anchor.Y.ToString();
                Store.SaveConfig(_cfg);
            }
            catch { }
        }

        private void ClampAnchor()
        {
            Rectangle wa = CurrentWorkArea;
            int x = Math.Max(wa.Left + _ballSize / 2, Math.Min(_anchor.X, wa.Right - _ballSize / 2));
            int y = Math.Max(wa.Top + _ballSize / 2, Math.Min(_anchor.Y, wa.Bottom - _ballSize / 2));
            _anchor = new Point(x, y);
        }

        // ============================================================
        //  贴边一小截的鼠标操作（v1.7：这是唯一的「收起」形态）
        // ============================================================
        private void HandleBallPress()
        {
            // 贴边态有一大半窗口在屏幕外，但没必要先「归位」再拖 ——
            // 窗口本身能跟着鼠标走，露出那一截在拖动中始终在光标下面。
            // （v1.7 之前要先弹回完整悬浮球，那是因为球的圆心才是锚点；
            //   现在没有球了，直接在原几何上拖更跟手。）
            Point before = this.Location;
            _dragging = true;
            _suppressHide = true;
            try { Native.DragWindow(this.Handle); }
            catch { }
            finally { _dragging = false; _suppressHide = false; }

            _lastActive = Now;
            int moved = Math.Abs(this.Left - before.X) + Math.Abs(this.Top - before.Y);

            if (moved > (int)(CLICK_SLOP * _scale))
            {
                // 拖动结束：更新显示器和横向锚点，再返回顶部。
                // 取拖完之后的窗口中心当新锚点（贴边时中心其实在屏幕外，
                // 但 DockRect 只用它判断「离哪条边近」，在屏幕外正是我们想要的信息）。
                _anchor = new Point(this.Left + this.Width / 2, this.Top + this.Height / 2);
                ClampAnchor();
                Redock();                     // 就近重新贴边 + 存锚点
            }
        }

        // ============================================================
        //  逐像素透明的球（稳态专用）
        // ============================================================
        private void RepaintBall()
        {
            RepaintBall(BallAlpha());
        }

        /// <summary>
        /// 按指定不透明度重画一次球。
        ///
        /// ⚠ 逐像素模式下【绝对不能】再去调 SetLayeredWindowAttributes ——
        ///   它一旦被调用，UpdateLayeredWindow 就彻底失效（永远返回 0），而且是
        ///   不可逆的（除非把 WS_EX_LAYERED 摘掉重装）。失效之后会同时长出两个
        ///   毛病，正好就是用户报的那两个：
        ///     · 窗口不再按位图画，退回「自己那一块底」→ 球看着就是个方块；
        ///     · 那张旧位图还粘在窗口表面上，窗口尺寸一变就被 DWM 拉伸，
        ///       于是面板底色里透出一个直径 = 球直径的淡色圆（实测圆心落在
        ///       两个窗口尺寸的正中，就是被拉伸的结果）。
        ///   所以画失败要能自愈：重新装一次样式位再画；还不行就整体退回网页渲染。
        /// </summary>
        private void RepaintBall(int alpha)
        {
            if (!_pixelBall) return;

            int n = this.Width < this.Height ? this.Width : this.Height;
            bool ok = false;
            try { ok = Native.PaintBall(this.Handle, n, _docked, _dockSide, _badge, alpha); }
            catch { ok = false; }

            if (!ok)
            {
                try
                {
                    Native.SetLayeredMode(this.Handle, true);
                    ok = Native.PaintBall(this.Handle, n, _docked, _dockSide, _badge, alpha);
                }
                catch { ok = false; }
            }

            if (ok) { _pixAlpha = alpha; _pixN = n; return; }

            FallbackToWebBall();
        }

        /// <summary>
        /// 把窗口表面擦干净（退出逐像素模式 / 回退到网页渲染时都要走一遍）。
        ///
        /// ⚠ 必须先重新装一次逐像素模式：中途只要调过一次
        ///   SetLayeredWindowAttributes，UpdateLayeredWindow 就会失败（返回 0），
        ///   这次擦除等于什么都没做，球的位图会留在面板底下透出来（见 Native.cs 经验四）。
        /// </summary>
        private void ClearLayeredSafe()
        {
            Native.SetLayeredMode(this.Handle, true);
            if (Native.ClearLayered(this.Handle, this.Width, this.Height)) return;

            Native.SetLayeredMode(this.Handle, true);          // 再装一次重擦
            if (Native.ClearLayered(this.Handle, this.Width, this.Height)) return;

            // 连擦两次都没成，只能硬盖：拿 DC 把客户区刷黑。
            // 刷黑和「擦成全透明」在 SLWA 模式下看到的是一回事（透明位图的 RGB 就是 0）。
            Native.FillClient(this.Handle, Color.Black);
        }

        /// <summary>
        /// 逐像素绘制失败时的统一退路：回到「Region + 网页渲染」的老方案。
        /// 宁可圆边有点台阶，也绝不能停在「球变成一个方块」或者「球不见了」上。
        /// </summary>
        private void FallbackToWebBall()
        {
            _pixelBall = false;
            _pixAlpha = -1;
            _pixN = -1;
            try { _wvHide.Stop(); } catch { }
            try { this.Cursor = Cursors.Default; } catch { }
            ClearLayeredSafe();                                 // 别把旧位图留在面板底下
            try { Native.SetLayeredMode(this.Handle, false); } catch { }
            Native.ApplyRegion(this, this.Width, this.Height, _ballSize, ref _region);
            Native.SetAlpha(this.Handle, _expanded ? 255 : BallAlpha());
            Emit("{\"evt\":\"ballhost\",\"on\":false}");
            try { _wv.Visible = true; } catch { }
        }

        /// <summary>
        /// 进入「自己画球」模式。顺序很讲究：先切模式、去掉 Region，试画一次，
        /// **画成功了才敢把 WebView2 藏起来** —— 万一 GDI+ 出问题，界面还在。
        /// </summary>
        private void EnterPixelBall()
        {
            if (_pixelBall || !_ready) return;
            if (_expanded) return;
            if (this.Width < 8 || this.Height < 8) return;

            Native.SetLayeredMode(this.Handle, true);
            Native.ClearRegion(this, ref _region);

            bool ok = false;
            try
            {
                int n = this.Width < this.Height ? this.Width : this.Height;
                ok = Native.PaintBall(this.Handle, n, _docked, _dockSide, _badge, BallAlpha());
            }
            catch { ok = false; }

            if (!ok)
            {
                FallbackToWebBall();
                return;
            }

            _pixelBall = true;
            _pixAlpha = BallAlpha();
            _pixN = this.Width < this.Height ? this.Width : this.Height;
            try { this.Cursor = Cursors.Hand; } catch { }

            // 先让网页把球收起来（球改由宿主画），画完一帧再藏 WebView2。
            Emit("{\"evt\":\"ballhost\",\"on\":true}");
            _wvHide.Stop();
            _wvHide.Start();
        }

        /// <summary>延后隐藏 WebView2：等网页把球收起来并画完一帧。</summary>
        private void WvHideTick(object sender, EventArgs e)
        {
            _wvHide.Stop();
            if (!_pixelBall) return;                // 这中间已经退出逐像素模式了，别乱藏
            if (_wv == null || _wv.CoreWebView2 == null) return;
            try { _wv.Visible = false; } catch { }
        }

        /// <summary>退出「自己画球」模式，交回给网页渲染。</summary>
        private void ExitPixelBall()
        {
            if (!_pixelBall) return;
            _pixelBall = false;
            _pixAlpha = -1;
            _pixN = -1;
            try { _wvHide.Stop(); } catch { }        // 别让待命中的那次隐藏把网页又藏了
            try { this.Cursor = Cursors.Default; } catch { }

            // ① 先把分层表面擦成完全透明。
            //    ⚠ 顺序不能反：只要之前调过一次 SetLayeredWindowAttributes，
            //    UpdateLayeredWindow 就会失败（返回 0），ClearLayered 也就白调了，
            //    球的位置图会一直粘在窗口表面上。窗口随即被拉大到面板尺寸，
            //    那张旧位图跟着被拉伸 —— 面板左上角就是这么冒出一个淡色圆的。
            //    所以 ClearLayeredSafe 会先重新装一次「逐像素」模式，保证 ULW 一定生效。
            ClearLayeredSafe();

            // ② 切回整体 Alpha 模式（此后窗口内容 = 上面那张全透明位图的 RGB，
            //    也就是纯黑。网页 #panel 是 92% 白，盖上去 8% 透出这层黑，
            //    于是面板底色是一块浅灰 —— 这就是现在的观感，别乱改。）
            Native.SetLayeredMode(this.Handle, false);

            // ③ 先按「贴边一小截」的几何装一次 Region 和 Alpha —— 这只是过渡，
            //    紧跟着调用方会用 ApplyBounds(面板几何, _panelRadius) 覆盖掉。
            //    留这一步是为了「只退出逐像素模式、几何不变」的调用场景（如
            //    FallbackToWebBall 之后），不至于出现一刻无 Region 的裸窗口。
            int rr = (this.Width <= _ballSize + 2) ? _ballSize : _panelRadius;
            Native.ApplyRegion(this, this.Width, this.Height, rr, ref _region);
            Native.SetAlpha(this.Handle, _expanded ? 255 : BallAlpha());

            // ④ 交回网页渲染
            Emit("{\"evt\":\"ballhost\",\"on\":false}");
            try { _wv.Visible = true; } catch { }
        }

        /// <summary>
        /// 逐像素模式下网页收不到鼠标事件，按下/右键都得宿主自己处理。
        /// 面板态与动画中不接管（那时候 WebView2 是显示的，事件归网页）。
        /// </summary>
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!_pixelBall) return;
            if (e.Button == MouseButtons.Left)
            {
                _lastActive = Now;
                HandleBallPress();
            }
            else if (e.Button == MouseButtons.Right)
            {
                ShowBallMenu();
            }
        }

        /// <summary>逐像素模式下的右键菜单（网页里的 HTML 菜单这时候是看不见的）。</summary>
        private void ShowBallMenu()
        {
            try
            {
                if (_ballMenu == null)
                {
                    ContextMenuStrip m = new ContextMenuStrip();
                    m.ShowImageMargin = false;
                    m.Font = new Font("Microsoft YaHei UI", 9f);
                    AddItem(m, "打开待办", delegate { Expand(); });
                    AddItem(m, "AI 智能添加", delegate { Nav("ai"); });
                    m.Items.Add(new ToolStripSeparator());
                    AddItem(m, "设置", delegate { Nav("set"); });
                    AddItem(m, "打开数据文件夹", delegate { OpenDataDir(); });
                    m.Items.Add(new ToolStripSeparator());
                    AddItem(m, "退出程序", delegate { Nav("askquit"); });
                    m.Closed += delegate { _menuOpen = false; _lastActive = Now; };
                    _ballMenu = m;
                }
                _menuOpen = true;
                _lastActive = Now;
                _ballMenu.Show(Cursor.Position);
            }
            catch { _menuOpen = false; }
        }

        private static void AddItem(ContextMenuStrip m, string text, EventHandler onClick)
        {
            ToolStripMenuItem it = new ToolStripMenuItem(text);
            it.Click += onClick;
            m.Items.Add(it);
        }

        /// <summary>先展开面板，再把网页导航到某个视图（菜单里的 AI / 设置 / 退出走这条路）。</summary>
        private void Nav(string to)
        {
            Expand();
            Timer t = new Timer();
            t.Interval = 320;
            t.Tick += delegate(object o, EventArgs a)
            {
                t.Stop();
                t.Dispose();
                Emit("{\"evt\":\"nav\",\"to\":" + Q(to) + "}");
            };
            t.Start();
        }

        private void OpenDataDir()
        {
            _suppressHide = true;
            try
            {
                string dir = Path.GetDirectoryName(Store.TasksPath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\"");
            }
            catch { }
            finally { _suppressHide = false; }
        }

        // ---------------- 布局 ----------------
        /// <summary>
        /// 把窗口摆到贴边位置（v1.7：启动/配置变更后都直接落到贴边态）。
        /// 参数只是为了保持调用点不变，已经没有动画分支了。
        /// </summary>
        private void ApplyBallBounds(bool animate)
        {
            Rectangle r = DockRect(out _dockSide);
            _docked = true;
            _expanded = false;
            ApplyBounds(r, _ballSize);
            EnterPixelBall();
            if (_pixelBall) RepaintBall();
        }

        private Rectangle BallRect()
        {
            Rectangle wa = CurrentWorkArea;
            int x = _anchor.X - _ballSize / 2;
            int y = _anchor.Y - _ballSize / 2;
            x = Math.Max(wa.Left, Math.Min(x, wa.Right - _ballSize));
            y = Math.Max(wa.Top, Math.Min(y, wa.Bottom - _ballSize));
            return new Rectangle(x, y, _ballSize, _ballSize);
        }

        /// <summary>顶部收起位置：只露出透明把手的下方区域。</summary>
        private Rectangle DockRect(out string side)
        {
            side = "top";
            Rectangle area = CurrentWorkArea;
            return TopPanelMotion.Handle(area,
                TopPanelMotion.RightAnchor(area, _ballSize, (int)Math.Round(24 * _scale)), _ballSize);
        }

        private static int ClampInt(int v, int lo, int hi)
        {
            if (hi < lo) return lo;
            return v < lo ? lo : (v > hi ? hi : v);
        }

        // ⚠ v1.7 删除：CursorNearDocked / Undock / PanelRect(out offset)
        //   · 贴边态不再「鼠标靠近就弹回完整悬浮球」（球这一态已删）——
        //     现在检测整个把手矩形（含透明像素）悬停，直接展开工作区。
        //   · 面板位置改由 PanelRectFromDock(side) 按「贴的那一边」算。
        //   · 面板拖动后保持横向位置并返回顶部（见 DoDragWindow）。

        private void ApplyBounds(Rectangle r, int radius)
        {
            this.SetBounds(r.X, r.Y, r.Width, r.Height);
            if (_pixelBall)
            {
                // 逐像素模式下窗口形状由位图的 Alpha 通道决定，不能再用 Region 切，
                // 否则又把抗锯齿的圆边切成硬边
                Native.ClearRegion(this, ref _region);
                // 尺寸一变，粘在窗口表面的那张位图会被拉伸（拉伸出来的圆边很糊，
                // 还会从网页 8% 的透明里透出来），所以必须按新尺寸重画
                int n = r.Width < r.Height ? r.Width : r.Height;
                if (n != _pixN) RepaintBall(_pixAlpha >= 0 ? _pixAlpha : BallAlpha());
                return;
            }
            Native.ApplyRegion(this, r.Width, r.Height, radius, ref _region);
        }

        /// <summary>已有实例被再次启动时，把面板弹出来。</summary>
        public void ShowPanel()
        {
            try
            {
                this.Visible = true;
                this.TopMost = CB("topmost", true);
            }
            catch { }
            Expand();
            try { this.Activate(); } catch { }
            try { this.BringToFront(); } catch { }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) Collapse();
            base.OnKeyDown(e);
        }

        // Top-edge motion reveals a fixed-size WebView. ULW handle rendering and
        // panel alpha rendering switch only once at each transition boundary.
        private void Expand()
        {
            if (!_ready) return;
            if (_sliding) { if (!_slideOpening) BeginSlide(true, false); return; }
            if (_expanded) return;
            _hoverHandleBounds = DockRect(out _dockSide);
            _expanded = true;
            _docked = false;
            _dockSide = "";
            _lastActive = Now;
            // Switch ULW/SLWA once, outside the animation; never scale the native bitmap.
            ExitPixelBall();
            Rectangle panel = PanelRectFromDock("top");
            _wv.Dock = DockStyle.None;
            _wv.SetBounds(0, -panel.Height, panel.Width, panel.Height);
            ApplyBounds(panel, _panelRadius);
            Native.SetAlpha(this.Handle, 255);
            _slideVisible = 0;
            EmitMode();
            BeginSlide(true, false);
            try { this.Activate(); this.BringToFront(); } catch { }
        }

        private void Collapse() { Collapse(false); }
        private void Collapse(bool automatic)
        {
            if (!_expanded || (_sliding && !_slideOpening)) return;
            BeginSlide(false, automatic);
        }

        private void BeginSlide(bool opening, bool automatic)
        {
            _slide.Stop();
            if (!_sliding) _slideVisible = opening ? 0 : this.Height;
            _sliding = true;
            _slideOpening = opening;
            _slideAutomatic = automatic;
            _slideFrom = _slideVisible;
            _slideTo = opening ? this.Height : 0;
            _slideStarted = Now;
            _slideDuration = Math.Max(90, (opening ? 280.0 : 240.0) * Math.Abs(_slideTo - _slideFrom) / Math.Max(1, this.Height));
            _wv.Dock = DockStyle.None;
            // Constant viewport size throughout: only content position and the reveal region change.
            _wv.Size = this.ClientSize;
            SetSlideFrame(_slideVisible);
            _slide.Start();
        }

        private void SetSlideFrame(int visible)
        {
            _slideVisible = Math.Max(0, Math.Min(this.Height, visible));
            _wv.Location = new Point(0, _slideVisible - this.Height);
            if (_slideVisible > 0)
                Native.ApplyRegion(this, this.Width, _slideVisible, _panelRadius, ref _region);
            else
            {
                Region old = _region;
                _region = new Region(Rectangle.Empty);
                this.Region = _region;
                if (old != null) old.Dispose();
            }
        }

        private void SlideTick(object sender, EventArgs e)
        {
            if (!_slideOpening && _slideAutomatic &&
                (_pinned || _interacting || _suppressHide || _job != null ||
                 new Rectangle(this.Left, this.Top, this.Width, _slideVisible).Contains(Cursor.Position) ||
                 _hoverHandleBounds.Contains(Cursor.Position)))
            { BeginSlide(true, false); return; }
            double t = (Now - _slideStarted) / _slideDuration;
            SetSlideFrame(TopPanelMotion.Reveal(_slideFrom, _slideTo, t));
            if (t < 1) return;
            if (_slideOpening) FinishSlideOpen();
            else FinishSlideClosed();
        }

        private void FinishSlideOpen()
        {
            _slide.Stop();
            _sliding = false;
            _wv.Location = Point.Empty;
            _wv.Dock = DockStyle.Fill;
            Native.ApplyRegion(this, this.Width, this.Height, _panelRadius, ref _region);
            _slideVisible = this.Height;
            _lastActive = Now;
        }

        private void FinishSlideClosed()
        {
            _slide.Stop();
            _sliding = false;
            _wv.Visible = false;
            _wv.Dock = DockStyle.Fill;
            _expanded = false;
            _lastActive = Now;
            Rectangle handle = DockRect(out _dockSide);
            _docked = true;
            ApplyBounds(handle, _ballSize);
            _edgeHover.Collapsed(this.Bounds, Cursor.Position);
            EnterPixelBall();
            if (_pixelBall) RepaintBall();
            EmitMode();
        }

        /// <summary>
        /// 把窗口摆到贴边位置（无动画）。已经在贴边态时不做任何事。
        /// </summary>
        private void DockNow()
        {
            if (!_ready || _expanded || _docked || _dragging) return;

            Rectangle r = DockRect(out _dockSide);
            _docked = true;
            ApplyBounds(r, _ballSize);
            EnterPixelBall();
            if (_pixelBall) RepaintBall();

            EmitMode();
        }

        /// <summary>拖动结束后返回所选显示器顶部，保留横向位置。</summary>
        private void Redock()
        {
            if (!_ready || _expanded) return;

            Rectangle r = DockRect(out _dockSide);
            _docked = true;
            ApplyBounds(r, _ballSize);
            EnterPixelBall();
            if (_pixelBall) RepaintBall();

            EmitMode();
            SaveAnchor();
        }

        /// <summary>
        /// Expanded bounds stay flush with the current monitor working-area top.
        /// Clamp width/height and horizontal anchor to that monitor.
        /// </summary>
        private Rectangle PanelRectFromDock(string side)
        {
            Rectangle wa = CurrentWorkArea;
            bool compact = CS("layout", "workbench") == "compact";
            int anchorX = TopPanelMotion.RightAnchor(wa, _ballSize, (int)Math.Round(24 * _scale));
            Rectangle panel = TopPanelMotion.Panel(wa, anchorX,
                (int)Math.Round((compact ? 480 : PANEL_W) * _scale),
                (int)Math.Round((compact ? 680 : PANEL_H) * _scale));
            _panelW = panel.Width;
            _panelH = panel.Height;
            _anchorOffset = new PointF(anchorX - panel.Left, Math.Min(_ballSize / 2f, panel.Height / 2f));
            return panel;
        }

        // ---------------- 鼠标守望 ----------------
        private static long Now
        {
            get { return _uptime.ElapsedMilliseconds; }
        }
        private static readonly System.Diagnostics.Stopwatch _uptime = System.Diagnostics.Stopwatch.StartNew();

        private void WatchTick(object sender, EventArgs e)
        {
            if (!_ready || _dragging) return;

            Point p = Cursor.Position;
            if (_sliding)
            {
                if (!_slideOpening && _slideAutomatic &&
                    (new Rectangle(this.Left, this.Top, this.Width, _slideVisible).Contains(p) ||
                     _hoverHandleBounds.Contains(p) || _pinned || _interacting || _suppressHide || _job != null))
                    BeginSlide(true, false);
                return;
            }
            bool inside = _expanded
                ? EdgeHoverState.InWorkspace(this.Bounds, _hoverHandleBounds, p)
                : this.Bounds.Contains(p);

            if (_menuOpen)
            {
                // 右键菜单开着的时候鼠标在菜单上（窗口外），别误触发
                _lastActive = Now;
                return;
            }

            // 包括透明像素在内的整个把手窗口都是悬停区域，不依赖点击或命中测试。
            if (_docked)
            {
                _lastActive = Now;
                if (_edgeHover.ShouldExpand(this.Bounds, p,
                    !_suppressHide && Control.MouseButtons == MouseButtons.None)) Expand();
                return;
            }

            if (inside) _lastActive = Now;

            if (_expanded)
            {
                if (_pinned || _suppressHide || _interacting) { _lastActive = Now; return; }
                if (_job != null) { _lastActive = Now; return; }   // AI 生成中不收
                if (inside) return;
                int delay;
                if (!int.TryParse(CS("idleDelay", "1000"), out delay)) delay = IDLE_MS;
                if (delay > 0 && Now - _lastActive > Math.Max(1000, Math.Min(60000, delay))) Collapse(true);
                return;
            }

            // v1.7 起不该再有「既没展开也没贴边」的中间态；真出现了（比如刚
            // 启动还没摆好位置）就补一次吸附，别让窗口停在屏幕中间。
            if (Now - _lastActive > IDLE_MS) DockNow();
        }

        // 手动收起时若鼠标仍在把手内，先移出再移入才展开，避免立即弹回。

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_hotkeyRegistered) { UnregisterHotKey(this.Handle, 0x5448); _hotkeyRegistered = false; }
                if (_desktop != null) { _desktop.Dispose(); _desktop = null; }
                Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;
                _watch.Stop(); _wvHide.Stop(); _slide.Stop();
                _watch.Dispose(); _wvHide.Dispose(); _slide.Dispose();
                if (_region != null) { try { _region.Dispose(); } catch { } }
                if (_ballMenu != null) { try { _ballMenu.Dispose(); } catch { } }
            }
            base.Dispose(disposing);
        }
    }
}
