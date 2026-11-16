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
            // two buttons split the width, like a message box
            var ok = new Button { Content = "ok" };
            var cancel = new Button { Content = "cancel" };
            var buttons = new Grid();
            buttons.ColumnDefinitions.Add(new ColumnDefinition());
            buttons.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetColumn(cancel, 1);
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            var panel = new StackPanel { Margin = new Thickness(12, 32, 12, 12) };
            panel.Children.Add(new TextBlock
            {
                Text = title, Margin = new Thickness(12, 0, 12, 12),
                // a popup sits outside the page, so text doesn't inherit the theme's color
                Foreground = (Brush)Application.Current.Resources["PhoneForegroundBrush"],
                FontSize = (double)Application.Current.Resources["PhoneFontSizeLarge"],
                FontFamily = (FontFamily)Application.Current.Resources["PhoneFontFamilySemiBold"]
            });
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
            RetroFiles.Core.Metro.SwivelIn(root);
            box.Focus();
            // select the name without its extension, like Explorer
            int dot = box.Text.LastIndexOf('.');
            box.Select(0, dot > 0 ? dot : box.Text.Length);
        }
    }
}
