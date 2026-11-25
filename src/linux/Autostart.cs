// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman, Linux only: starting with the session. Windows does this with a scheduled task made by the window
// (src\Ui.cs), because it needs one that starts elevated; here Ohman is an ordinary program and the desktop's
// own mechanism is enough: a .desktop file in ~/.config/autostart (the XDG autostart specification, honoured
// by GNOME, KDE, Xfce, Cinnamon and the rest). The entry starts Ohman hidden, as the Windows task does.
using System;
using System.IO;

namespace Ohman {
    static class Autostart {
        /// <summary>~/.config/autostart/ohman.desktop, or under $XDG_CONFIG_HOME when that is set.</summary>
        public static string File_ {
            get {
                string config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
                if (string.IsNullOrEmpty(config) || config[0] != '/') {
                    string home = Environment.GetEnvironmentVariable("HOME");
                    if (string.IsNullOrEmpty(home)) home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    config = Path.Combine(home, ".config");
                }
                return Path.Combine(config, "autostart", Program.FileStem + ".desktop");
            }
        }

        /// <summary>The program to start: the installed launcher when there is one, else this very binary.</summary>
        static string Command() {
            if (File.Exists("/usr/bin/" + Program.FileStem)) return "/usr/bin/" + Program.FileStem;
            string exe = Update.ExePath;
            return string.IsNullOrEmpty(exe) ? Program.FileStem : exe;
        }

        /// <summary>Whether Ohman starts with the session: the entry exists and has not been switched off by a
        /// desktop's own "startup applications" editor, which writes Hidden=true rather than deleting it.</summary>
        public static bool IsOn() {
            try {
                if (!File.Exists(File_)) return false;
                foreach (string line in File.ReadAllLines(File_)) {
                    string l = line.Trim();
                    if (l.Equals("Hidden=true", StringComparison.OrdinalIgnoreCase)) return false;
                    if (l.Equals("X-GNOME-Autostart-enabled=false", StringComparison.OrdinalIgnoreCase)) return false;
                }
                return true;
            } catch { return false; }
        }

        /// <summary>Write or remove the entry. True when the file is now as asked.</summary>
        public static bool Set(bool on) {
            try {
                string file = File_;
                if (!on) { if (File.Exists(file)) File.Delete(file); Log.Write("autostart removed"); return true; }
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                string exec = Command();
                // The Exec key's own quoting (Desktop Entry specification): a path with a space goes in double quotes.
                if (exec.IndexOf(' ') >= 0) exec = "\"" + exec.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
                File.WriteAllText(file,
                    "[Desktop Entry]\n" +
                    "Type=Application\n" +
                    "Name=" + Program.DisplayName + "\n" +
                    "Comment=Fan, performance and keyboard control for HP OMEN and Victus laptops\n" +
                    "Exec=" + exec + " --hidden\n" +
                    "Icon=" + Program.FileStem + "\n" +
                    "Terminal=false\n" +
                    "X-GNOME-Autostart-enabled=true\n");
                Log.Write("autostart: " + file);
                return true;
            } catch (Exception ex) { Log.Write("autostart: " + ex.Message); return false; }
        }
    }
}
