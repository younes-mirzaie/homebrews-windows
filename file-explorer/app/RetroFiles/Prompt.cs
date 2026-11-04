// A one-line text prompt (Silverlight for Windows Phone has no input dialog).
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Phone.Controls;

namespace RetroFiles
{
    public static class Prompt
    {
        public static void Show(PhoneApplicationPage page, string title, string initial, Action<string> done)
        {
            var popup = new Popup();
            var bar = page.ApplicationBar;
            var box = new TextBox { Text = initial ?? "", InputScope = new InputScope { Names = { new InputScopeName { NameValue = InputScopeNameValue.Text } } } };
            var ok = new Button { Content = "ok", Width = 200 };
            var cancel = new Button { Content = "cancel", Width = 200 };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            var panel = new StackPanel { Margin = new Thickness(12, 40, 12, 12) };
            panel.Children.Add(new TextBlock { Text = title, FontSize = 32, Margin = new Thickness(12, 0, 12, 12) });
            panel.Children.Add(box);
            panel.Children.Add(buttons);
            var root = new Border
            {
                Background = (Brush)Application.Current.Resources["PhoneChromeBrush"],
                Width = Application.Current.Host.Content.ActualWidth,
                Child = panel
            };
            popup.Child = root;

            EventHandler<System.ComponentModel.CancelEventArgs> back = null;
            Action<string> close = result =>
            {
                popup.IsOpen = false;
                page.BackKeyPress -= back;
                if (bar != null) bar.IsVisible = true;
                done(result);
            };
            back = (s, e) => { e.Cancel = true; close(null); };
            ok.Click += (s, e) => close(box.Text);
            cancel.Click += (s, e) => close(null);
            box.KeyUp += (s, e) => { if (e.Key == Key.Enter) close(box.Text); };
            page.BackKeyPress += back;
            if (bar != null) bar.IsVisible = false;
            popup.IsOpen = true;
            box.Focus();
            // select the name without its extension, like Explorer
            int dot = box.Text.LastIndexOf('.');
            box.Select(0, dot > 0 ? dot : box.Text.Length);
        }
    }
}
