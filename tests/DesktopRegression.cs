using System;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PhoneAudioTray
{
    internal static class DesktopRegression
    {
        [STAThread]
        public static int Main()
        {
            var payloadCheck = typeof(Payload).GetMethod("IsComplete", BindingFlags.Static | BindingFlags.NonPublic);
            var temporary = System.IO.Path.GetTempFileName();
            try {
                if ((bool)payloadCheck.Invoke(null, new object[] { temporary })) throw new Exception("Empty runtime file accepted");
                System.IO.File.WriteAllText(temporary, "synthetic runtime");
                if (!(bool)payloadCheck.Invoke(null, new object[] { temporary })) throw new Exception("Nonempty runtime file rejected");
            } finally { System.IO.File.Delete(temporary); }
            Console.WriteLine("PASS runtime completeness rejects zero-byte files");
            const string path = @"Software\Microsoft\Windows\CurrentVersion\Run";
            const string name = "PhoneAudioTray";
            using (var key = Registry.CurrentUser.CreateSubKey(path))
            {
                var original = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                var kind = original == null ? RegistryValueKind.String : key.GetValueKind(name);
                try
                {
                    Shortcut.SetStartup(true);
                    if (!Shortcut.StartupEnabled || (string)key.GetValue(name) != "\"" + Application.ExecutablePath + "\"")
                        throw new Exception("Startup path mismatch");
                    Shortcut.SetStartup(false);
                    if (Shortcut.StartupEnabled) throw new Exception("Startup disable failed");
                    Console.WriteLine("PASS startup enable/disable, quoted executable path");
                }
                finally
                {
                    if (original == null) key.DeleteValue(name, false);
                    else key.SetValue(name, original, kind);
                }
            }
            Application.EnableVisualStyles();
            typeof(Program).GetMethod("BuildTray", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
            try
            {
                typeof(NotifyIcon).GetMethod("OnMouseUp", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(Program.Tray, new object[] { new MouseEventArgs(MouseButtons.Left, 1, 0, 0, 0) });
                Application.DoEvents();
                if (!Program.Tray.ContextMenuStrip.Visible) throw new Exception("Left click did not open menu");
                Console.WriteLine("PASS left mouse release opens tray menu");
                Program.Tray.ContextMenuStrip.Close();
            }
            finally { Program.Tray.Dispose(); }
            return 0;
        }
    }
}
