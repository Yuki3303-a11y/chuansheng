using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace PhoneAudioTray
{
    internal static class FileOperationsRegression
    {
        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "delayed") { Thread.Sleep(350); Console.Write("error.txt"); Console.Error.Write("diagnostic"); return 0; }
            if (args.Length > 0 && args[0] == "slow") { Thread.Sleep(5000); return 0; }
            if (args.Length > 0 && args[0] == "-s")
            {
                File.AppendAllText(Environment.GetEnvironmentVariable("FILE_OP_TEST_LOG"), args[3] + "\n");
                if (args[1] == "fail-mkdir" && args[3].Contains("mkdir")) { Console.Error.Write("Permission denied"); return 3; }
                if (args[1] == "fail-delete") { Console.Error.Write("Permission denied"); return 5; }
                if (args[1] == "names")
                {
                    Console.WriteLine("-rw-rw-r-- 1 0 0 12 2026-11-17 12:00 foo");
                    Console.WriteLine("-rw-rw-r-- 1 0 0 12 2026-11-17 12:00  foo ");
                }
                return 0;
            }
            try
            {
                var exe = typeof(Program).GetField("_adb", BindingFlags.Static | BindingFlags.NonPublic);
                exe.SetValue(null, Assembly.GetExecutingAssembly().Location);
                var log = Path.Combine(Path.GetTempPath(), "Transmit-FileOps-" + Guid.NewGuid().ToString("N") + ".log");
                Environment.SetEnvironmentVariable("FILE_OP_TEST_LOG", log);
                var output = Transfer.RunFileCommand("delayed", Timeout.Infinite);
                Check(output == "error.txt", "Successful stderr must not corrupt command stdout");
                string message;
                Check(!Transfer.MoveToTrash("fail-mkdir", new RemoteEntry { Name = "test.txt", FullPath = "/synthetic/test.txt" }, out message), "Failed mkdir must stop trash operation");
                Check(!File.ReadAllText(log).Contains("mv "), "Trash must not attempt mv after failed mkdir");
                Check(!Transfer.DeletePermanent("fail-delete", new RemoteEntry { Name = "test.txt", FullPath = "/synthetic/test.txt" }, out message), "Nonzero deletion exit code must fail even without the word error");
                var names = Transfer.ListRemote("names", "/synthetic");
                Check(names.Count == 2 && names[0].FullPath == "/synthetic/foo" && names[1].FullPath == "/synthetic/ foo ", "Whitespace filenames must map to exact distinct paths, never the neighboring file");
                var existing = Path.GetTempFileName();
                File.WriteAllText(existing, "must survive");
                try
                {
                    try { Transfer.PullEntry("fake", new RemoteEntry { FullPath = "/synthetic/" + Path.GetFileName(existing) }, Path.GetDirectoryName(existing), CancellationToken.None); throw new Exception("Pull overwrote existing file"); }
                    catch (IOException) { Check(File.ReadAllText(existing) == "must survive", "Existing local file changed"); }
                    foreach (var invalid in new[] { "../escape", "file:stream", "CON.txt", "trailing.", "trailing " })
                    {
                        try { Transfer.PullEntry("fake", new RemoteEntry { FullPath = "/synthetic/" + invalid }, Path.GetDirectoryName(existing), CancellationToken.None); throw new Exception("Unsafe local filename accepted: " + invalid); }
                        catch (IOException) { }
                    }
                }
                finally { File.Delete(existing); }
                Console.WriteLine("PASS infinite timeout, stdout separation, mkdir failure, deletion exit code");
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                bool reportedComplete = false;
                int ticks = 0;
                using (var operation = new FileOperationDialog("Synthetic cancellation", new[] { "synthetic file" }, (index, token) =>
                {
                    Transfer.RunFileCommand("slow", Timeout.Infinite, token);
                    reportedComplete = true;
                }))
                using (var timer = new System.Windows.Forms.Timer { Interval = 25 })
                {
                    operation.StartPosition = FormStartPosition.Manual;
                    operation.Location = new System.Drawing.Point(-10000, -10000);
                    operation.ShowInTaskbar = false;
                    timer.Tick += (sender, e) => { if (++ticks == 6) operation.Close(); };
                    var clock = Stopwatch.StartNew();
                    timer.Start();
                    operation.ShowDialog();
                    Check(ticks >= 6 && clock.ElapsedMilliseconds < 2000, "Progress dialog must remain responsive and cancel its child process promptly");
                    Check(operation.WasCancelled && !reportedComplete, "Closing a transfer must not report completion");
                }
                Console.WriteLine("PASS responsive transfer progress and close cancellation without false completion");
                File.Delete(log);
                if (args.Length == 2)
                {
                    exe.SetValue(null, args[0]);
                    TestDevice(args[1]);
                }
                return 0;
            }
            catch (Exception ex) { Console.WriteLine("FAIL " + ex); return 1; }
        }
        private static void Check(bool value, string message) { if (!value) throw new Exception(message); }

        private static void TestDevice(string serial)
        {
            var id = "Transmit-FileSafety-" + Guid.NewGuid().ToString("N");
            var local = Path.Combine(Path.GetTempPath(), id);
            var remote = "/sdcard/Download/" + id;
            Directory.CreateDirectory(local);
            string message;
            foreach (var name in new[] { "error special '$ & (1).txt", ".source" })
            {
                var file = Path.Combine(local, name);
                File.WriteAllText(file, "Synthetic original content: " + id);
                Transfer.PushFile(serial, file, remote);
                try { Transfer.PushFile(serial, file, remote); throw new Exception("Push overwrote existing remote file"); }
                catch (IOException) { }
                var entry = new RemoteEntry { Name = name, FullPath = remote + "/" + name };
                var pull = Path.Combine(local, "pull-initial");
                Transfer.PullEntry(serial, entry, pull, CancellationToken.None);
                Check(File.ReadAllText(Path.Combine(pull, name)) == File.ReadAllText(file), "Special filename pull changed content");
                Check(Transfer.MoveToTrash(serial, entry, out message), "Synthetic trash failed: " + message);
                var trash = Transfer.ListTrash(serial).Find(item => item.Source == entry.FullPath);
                Check(trash != null && trash.Name == name, "Exact provenance/name missing, including .source filename");
                File.WriteAllText(file, "Synthetic replacement must survive");
                Transfer.PushFile(serial, file, remote);
                Check(!Transfer.RestoreTrash(serial, trash, out message), "Restore must refuse to overwrite existing file");
                var replacement = Path.Combine(local, "pull-replacement");
                Transfer.PullEntry(serial, entry, replacement, CancellationToken.None);
                Check(File.ReadAllText(Path.Combine(replacement, name)) == "Synthetic replacement must survive", "Restore overwrote replacement");
                Check(Transfer.DeletePermanent(serial, entry, out message), "Cannot delete synthetic replacement: " + message);
                Check(Transfer.RestoreTrash(serial, trash, out message), "Synthetic restore failed: " + message);
                var restored = Path.Combine(local, "pull-restored");
                Transfer.PullEntry(serial, entry, restored, CancellationToken.None);
                Check(File.ReadAllText(Path.Combine(restored, name)) == "Synthetic original content: " + id, "Restore changed original bytes");
                Check(Transfer.MoveToTrash(serial, entry, out message), "Second synthetic trash failed: " + message);
                trash = Transfer.ListTrash(serial).Find(item => item.Source == entry.FullPath);
                Check(trash != null && Transfer.PurgeTrash(serial, trash, out message), "Synthetic purge failed");
            }
            // Only this harness's randomly named synthetic directory is removed.
            Check(Transfer.DeletePermanent(serial, new RemoteEntry { Name = id, FullPath = remote, IsDir = true }, out message), "Synthetic directory cleanup failed");
            Console.WriteLine("PASS device: special characters, error filename, .source collision, exact trash provenance, restore conflict, content roundtrip and purge");
        }
    }
}
