// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace Ohman {
    public sealed partial class MainWindow {
        Booster booster;
        BoostKeys boostKeys;
        DispatcherTimer boostUiTimer, boostExitTimer;

        void BuildBooster() {
            var auto = F<ToggleButton>("TgBoostAuto");
            var status = F<TextBlock>("TxtBoostStatus");
            var apply = F<Button>("BtnBoostApply");
            var restore = F<Button>("BtnBoostRestore");
            var choices = F<StackPanel>("BoostChoices");
            var games = F<ListBox>("BoostGames");
            var add = F<Button>("BtnBoostGameAdd");
            var remove = F<Button>("BtnBoostGameRemove");
            var settings = E.S;
            if (!E.Hw.IsDemo) { booster = new Booster(); boostKeys = new BoostKeys(booster); }

            Action configure = delegate {
                if (booster != null) booster.Configure(settings.BoostAuto, settings.BoostSelection, settings.BoostGames);
                apply.IsEnabled = booster != null && settings.BoostSelection != 0;
            };
            Action save = delegate { settings.Save(); configure(); };
            auto.IsChecked = settings.BoostAuto;
            OnSwitch(auto, on => { settings.BoostAuto = on; save(); });

            AddBoostChoice(choices, BoostOptions.Parking, "Disable CPU core parking", "Keep logical processors unparked", save);
            AddBoostChoice(choices, BoostOptions.Efficiency, "Prefer CPU performance", "Adjust the active plan and request Best performance where Windows supports it", save);
            AddBoostChoice(choices, BoostOptions.Sharing, "Disable file and printer sharing", "Stop Windows SMB sharing and prevent it from restarting during a boost", save);
            AddBoostChoice(choices, BoostOptions.GameBar, "Stop Xbox Game Bar and capture", "Turns off capture and stops Game Bar while a selected game is boosted. Can interrupt a recording.", save);
            AddBoostChoice(choices, BoostOptions.GameMode, "Disable Game Mode", "Temporarily turn off the Windows Game Mode preference", save);
            AddBoostChoice(choices, BoostOptions.WindowsKey, "Disable Windows key", "Block it while a selected game has focus", save);
            AddBoostChoice(choices, BoostOptions.Memory, "Memory boost", "Clear working sets and standby cache once at boost start", save);
            AddBoostChoice(choices, BoostOptions.Priority, "Set game process to High priority", "Reapply High priority for selected games during the boost", save);

            Action<string> append = path => games.Items.Add(new ListBoxItem { Content = System.IO.Path.GetFileName(path), Tag = path, ToolTip = path });
            foreach (string game in settings.BoostGames) append(game);
            Action saveGames = delegate {
                var selected = new List<string>();
                foreach (ListBoxItem item in games.Items) selected.Add((string)item.Tag);
                settings.BoostGames = selected.ToArray();
                save();
            };
            add.Click += delegate {
                var picker = new Microsoft.Win32.OpenFileDialog {
                    Title = "Choose game executables", Filter = "Windows applications (*.exe)|*.exe", CheckFileExists = true, Multiselect = true
                };
                if (picker.ShowDialog(this) != true) return;
                var existing = new HashSet<string>(settings.BoostGames, StringComparer.OrdinalIgnoreCase);
                foreach (string path in Booster.ParseGames(string.Join("|", picker.FileNames))) if (existing.Add(path)) append(path);
                saveGames();
            };
            remove.Click += delegate { if (games.SelectedItem != null) { games.Items.Remove(games.SelectedItem); saveGames(); } };
            games.SelectionChanged += delegate { remove.IsEnabled = games.SelectedItem != null; };

            configure();
            restore.IsEnabled = booster != null;
            if (booster == null) { status.Text = "Preview · Windows settings stay as they are"; return; }
            apply.Click += delegate { booster.StartManual(); status.Text = "Applying boost…"; };
            restore.Click += delegate { booster.RestoreNow(); status.Text = "Restoring…"; };
            boostUiTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
            boostUiTimer.Tick += delegate {
                boostKeys.Update();
                if (cur == Page.Booster && IsVisible) {
                    status.Text = boostKeys.Failed ? booster.Status + " · key filter unavailable" : booster.Status;
                    apply.IsEnabled = settings.BoostSelection != 0 && !booster.Manual && !booster.Draining;
                }
            };
            boostUiTimer.Start();
        }

        void AddBoostChoice(Panel choices, BoostOptions option, string title, string description, Action save) {
            var label = new StackPanel();
            label.Children.Add(new TextBlock { Text = title, Style = (Style)root.Resources["Row"], TextWrapping = TextWrapping.Wrap });
            label.Children.Add(new TextBlock { Text = description, Style = (Style)root.Resources["RowSub"], TextWrapping = TextWrapping.Wrap });
            var check = new CheckBox { Content = label, IsChecked = (E.S.BoostSelection & (int)option) != 0, Margin = new Thickness(0, 0, 0, 10), Padding = new Thickness(5, 0, 0, 0) };
            System.Windows.Automation.AutomationProperties.SetName(check, title);
            RoutedEventHandler update = delegate {
                if (check.IsChecked == true) E.S.BoostSelection |= (int)option;
                else E.S.BoostSelection &= ~(int)option;
                save();
            };
            check.Checked += update;
            check.Unchecked += update;
            choices.Children.Add(check);
        }

        bool BoosterReadyToClose() { return booster == null || booster.Drain(); }

        void WaitForBoosterExit() {
            if (boostExitTimer != null) return;
            if (!IsVisible) ShowPanel();
            ShowToast("Restoring Windows settings before exit…", false);
            var started = System.Diagnostics.Stopwatch.StartNew();
            boostExitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            boostExitTimer.Tick += delegate {
                bool ready = BoosterReadyToClose();
                if (!ready && started.Elapsed.TotalSeconds < 12) return;
                boostExitTimer.Stop();
                boostExitTimer = null;
                if (ready) { ExitApp(); return; }
                if (MessageBox.Show(this, "Windows settings are still being restored. Exit and retry recovery the next time you open this build?",
                    Program.DisplayName, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes) {
                    quietExit = true;
                    ExitApp();
                } else WaitForBoosterExit();
            };
            boostExitTimer.Start();
        }

        void CloseBooster() {
            if (boostExitTimer != null) boostExitTimer.Stop();
            if (boostUiTimer != null) boostUiTimer.Stop();
            if (boostKeys != null) boostKeys.Dispose();
            if (booster != null) booster.Dispose();
        }
    }
}
