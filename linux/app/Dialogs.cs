// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman, Linux: MessageBox. The WPF window asks its few yes/no questions with System.Windows.MessageBox
// (src\Ui.cs: the thermal guard, the reset, the graphics switch, the restart). Avalonia has none, so this is a
// small modal window with the same Show(...) calls, the same enums and the same blocking behaviour, which lets
// the window's code read the same on both systems.
//
// Differences from the WPF original
//  - Windows draws its own dialog; this one is drawn in the app's colours and fonts, with the window manager's
//    title bar. The icon argument is accepted and not drawn.
//  - Show blocks by running a nested dispatcher frame until the dialog closes, as a Win32 message box does.
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Ohman {
    public enum MessageBoxButton { OK, YesNo, YesNoCancel }
    public enum MessageBoxImage { None, Information, Question, Warning, Error }
    public enum MessageBoxResult { None, OK, Cancel, Yes, No }

    static class MessageBox {
        public static MessageBoxResult Show(string text, string title, MessageBoxButton buttons, MessageBoxImage image) { return Show(null, text, title, buttons, image); }

        public static MessageBoxResult Show(Window owner, string text, string title, MessageBoxButton buttons, MessageBoxImage image) {
            var result = buttons == MessageBoxButton.OK ? MessageBoxResult.OK : buttons == MessageBoxButton.YesNo ? MessageBoxResult.No : MessageBoxResult.Cancel;
            var w = new Window {
                Title = title, Background = Ui.Card, CanResize = false, ShowInTaskbar = owner == null,
                SizeToContent = SizeToContent.WidthAndHeight, WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
                Topmost = owner == null,
            };
            var body = new StackPanel { Margin = new Thickness(24, 22, 24, 18), Width = 380 };
            body.Children.Add(new TextBlock { Text = text, FontFamily = Ui.UiFont, FontSize = 13, Foreground = Ui.TextB, TextWrapping = TextWrapping.Wrap, LineHeight = 19 });
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
            Action<string, MessageBoxResult, bool> add = delegate(string label, MessageBoxResult r, bool primary) {
                var tb = new TextBlock { Text = label, FontFamily = Ui.UiFont, FontSize = 13, FontWeight = primary ? FontWeight.Medium : FontWeight.Normal, Foreground = primary ? Brushes.White : Ui.TextB, HorizontalAlignment = HorizontalAlignment.Center };
                var b = new Border { Background = primary ? (IBrush)Ui.Accent : Ui.Pill, CornerRadius = new CornerRadius(7), Padding = new Thickness(18, 7, 18, 7), MinWidth = 76, Margin = new Thickness(8, 0, 0, 0), Cursor = Cursors.Hand, Child = tb };
                b.PointerEntered += delegate { b.Opacity = 0.85; };
                b.PointerExited += delegate { b.Opacity = 1; };
                Ui.LeftUp(b, delegate { result = r; w.Close(); });
                row.Children.Add(b);
            };
            if (buttons == MessageBoxButton.OK) add("OK", MessageBoxResult.OK, true);
            else {
                add("Yes", MessageBoxResult.Yes, true);
                add("No", MessageBoxResult.No, false);
                if (buttons == MessageBoxButton.YesNoCancel) add("Cancel", MessageBoxResult.Cancel, false);
            }
            body.Children.Add(row);
            w.Content = body;
            // Enter takes the first button, Escape the answer that changes nothing, as a message box does.
            w.KeyDown += delegate(object o, KeyEventArgs e) {
                if (e.Key == Key.Enter) { result = buttons == MessageBoxButton.OK ? MessageBoxResult.OK : MessageBoxResult.Yes; w.Close(); }
                else if (e.Key == Key.Escape) w.Close();
            };
            var frame = new DispatcherFrame();
            w.Closed += delegate { frame.Continue = false; };
            try {
                if (owner != null && owner.IsVisible) w.ShowDialog(owner);
                else w.Show();
                Dispatcher.UIThread.PushFrame(frame);
            } catch (Exception ex) { Log.Write("dialog: " + ex.Message); }
            return result;
        }
    }
}
