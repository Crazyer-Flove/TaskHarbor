// ============================================================
//  入口：释放运行时 → 单实例 → DPI 感知 → 启动窗口
// ============================================================
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

// ---------------- 程序集元数据 ----------------
// C# 编译器没有设置版本号的命令行开关（/version: 是 VB 编译器的选项），
// 程序集版本必须通过特性声明。
// TargetFramework 特性还会让运行时按 .NET 4.6.2+ 的兼容行为启动，
// 即默认就启用 TLS 1.2（AiClient 里另有显式设置，作为双保险）。
[assembly: System.Reflection.AssemblyTitle("TaskHarbor")]
[assembly: System.Reflection.AssemblyProduct("TaskHarbor")]
[assembly: System.Reflection.AssemblyDescription("A local-first Eisenhower task workspace")]
[assembly: System.Reflection.AssemblyVersion("3.0.3.0")]
[assembly: System.Reflection.AssemblyFileVersion("3.0.3.0")]
[assembly: System.Runtime.Versioning.TargetFramework(
    ".NETFramework,Version=v4.6.2",
    FrameworkDisplayName = ".NET Framework 4.6.2")]

namespace TaskHarbor
{
    internal static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        private const string MUTEX_NAME = "TaskHarbor_SingleInstance_v3";
        private static string EVENT_NAME = "TaskHarbor_ShowPanel_v3";
        internal static bool DemoMode;

        [STAThread]
        private static void Main(string[] args)
        {
            string dataDir = null;
            bool selfTest = Array.IndexOf(args, "--self-test") >= 0;
            foreach (string arg in args) if (arg == "--demo") DemoMode = true;
            for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--data-dir") dataDir = args[i + 1];
            if (selfTest && string.IsNullOrEmpty(dataDir)) { Environment.ExitCode = 2; return; }
            if (DemoMode && string.IsNullOrEmpty(dataDir)) dataDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaskHarbor", "Demo");
            if (string.IsNullOrEmpty(dataDir))
            {
                try {
                    using (Mutex legacy = Mutex.OpenExisting("DeskTodo_SingleInstance_v1"))
                    {
                        if (!legacy.WaitOne(0)) { MessageBox.Show("请先退出旧版待办，再启动 TaskHarbor，以免同时修改任务文件。", "TaskHarbor"); return; }
                        legacy.ReleaseMutex();
                    }
                } catch (WaitHandleCannotBeOpenedException) { } catch (AbandonedMutexException) { }
            }
            try { Store.Initialize(dataDir); }
            catch (Exception ex) { if (!selfTest) MessageBox.Show(ex.Message, "TaskHarbor", MessageBoxButtons.OK, MessageBoxIcon.Error); Environment.ExitCode = 1; return; }
            // 必须先挂上程序集解析钩子，后面才能用内嵌的 WebView2
            Bootstrap.Hook();
            Bootstrap.PrepareNative();
            if (selfTest) { Environment.ExitCode = Diagnostics.Run(); return; }

            bool created = false;
            string suffix = string.IsNullOrEmpty(dataDir) ? "" : "_" + StableKey(Store.AppDir);
            EVENT_NAME += suffix;
            Mutex mtx = new Mutex(true, MUTEX_NAME + suffix, out created);

            if (!created)
            {
                WakeExisting();
                try { mtx.Close(); } catch { }
                return;
            }

            try { SetProcessDPIAware(); } catch { }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            try
            {
                RunApp();
            }
            catch (Exception ex)
            {
                MessageBox.Show("程序启动失败：\n\n" + ex.ToString(), "TaskHarbor",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                try { mtx.ReleaseMutex(); } catch { }
                try { mtx.Close(); } catch { }
            }
        }

        private static string StableKey(string path)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(path.ToLowerInvariant()))).Replace("-", "").Substring(0, 16);
        }

        private static void WakeExisting()
        {
            try
            {
                EventWaitHandle ev = EventWaitHandle.OpenExisting(EVENT_NAME);
                ev.Set();
                ev.Close();
            }
            catch { }
        }

        // 单独一层：让 Main 的 IL 里不出现 WebView2 相关类型，
        // 保证 JIT 编译 Main 时不需要解析那些程序集
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RunApp()
        {
            HostForm form = new HostForm();
            StartWakeupListener(form);
            Application.Run(form);
        }

        private static void StartWakeupListener(HostForm form)
        {
            try
            {
                EventWaitHandle ev = new EventWaitHandle(false, EventResetMode.AutoReset, EVENT_NAME);
                Thread t = new Thread(delegate()
                {
                    while (true)
                    {
                        bool signaled = false;
                        try { signaled = ev.WaitOne(); }
                        catch { return; }
                        if (!signaled) return;
                        try { form.BeginInvoke(new Action(delegate { form.ShowPanel(); })); }
                        catch { return; }
                    }
                });
                t.IsBackground = true;
                t.SetApartmentState(ApartmentState.MTA);
                t.Start();
            }
            catch { }
        }
    }
}
