// ============================================================
//  启动引导：把内嵌的 WebView2 程序集在运行时释放并加载
//  这样最终交付就是一个 exe，不用旁边再放一堆 dll
// ============================================================
using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TaskHarbor
{
    internal static class Bootstrap
    {
        private static string _rt;
        private static bool _hooked;
        private static bool _nativeReady;
        private static bool _loaderPreloaded;

        /// <summary>原生加载器是否已成功预加载（诊断用）。</summary>
        public static bool LoaderPreloaded { get { return _loaderPreloaded; } }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string lpFileName);

        public static void Hook()
        {
            if (_hooked) return;
            _hooked = true;
            AppDomain.CurrentDomain.AssemblyResolve += new ResolveEventHandler(OnResolve);
        }

        private static string Rt
        {
            get
            {
                if (_rt != null) return _rt;
                string dir = Path.Combine(Store.AppDir, "runtime", WebAssets.Ver);
                try { if (!Directory.Exists(dir)) Directory.CreateDirectory(dir); }
                catch { }
                _rt = dir;
                return _rt;
            }
        }

        private static Assembly OnResolve(object sender, ResolveEventArgs args)
        {
            try
            {
                string simple = new AssemblyName(args.Name).Name;
                if (simple == null) return null;
                if (simple.IndexOf("WebView2", StringComparison.OrdinalIgnoreCase) < 0) return null;

                string file = Path.Combine(Rt, simple + ".dll");
                if (!File.Exists(file)) Extract(simple + ".dll", file);
                if (File.Exists(file)) return Assembly.LoadFrom(file);
            }
            catch { }
            return null;
        }

        /// <summary>从 exe 内嵌资源里把一个文件释放到 target（内容一致就跳过）。</summary>
        private static bool Extract(string fileName, string target)
        {
            try
            {
                if (File.Exists(target)) return true;
                Assembly asm = Assembly.GetExecutingAssembly();
                string[] all = asm.GetManifestResourceNames();
                for (int i = 0; i < all.Length; i++)
                {
                    if (!all[i].EndsWith(fileName, StringComparison.OrdinalIgnoreCase)) continue;
                    using (Stream s = asm.GetManifestResourceStream(all[i]))
                    {
                        if (s == null) return false;
                        string tmp = target + ".tmp";
                        using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            byte[] buf = new byte[65536];
                            int n;
                            while ((n = s.Read(buf, 0, buf.Length)) > 0) fs.Write(buf, 0, n);
                        }
                        if (File.Exists(target)) File.Delete(target);
                        File.Move(tmp, target);
                    }
                    return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// 确保原生 WebView2Loader.dll 能被找到。
        /// 三段保险：预加载进进程 → 告诉 SDK 去哪找 → 兜底在 exe 旁边放一份。
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void PrepareNative()
        {
            if (_nativeReady) return;
            _nativeReady = true;

            try
            {
                string dir = Rt;

                // 1) 先把三个文件都释放出来（后面要按路径加载）
                string corePath = Path.Combine(dir, "Microsoft.Web.WebView2.Core.dll");
                string wfPath = Path.Combine(dir, "Microsoft.Web.WebView2.WinForms.dll");
                string loader = Path.Combine(dir, "WebView2Loader.dll");

                Extract("Microsoft.Web.WebView2.Core.dll", corePath);
                Extract("Microsoft.Web.WebView2.WinForms.dll", wfPath);
                Extract("WebView2Loader.dll", loader);

                // 2) 把原生加载器预加载进本进程。
                //    Windows 的 LoadLibrary 命中已加载模块时按「文件基名」返回，
                //    所以之后 WebView2 内部再 LoadLibrary("WebView2Loader.dll") 一定拿到它。
                try
                {
                    if (File.Exists(loader))
                        _loaderPreloaded = LoadLibraryW(loader) != IntPtr.Zero;
                }
                catch { }

                // 3) 同目录再放一份（有些版本的 SDK 只认 app 目录）
                try
                {
                    string exeDir = Path.GetDirectoryName(Application.ExecutablePath);
                    if (!string.IsNullOrEmpty(exeDir) && File.Exists(loader))
                    {
                        string beside = Path.Combine(exeDir, "WebView2Loader.dll");
                        if (!File.Exists(beside)) File.Copy(loader, beside, false);
                    }
                }
                catch { }

                // 4) 反射调用 SetLoaderDllFolderPath（SDK 1.0.1774+ 才有，兼容老版本）
                try
                {
                    if (File.Exists(corePath))
                    {
                        Assembly core = Assembly.LoadFrom(corePath);
                        Type t = core.GetType("Microsoft.Web.WebView2.Core.CoreWebView2Environment");
                        if (t != null)
                        {
                            MethodInfo mi = t.GetMethod("SetLoaderDllFolderPath",
                                BindingFlags.Public | BindingFlags.Static);
                            if (mi != null) mi.Invoke(null, new object[] { dir });
                        }
                    }
                }
                catch { }

                // 预加载没成功也不致命：还有「同目录放一份」和 SetLoaderDllFolderPath 两条路
            }
            catch { }
        }
    }
}
