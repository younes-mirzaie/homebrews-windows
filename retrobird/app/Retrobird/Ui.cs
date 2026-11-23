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

        sealed class PickItem
        {
            public string Label;
            public object Value;
            public override string ToString() { return Label; }
        }

        /// <summary>The phone's full-screen list picker; returns the chosen value, or null.</summary>
        public static async Task<object> PickAsync(FrameworkElement at, string title, System.Collections.Generic.IEnumerable<Tuple<string, object>> items)
        {
            var list = new System.Collections.Generic.List<PickItem>();
            foreach (var i in items) list.Add(new PickItem { Label = i.Item1, Value = i.Item2 });
            var f = new ListPickerFlyout { ItemsSource = list, SelectionMode = ListPickerFlyoutSelectionMode.Single };
            Windows.UI.Xaml.Controls.Primitives.PickerFlyoutBase.SetTitle(f, title.ToUpperInvariant());
            var picked = await f.ShowAtAsync(at);
            var p = picked != null && picked.Count > 0 ? picked[0] as PickItem : null;
            return p == null ? null : p.Value;
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

        /// <summary>Words set in code for a title, label or button. The phone keeps them lowercase
        /// (Metro); the Windows 10 app (Core10\Ui.cs) shows them in sentence case.</summary>
        public static string Text(string s) { return s; }

        /// <summary>The small title above a page title ("INBOX · GMAIL"): all caps on the phone,
        /// as written on Windows 10.</summary>
        public static string Caps(string s) { return (s ?? "").ToUpperInvariant(); }

        /// <summary>A failure in plain words (Core/Inbox.cs, shared with Windows 10).</summary>
        public static string Describe(Exception e) { return Inbox.Describe(e); }

        // only one message box can be open at a time
        static readonly SemaphoreSlim _dialog = new SemaphoreSlim(1);

        static string _shownText;   // the box on screen or waiting: the same one isn't queued twice

        static async Task<bool> ShowAsync(string text, string title, string ok, string cancel)
        {
            // The phone allows one message box at a time (a second fails with access denied), so
            // they wait their turn.
            if (cancel == null && text == _shownText) return false;
            await _dialog.WaitAsync();
            _shownText = text;
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
            catch (Exception e)
            {
                InAppHelper.Log("message box \"" + title + "\" failed: " + e);
                return false;
            }
            finally
            {
                _shownText = null;
                _dialog.Release();
            }
        }

        public static Task AlertAsync(string text, string title) { return ShowAsync(text, title, "ok", null); }

        public static Task<bool> ConfirmAsync(string text, string title) { return ShowAsync(text, title, "ok", "cancel"); }

        public static Task ErrorAsync(Exception e, string title = "Retrobird") { return AlertAsync(Describe(e), title); }
    }
}
