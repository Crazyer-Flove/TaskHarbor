using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Web.Script.Serialization;
using Microsoft.Web.WebView2.Core;

namespace TaskHarbor
{
    internal static class Diagnostics
    {
        // Noninteractive release-binary check. Does not instantiate a Form or mutate task data.
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int Run()
        {
            var report = new Dictionary<string, object>();
            report["product"] = "TaskHarbor";
            report["version"] = Assembly.GetExecutingAssembly().GetName().Version.ToString();
            report["uiVerified"] = false;
            try
            {
                report["nativeLoader"] = Bootstrap.LoaderPreloaded;
                report["webViewRuntime"] = CoreWebView2Environment.GetAvailableBrowserVersionString();
                string web = WebAssets.Ensure();
                foreach (string name in new[] { "panel.html", "panel.css", "panel.js", "model.js", "brand.png" })
                    if (!File.Exists(Path.Combine(web, name)) || new FileInfo(Path.Combine(web, name)).Length == 0) throw new IOException("Embedded resource not extracted: " + name);
                string probe = Path.Combine(Store.AppDir, "self-test-" + Guid.NewGuid().ToString("N") + ".tmp");
                if (!Store.WriteText(probe, "TaskHarbor probe") || File.ReadAllText(probe) != "TaskHarbor probe") throw new IOException("Data directory is not writable");
                File.Delete(probe);
                report["embeddedResources"] = true;
                report["success"] = Bootstrap.LoaderPreloaded && !string.IsNullOrEmpty(report["webViewRuntime"].ToString());
            }
            catch (Exception ex) { report["success"] = false; report["error"] = ex.GetType().Name + ": " + ex.Message; }
            Store.WriteText(Path.Combine(Store.AppDir, "self-test.json"), new JavaScriptSerializer().Serialize(report));
            return (bool)report["success"] ? 0 : 1;
        }
    }
}
