using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace TaskHarbor
{
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
