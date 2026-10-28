using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace TaskHarbor
{
    // This test assembly never creates a window or a notification icon.
    internal static class Program { internal static bool DemoMode = true; }
    internal static class NativeTests
    {
        private static int _count;
        private static void Check(string name, bool value) { if (!value) throw new Exception(name); Console.WriteLine("PASS " + name); _count++; }
        private static string _root;
        private static string Dir(string name) { string d = Path.Combine(_root, name); Directory.CreateDirectory(d); return d; }
        private static void Main(string[] args)
        {
            try { _root = Path.GetFullPath(args[0]); Directory.CreateDirectory(_root); Run(); Console.WriteLine("Native checks passed: " + _count); }
            catch (Exception ex) { Console.WriteLine("FAIL " + ex.GetType().Name + ": " + ex.Message); Environment.Exit(1); }
        }
        private static void Run()
        {
            Store.Initialize(Dir("primary"));
            string first = "[{\"id\":\"t1\",\"title\":\"Task\",\"period\":\"week\",\"planDate\":\"2026-10-26\",\"date\":\"\",\"subtasks\":[{\"title\":\"Step\",\"done\":false}]}]";
            Check("task data roundtrip including subtasks and empty deadline", Store.SaveTasks(first) && Store.LoadTasks() == first);
            Check("invalid task row rejected", !Store.SaveTasks("[{\"title\":42}]"));
            Check("invalid save leaves original file", Store.LoadTasks() == first);
            Check("atomic replacement succeeds", Store.SaveTasks("[]") && Store.LoadTasks() == "[]");
            Check("pre-upgrade backup preserves original file", File.ReadAllText(Store.TasksPath + ".before-taskharbor-v3.bak") == first);
            File.WriteAllText(Store.TasksPath, "{broken");
            bool blocked = false; try { Store.LoadTasks(); } catch (IOException) { blocked = true; }
            Check("corrupt task file raises a visible load error", blocked);
            Check("load failure does not overwrite corrupt bytes", File.ReadAllText(Store.TasksPath) == "{broken");
            File.WriteAllText(Store.TasksPath, first);
            Store.BackupBeforeImport();
            Check("import creates recoverable backup", Directory.GetFiles(Path.Combine(Path.GetDirectoryName(Store.TasksPath), "backups")).Length == 1);
            var cfg = new Dictionary<string, object> { {"apiKey","test-key-not-a-real-credential"}, {"imgApiKey","test-image-not-real"}, {"pinned",true}, {"layout","compact"}, {"idleDelay",1000} };
            Check("settings save", Store.SaveConfig(cfg));
            Check("in-memory API keys retained", cfg.ContainsKey("apiKey") && (string)cfg["apiKey"] == "test-key-not-a-real-credential");
            Check("disk config excludes plaintext API keys", !File.ReadAllText(Store.ConfigPath).Contains("test-key-not-a-real-credential"));
            Check("DPAPI current-user roundtrip", (string)Store.LoadConfig()["apiKey"] == "test-key-not-a-real-credential");
            Check("image key roundtrip", (string)Store.LoadConfig()["imgApiKey"] == "test-image-not-real");
            Check("encrypted keys survive repeated config saves", Store.SaveConfig(cfg) && (string)Store.LoadConfig()["apiKey"] == "test-key-not-a-real-credential");
            Check("layout preference survives reload", (string)Store.LoadConfig()["layout"] == "compact");
            string legacy = Dir("legacy"), destination = Dir("migrated");
            File.WriteAllText(Path.Combine(legacy,"tasks.json"), first);
            File.WriteAllText(Path.Combine(legacy,"config.json"), "{\"provider\":\"custom\",\"dataPath\":" + new JavaScriptSerializer().Serialize(Path.Combine(legacy,"tasks.json")) + "}");
            Store.Initialize(destination); Store.MigrateLegacy(legacy);
            Check("legacy tasks copied into new brand directory", File.ReadAllText(Store.DefaultTasksPath) == first);
            Check("legacy tasks remain untouched", File.ReadAllText(Path.Combine(legacy,"tasks.json")) == first);
            Check("legacy default path rewritten to new directory", (string)Store.LoadConfig()["dataPath"] == Store.DefaultTasksPath);
            Check("migration marker written", File.Exists(Path.Combine(destination,"migration-v3.complete")));
            Check("migration does not overwrite existing new data", Store.SaveTasks("[]")); Store.MigrateLegacy(legacy); Check("existing new task data preserved", Store.LoadTasks()=="[]");
            Store.Initialize(Dir("invalid-migration")); File.WriteAllText(Path.Combine(legacy,"tasks.json"),"bad");
            blocked=false;try{Store.MigrateLegacy(legacy);}catch(IOException){blocked=true;}
            Check("invalid legacy migration fails before writing data", blocked && !File.Exists(Store.ConfigPath) && !File.Exists(Store.DefaultTasksPath));
            var items = AiJob.ParseItems("{\"items\":[{\"title\":\"Plan\",\"period\":\"week\",\"date\":\"\",\"quadrant\":\"q2\"},{\"title\":\"Deadline\",\"date\":\"2026-10-30\",\"due\":\"18:00\",\"quadrant\":\"q1\"}]}");
            Check("AI keeps absent hard deadline empty",items.Count==2 && items[0].date=="" && items[0].period=="week");
            Check("AI explicit deadline and time preserved",items[1].date=="2026-10-30" && items[1].due=="18:00");
            Check("AI malformed output safely returns empty",AiJob.ParseItems("bad").Count==0);
            var reminder=new Dictionary<string,object>{{"id","r1"},{"title","Private title"},{"date","2026-10-27"},{"due","09:00"},{"remind",true}};
            string token; DateTime now = new DateTime(2026,10,27,10,0,0,DateTimeKind.Local);
            Check("due reminder within catch-up window",DesktopServices.Due(reminder,now,out token));
            Check("reminder key excludes private title",!token.Contains("Private title"));
            reminder["done"]=true;Check("completed task never reminds",!DesktopServices.Due(reminder,now,out token));reminder["done"]=false;
            reminder["deletedAt"]="2026-10-27";Check("deleted task never reminds",!DesktopServices.Due(reminder,now,out token));reminder.Remove("deletedAt");
            Check("stale reminders older than 24 hours ignored",!DesktopServices.Due(reminder,now.AddDays(2),out token));
            reminder["snoozeUntil"]=now.AddMinutes(15).ToUniversalTime().ToString("o");Check("snooze defers reminder",!DesktopServices.Due(reminder,now,out token));Check("snooze becomes due on time",DesktopServices.Due(reminder,now.AddMinutes(15),out token));
            reminder["date"]="";Check("reminder never fabricates a missing date",!DesktopServices.Due(reminder,now,out token));
            Store.Initialize(Dir("unwritable"));Directory.CreateDirectory(Store.TasksPath);Check("write failure reported rather than success",!Store.SaveTasks(first));
        }
    }
}
