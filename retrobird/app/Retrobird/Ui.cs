// Small UI helpers shared by the pages: app bar buttons, the progress indicator, error text.
using System;
using System.Windows;
using Microsoft.Phone.Controls;
using Microsoft.Phone.Shell;
using Retrobird.Core;

namespace Retrobird
{
    public static class Ui
    {
        public static ApplicationBarIconButton Button(IApplicationBar bar, string icon, string text, EventHandler click)
        {
            var b = new ApplicationBarIconButton(new Uri("/Assets/AppBar/" + icon + ".png", UriKind.Relative)) { Text = text };
            b.Click += click;
            bar.Buttons.Add(b);
            return b;
        }

        public static ApplicationBarMenuItem Menu(IApplicationBar bar, string text, EventHandler click)
        {
            var m = new ApplicationBarMenuItem(text);
            m.Click += click;
            bar.MenuItems.Add(m);
            return m;
        }

        /// <summary>Shows or hides the system tray progress bar with a short text.</summary>
        public static void Busy(PhoneApplicationPage page, string text)
        {
            var pi = SystemTray.GetProgressIndicator(page);
            if (pi == null)
            {
                pi = new ProgressIndicator();
                SystemTray.SetProgressIndicator(page, pi);
            }
            pi.IsIndeterminate = text != null;
            pi.IsVisible = text != null;
            pi.Text = text ?? "";
        }

        public static string Describe(Exception e)
        {
            var m = e as MailException;
            if (m == null) return e.Message;
            switch (m.Code)
            {
                case "AUTH": return "Login failed: " + m.Message;
                case "NET": return "Can't connect: " + m.Message;
                default: return m.Message;
            }
        }

        public static void Error(Exception e, string title = "Retrobird")
        {
            MessageBox.Show(Describe(e), title, MessageBoxButton.OK);
        }

        public static bool Confirm(string text, string title)
        {
            return MessageBox.Show(text, title, MessageBoxButton.OKCancel) == MessageBoxResult.OK;
        }
    }
}
