// Small UI helpers shared by the pages: app bar commands, message boxes, error text.
using System;
using System.Threading;
using System.Threading.Tasks;
using Retrobird.Core;
using Windows.UI.Popups;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Retrobird
{
    public static class Ui
    {
        /// <summary>The page's app bar, made on first use (system chrome color, opacity 1).</summary>
        public static CommandBar Bar(Page page)
        {
            var bar = page.BottomAppBar as CommandBar;
            if (bar == null)
            {
                bar = new CommandBar();
                page.BottomAppBar = bar;
            }
            return bar;
        }

        /// <summary>An icon button (at most four per page; label lowercase).</summary>
        public static AppBarButton Button(Page page, Symbol icon, string label, RoutedEventHandler click)
        {
            var b = new AppBarButton { Icon = new SymbolIcon(icon), Label = label };
            b.Click += click;
            Bar(page).PrimaryCommands.Add(b);
            return b;
        }

        /// <summary>A menu item (at most five per page, 14 to 20 characters).</summary>
        public static AppBarButton Menu(Page page, string label, RoutedEventHandler click)
        {
            var m = new AppBarButton { Label = label };
            m.Click += click;
            Bar(page).SecondaryCommands.Add(m);
            return m;
        }

        /// <summary>The keyboard for a text box (set in code: older XAML parsers reject the short form).</summary>
        public static void Scope(TextBox box, Windows.UI.Xaml.Input.InputScopeNameValue name)
        {
            var scope = new Windows.UI.Xaml.Input.InputScope();
            scope.Names.Add(new Windows.UI.Xaml.Input.InputScopeName { NameValue = name });
            box.InputScope = scope;
        }

        public static string Describe(Exception e)
        {
            var m = e as MailException;
            if (m == null) return e.Message;
            switch (m.Code)
            {
                case "AUTH": return "Couldn't sign in: " + m.Message;
                case "NET": return "Can't connect: " + m.Message;
                default: return m.Message;
            }
        }

        // only one message box can be open at a time
        static readonly SemaphoreSlim _dialog = new SemaphoreSlim(1);

        static async Task<bool> ShowAsync(string text, string title, string ok, string cancel)
        {
            await _dialog.WaitAsync();
            try
            {
                var d = new MessageDialog(text ?? "", title ?? "");
                var yes = new UICommand(ok);
                d.Commands.Add(yes);
                if (cancel != null) d.Commands.Add(new UICommand(cancel));
                d.DefaultCommandIndex = 0;
                d.CancelCommandIndex = (uint)(cancel != null ? 1 : 0);
                var r = await d.ShowAsync();
                return r == yes;
            }
            catch (Exception) { return false; }
            finally { _dialog.Release(); }
        }

        public static Task AlertAsync(string text, string title) { return ShowAsync(text, title, "ok", null); }

        public static Task<bool> ConfirmAsync(string text, string title) { return ShowAsync(text, title, "ok", "cancel"); }

        public static Task ErrorAsync(Exception e, string title = "Retrobird") { return AlertAsync(Describe(e), title); }
    }
}
