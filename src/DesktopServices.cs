using System;
using System.Collections.Generic;
using System.Globalization;
using System.Drawing;
using System.IO;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace TaskHarbor
{
    internal static class TopPanelMotion
    {
        public static int RightAnchor(Rectangle area, int size, int inset)
        {
            size = Math.Max(1, Math.Min(size, Math.Min(area.Width, area.Height)));
            inset = Math.Max(0, Math.Min(inset, area.Width - size));
            return area.Right - inset - size + size / 2;
        }
        public static Rectangle Handle(Rectangle area, int anchorX, int size)
        {
            size = Math.Max(1, Math.Min(size, Math.Min(area.Width, area.Height)));
            int visible = Math.Max(1, (int)Math.Round(size * 0.42));
            int x = Math.Max(area.Left, Math.Min(anchorX - size / 2, area.Right - size));
            return new Rectangle(x, area.Top - size + visible, size, size);
        }
        public static Rectangle Panel(Rectangle area, int anchorX, int width, int height)
        {
            width = Math.Max(1, Math.Min(width, area.Width));
            height = Math.Max(1, Math.Min(height, area.Height));
            return new Rectangle(Math.Max(area.Left, Math.Min(anchorX - width / 2, area.Right - width)), area.Top, width, height);
        }
        public static int Reveal(int from, int to, double progress)
        {
            double t = Math.Max(0, Math.Min(1, progress));
            double eased = 1 - Math.Pow(1 - t, 3);
            return (int)Math.Round(from + (to - from) * eased);
        }
    }

    // Cursor polling includes alpha-zero pixels, which do not receive native mouse events.
    internal sealed class EdgeHoverState
    {
        private bool _armed = true;
        public bool ShouldExpand(Rectangle bounds, Point pointer, bool allowed)
        {
            if (!bounds.Contains(pointer)) { _armed = true; return false; }
            if (!allowed || !_armed) return false;
            _armed = false;
            return true;
        }
        public void Collapsed(Rectangle bounds, Point pointer) { _armed = !bounds.Contains(pointer); }
        public static bool InWorkspace(Rectangle panel, Rectangle handle, Point pointer)
        {
            return (handle.IsEmpty ? panel : Rectangle.Union(panel, handle)).Contains(pointer);
        }
    }

    // The host timer works while the WebView is hidden. No task titles in system notifications.
    internal sealed class DesktopServices : IDisposable
    {
        private readonly NotifyIcon _tray;
        private readonly Timer _timer;
        private readonly Action<string> _openTask;
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
        private Dictionary<string, object> _state;
        private string _lastTask;
        private string StatePath { get { return Path.Combine(Store.AppDir, "reminder-state.json"); } }
        public DesktopServices(Action workbench, Action compact, Action quit, Action<string> openTask)
        {
            _openTask = openTask;
            try { _state = _json.Deserialize<Dictionary<string, object>>(Store.ReadText(StatePath)); } catch { }
            if (_state == null) _state = new Dictionary<string, object>();
            _tray = new NotifyIcon { Text = "TaskHarbor" };
            try { _tray.Icon = new System.Drawing.Icon(WebAssets.IconPath()); } catch { _tray.Icon = System.Drawing.SystemIcons.Application; }
            var menu = new ContextMenuStrip();
            menu.Items.Add("打开工作台", null, delegate { workbench(); });
            menu.Items.Add("打开小面板", null, delegate { compact(); });
            menu.Items.Add("暂停提醒 1 小时", null, delegate { _state["pauseUntil"] = DateTime.UtcNow.AddHours(1).ToString("o"); Store.WriteText(StatePath, _json.Serialize(_state)); });
            menu.Items.Add("恢复提醒", null, delegate { _state.Remove("pauseUntil"); Store.WriteText(StatePath, _json.Serialize(_state)); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出 TaskHarbor", null, delegate { quit(); });
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += delegate { workbench(); };
            _tray.BalloonTipClicked += delegate { if (!string.IsNullOrEmpty(_lastTask)) _openTask(_lastTask); };
            _tray.Visible = true;
            _timer = new Timer { Interval = 10000 }; _timer.Tick += Tick; _timer.Start();
        }
        internal static bool Due(Dictionary<string, object> t, DateTime now, out string token)
        {
            token = "";
            if (Store.Bool(t, "done", false) || !string.IsNullOrEmpty(Value(t, "deletedAt")) || !Store.Bool(t, "remind", false)) return false;
            string id = Value(t, "id"), date = Value(t, "date"), time = Value(t, "due");
            if (id.Length == 0 || date.Length == 0) return false;
            if (time.Length == 0) time = "09:00";
            DateTime scheduled;
            if (!DateTime.TryParseExact(date + " " + time, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out scheduled)) return false;
            string snooze = Value(t, "snoozeUntil");
            if (snooze.Length > 0) { DateTime postponed; if (DateTime.TryParse(snooze, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out postponed)) scheduled = postponed.ToLocalTime(); }
            token = id + "|" + date + "|" + time + "|" + snooze;
            return scheduled <= now && scheduled >= now.AddHours(-24);
        }
        private static string Value(Dictionary<string, object> t, string k) { return t.ContainsKey(k) && t[k] != null ? t[k].ToString() : ""; }
        private void Tick(object sender, EventArgs e)
        {
            if (Program.DemoMode) return;
            try
            {
                var cfg = Store.LoadConfig();
                if (!Store.Bool(cfg, "remindersEnabled", true)) return;
                DateTime now = DateTime.Now, paused;
                if (_state.ContainsKey("pauseUntil") && DateTime.TryParse(_state["pauseUntil"].ToString(), out paused) && paused.ToUniversalTime() > DateTime.UtcNow) return;
                if (Store.Bool(cfg, "quietHours", true) && (now.Hour >= 22 || now.Hour < 8)) return;
                var tasks = _json.DeserializeObject(Store.LoadTasks()) as object[];
                if (tasks == null) return;
                var tokens = new List<string>(); string first = "";
                foreach (object raw in tasks)
                {
                    var t = raw as Dictionary<string, object>; string token;
                    if (t == null || !Due(t, now, out token) || _state.ContainsKey(token)) continue;
                    if (first.Length == 0) first = Value(t, "id");
                    tokens.Add(token);
                }
                if (tokens.Count == 0) return;
                _lastTask = first;
                _tray.ShowBalloonTip(6000, "TaskHarbor · 任务提醒", tokens.Count + " 项任务已到提醒时间。点击打开；可在任务中延后 15 分钟。", ToolTipIcon.Info);
                foreach (string token in tokens) _state[token] = DateTime.UtcNow.ToString("o");
                Store.WriteText(StatePath, _json.Serialize(_state));
            }
            catch { /* Never overwrite a failed/corrupt task file from a reminder tick. */ }
        }
        public void Dispose() { _timer.Stop(); _timer.Dispose(); _tray.Visible = false; if (_tray.ContextMenuStrip != null) _tray.ContextMenuStrip.Dispose(); _tray.Dispose(); }
    }
}
