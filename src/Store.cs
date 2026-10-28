// ============================================================
//  数据层：任务与配置的读写、API Key 加密、开机自启、备份
// ============================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace TaskHarbor
{
    internal static class Store
    {
        private static string _appDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TaskHarbor");
        public static string AppDir { get { return _appDir; } }
        public static string MigrationNote = "";
        public static void Initialize(string dataDir)
        {
            _tasksPath = null;
            if (!string.IsNullOrEmpty(dataDir)) { _appDir = Path.GetFullPath(dataDir); EnsureDir(); return; }
            EnsureDir();
            MigrateLegacy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeskTodo"));
        }

        internal static void MigrateLegacy(string legacy)
        {
            string marker = Path.Combine(AppDir, "migration-v3.complete");
            if (File.Exists(marker) || File.Exists(ConfigPath) || File.Exists(DefaultTasksPath)) return;
            string legacyConfig = Path.Combine(legacy, "config.json"), legacyTasks = Path.Combine(legacy, "tasks.json");
            var config = new Dictionary<string, object>();
            if (File.Exists(legacyConfig)) config = _js.Deserialize<Dictionary<string, object>>(File.ReadAllText(legacyConfig, Encoding.UTF8));
            if (config == null) throw new IOException("旧版配置格式无效，未修改原文件");
            string legacyDataPath = config.ContainsKey("dataPath") ? Str(config["dataPath"]) : legacyTasks;
            bool custom = !string.IsNullOrEmpty(legacyDataPath) && !string.Equals(Path.GetFullPath(legacyDataPath), Path.GetFullPath(legacyTasks), StringComparison.OrdinalIgnoreCase);
            string taskData = null;
            if (!custom && File.Exists(legacyTasks))
            {
                taskData = File.ReadAllText(legacyTasks, Encoding.UTF8);
                if (!ValidTasks(taskData)) throw new IOException("旧版任务格式无效，未修改原文件");
                config["dataPath"] = DefaultTasksPath;
            }
            bool tasksWritten = false, configWritten = false;
            try {
                if (taskData != null) { if (!WriteText(DefaultTasksPath, taskData)) throw new IOException("无法复制旧版任务"); tasksWritten = true; }
                if (File.Exists(legacyConfig)) { if (!WriteText(ConfigPath, _js.Serialize(config))) throw new IOException("无法迁移旧版配置"); configWritten = true; }
            } catch {
                if (tasksWritten) File.Delete(DefaultTasksPath);
                if (configWritten) File.Delete(ConfigPath);
                throw;
            }
            MigrationNote = File.Exists(legacyConfig) || File.Exists(legacyTasks) ? (custom ? "沿用旧版自定义任务路径，原配置保留。" : "已复制迁移旧版数据，DeskTodo 原文件保留。") : "这是全新的 TaskHarbor 本地数据目录。";
            WriteText(marker, MigrationNote);
        }

        public static string ConfigPath { get { return Path.Combine(AppDir, "config.json"); } }

        private static string _tasksPath = null;

        public static string DefaultTasksPath
        {
            get { return Path.Combine(AppDir, "tasks.json"); }
        }

        // ---------------- 基础读写 ----------------
        public static void EnsureDir()
        {
            try { if (!Directory.Exists(AppDir)) Directory.CreateDirectory(AppDir); }
            catch { }
        }

        public static string ReadText(string path)
        {
            try
            {
                if (!File.Exists(path)) return "";
                return File.ReadAllText(path, new UTF8Encoding(false));
            }
            catch { return ""; }
        }

        public static bool WriteText(string path, string text)
        {
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                // 先写临时文件再替换，避免写入中断把原数据写坏
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, text, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
                return true;
            }
            catch { return false; }
        }

        // ---------------- 任务 ----------------
        public static string TasksPath
        {
            get
            {
                if (!string.IsNullOrEmpty(_tasksPath)) return _tasksPath;
                return DefaultTasksPath;
            }
        }

        public static void SetTasksPath(string p)
        {
            if (!string.IsNullOrEmpty(p)) _tasksPath = Path.GetFullPath(p);
        }

        public static string LoadTasks()
        {
            if (!File.Exists(TasksPath)) return "[]";
            string s = File.ReadAllText(TasksPath, Encoding.UTF8);
            if (!ValidTasks(s)) throw new IOException("任务文件无法解析，请恢复备份；原文件未覆盖");
            return s;
        }

        public static bool ValidTasks(string json)
        {
            try {
                object[] items = _js.DeserializeObject(json) as object[];
                if (items == null) return false;
                foreach (object item in items) {
                    var task = item as Dictionary<string, object>;
                    if (task == null || !task.ContainsKey("title") || !(task["title"] is string)) return false;
                }
                return true;
            } catch { return false; }
        }

        public static void BackupBeforeImport()
        {
            if (!File.Exists(TasksPath)) return;
            string backupDir = Path.Combine(Path.GetDirectoryName(TasksPath), "backups");
            Directory.CreateDirectory(backupDir);
            File.Copy(TasksPath, Path.Combine(backupDir, "tasks-before-import-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".json"));
        }

        public static bool SaveTasks(string json)
        {
            if (string.IsNullOrEmpty(json)) json = "[]";
            try
            {
                object[] items = _js.DeserializeObject(json) as object[];
                if (items == null) return false;
                foreach (object item in items)
                {
                    var task = item as Dictionary<string, object>;
                    if (task == null || !task.ContainsKey("title") || !(task["title"] is string)) return false;
                }
                // 首次升级写入前保留原始数据，失败时不覆盖原文件。
                string backup = TasksPath + ".before-taskharbor-v3.bak";
                if (File.Exists(TasksPath) && !File.Exists(backup)) File.Copy(TasksPath, backup);
                return WriteText(TasksPath, json);
            }
            catch { return false; }
        }

        /// <summary>换存储位置时把已有任务搬过去。</summary>
        public static bool MoveTo(string newPath, out string err)
        {
            err = "";
            try
            {
                string data = LoadTasks();
                string dir = Path.GetDirectoryName(newPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(newPath, data, new UTF8Encoding(false));
                _tasksPath = newPath;
                return true;
            }
            catch (Exception ex)
            {
                err = ex.Message;
                return false;
            }
        }

        // ---------------- 配置 ----------------
        private static readonly JavaScriptSerializer _js = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };

        public static Dictionary<string, object> LoadConfig()
        {
            var cfg = new Dictionary<string, object>();
            string s = ReadText(ConfigPath);
            if (!string.IsNullOrEmpty(s))
            {
                try
                {
                    var d = _js.Deserialize<Dictionary<string, object>>(s);
                    if (d != null) cfg = d;
                }
                catch { }
            }

            // 解出明文 Key 交给界面；密文留在字典里等下一次保存
            if (cfg.ContainsKey("apiKeyEnc"))
            {
                string plain = Unprotect(Str(cfg["apiKeyEnc"]));
                if (plain != null) cfg["apiKey"] = plain;
            }
            if (cfg.ContainsKey("imgApiKeyEnc"))
            {
                string plain2 = Unprotect(Str(cfg["imgApiKeyEnc"]));
                if (plain2 != null) cfg["imgApiKey"] = plain2;
            }

            if (cfg.ContainsKey("dataPath")) SetTasksPath(Str(cfg["dataPath"]));
            return cfg;
        }

        public static bool SaveConfig(Dictionary<string, object> cfg)
        {
            if (cfg == null) cfg = new Dictionary<string, object>();
            // 只加密用于落盘的副本，保留宿主内存中的 Key。
            cfg = new Dictionary<string, object>(cfg);
            // 落盘前把明文 Key 换成密文
            string k = Str(cfg.ContainsKey("apiKey") ? cfg["apiKey"] : "");
            if (cfg.ContainsKey("apiKey")) { if (k.Length > 0) { string enc = Protect(k); if (enc.Length == 0) return false; cfg["apiKeyEnc"] = enc; } else cfg.Remove("apiKeyEnc"); }
            string ik = Str(cfg.ContainsKey("imgApiKey") ? cfg["imgApiKey"] : "");
            if (cfg.ContainsKey("imgApiKey")) { if (ik.Length > 0) { string enc = Protect(ik); if (enc.Length == 0) return false; cfg["imgApiKeyEnc"] = enc; } else cfg.Remove("imgApiKeyEnc"); }
            cfg.Remove("apiKey");
            cfg.Remove("imgApiKey");
            cfg["dataPath"] = TasksPath;

            try
            {
                return WriteText(ConfigPath, _js.Serialize(cfg));
            }
            catch { return false; }
        }

        public static string Str(object o)
        {
            if (o == null) return "";
            return o.ToString();
        }

        public static bool Bool(Dictionary<string, object> d, string key, bool def)
        {
            if (d == null || !d.ContainsKey(key)) return def;
            object v = d[key];
            if (v == null) return def;
            if (v is bool) return (bool)v;
            string s = v.ToString().ToLowerInvariant();
            return s == "true" || s == "1";
        }

        // ---------------- API Key 加密（仅当前用户可解） ----------------
        public static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            try
            {
                byte[] raw = Encoding.UTF8.GetBytes(plain);
                byte[] enc = ProtectedData.Protect(raw, null, DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(enc);
            }
            catch { return ""; }
        }

        public static string Unprotect(string cipher)
        {
            if (string.IsNullOrEmpty(cipher)) return null;
            try
            {
                byte[] enc = Convert.FromBase64String(cipher);
                byte[] raw = ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(raw);
            }
            catch { return null; }
        }

        // ---------------- 开机自启 ----------------
        private const string RUN_KEY = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RUN_NAME = "TaskHarbor";

        public static bool GetAutoStart()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RUN_KEY, false))
                {
                    if (k == null) return false;
                    object v = k.GetValue(RUN_NAME);
                    return v != null && v.ToString().Length > 0;
                }
            }
            catch { return false; }
        }

        public static bool SetAutoStart(bool on, out string err)
        {
            err = "";
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RUN_KEY, true))
                {
                    if (k == null) { err = "打不开启动项注册表"; return false; }
                    if (on)
                    {
                        string exe = System.Windows.Forms.Application.ExecutablePath;
                        k.SetValue(RUN_NAME, "\"" + exe + "\"");
                    }
                    else
                    {
                        k.DeleteValue(RUN_NAME, false);
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                err = ex.Message;
                return false;
            }
        }

        // ---------------- 快捷方式 ----------------
        public static string CreateShortcut()
        {
            try
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string lnk = Path.Combine(desktop, "TaskHarbor.lnk");
                string exe = System.Windows.Forms.Application.ExecutablePath;

                Type t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) return null;
                object shell = Activator.CreateInstance(t);
                object sc = t.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                Type st = sc.GetType();
                st.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { exe });
                st.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { Path.GetDirectoryName(exe) });
                st.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { "TaskHarbor — Personal task workspace" });
                st.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, sc, null);
                System.Runtime.InteropServices.Marshal.ReleaseComObject(sc);
                System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
                return lnk;
            }
            catch { return null; }
        }
    }
}
