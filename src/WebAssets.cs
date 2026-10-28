// ============================================================
//  把内嵌的网页资源释放到磁盘（WebView2 需要一个真实目录来映射）
// ============================================================
using System;
using System.IO;
using System.Reflection;
using System.Text;

namespace TaskHarbor
{
    internal static class WebAssets
    {
        public static string Ver
        {
            get
            {
                try { return Assembly.GetExecutingAssembly().GetName().Version.ToString(); }
                catch { return "1.0.0.0"; }
            }
        }

        public static string Ensure()
        {
            string dir = Path.Combine(Store.AppDir, "web-" + Ver);
            try { if (!Directory.Exists(dir)) Directory.CreateDirectory(dir); }
            catch { }

            Write(dir, "panel.html", "TaskHarbor.web.panel.html");
            Write(dir, "panel.css", "TaskHarbor.web.panel.css");
            Write(dir, "panel.js", "TaskHarbor.web.panel.js");
            Write(dir, "model.js", "TaskHarbor.web.model.js");
            using (Stream brand = Assembly.GetExecutingAssembly().GetManifestResourceStream("TaskHarbor.web.brand.png"))
            {
                if (brand != null) using (var file = File.Create(Path.Combine(dir, "brand.png"))) brand.CopyTo(file);
            }
            return dir;
        }

        private static void Write(string dir, string fileName, string resName)
        {
            try
            {
                string target = Path.Combine(dir, fileName);
                Assembly asm = Assembly.GetExecutingAssembly();
                Stream s = Find(asm, resName, fileName);
                if (s == null) return;

                string txt;
                using (s)
                using (StreamReader r = new StreamReader(s, new UTF8Encoding(false)))
                {
                    txt = r.ReadToEnd();
                }

                if (File.Exists(target))
                {
                    try
                    {
                        if (File.ReadAllText(target, new UTF8Encoding(false)) == txt) return;
                    }
                    catch { }
                }
                File.WriteAllText(target, txt, new UTF8Encoding(false));
            }
            catch { }
        }

        /// <summary>
        /// 把内嵌的圆形图标释放到磁盘并返回路径（失败返回 null）。
        /// 用途有二：运行时用 WM_SETICON 给窗口设图标；如果编译参数 /win32icon
        /// 没生效（exe 里没有图标资源），这里是任务栏图标的唯一兜底。
        /// </summary>
        public static string IconPath()
        {
            try
            {
                string target = Path.Combine(Store.AppDir, "icon.ico");
                if (!Directory.Exists(Store.AppDir)) Directory.CreateDirectory(Store.AppDir);

                Assembly asm = Assembly.GetExecutingAssembly();
                Stream s = Find(asm, "TaskHarbor.icon.ico", "icon.ico");
                if (s == null) return null;

                byte[] data;
                using (s)
                using (MemoryStream ms = new MemoryStream())
                {
                    byte[] buf = new byte[8192];
                    int n;
                    while ((n = s.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
                    data = ms.ToArray();
                }
                if (data.Length < 64) return null;

                if (File.Exists(target))
                {
                    try
                    {
                        if (new FileInfo(target).Length == data.Length) return target;   // 大小一致就当没变
                    }
                    catch { }
                }
                File.WriteAllBytes(target, data);
                return target;
            }
            catch { return null; }
        }

        private static Stream Find(Assembly asm, string resName, string fileName)
        {
            Stream s = asm.GetManifestResourceStream(resName);
            if (s != null) return s;
            string[] all = asm.GetManifestResourceNames();
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].EndsWith("." + fileName, StringComparison.OrdinalIgnoreCase))
                    return asm.GetManifestResourceStream(all[i]);
            }
            return null;
        }
    }
}
