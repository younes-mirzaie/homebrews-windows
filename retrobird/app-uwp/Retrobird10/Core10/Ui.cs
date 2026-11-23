// Small UI helpers for the pages, with the same calls as the Windows Phone 8.1 app's Ui.cs so the
// shared page code runs on both. Windows 10 differences: commands go on the page's own CommandBar,
// labels and titles are in sentence case, pickers are menus, and messages are content dialogs.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Retrobird.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;

namespace Retrobird
{
    public static class Ui
    {
        /// <summary>The page's command bar (the CommandBar named "Bar" in its XAML).</summary>
        public static CommandBar Bar(Page page)
        {
            var bar = page.FindName("Bar") as CommandBar;
            if (bar == null) throw new InvalidOperationException(page.GetType().Name + " has no command bar");
            return bar;
        }

        /// <summary>A command with an icon (at most four on a phone).</summary>
        public static AppBarButton Button(Page page, Symbol icon, string label, RoutedEventHandler click)
        {
            var b = new AppBarButton { Icon = new SymbolIcon(icon), Label = Text(label) };
            ToolTipService.SetToolTip(b, b.Label);
            if (click != null) b.Click += click;
            Bar(page).PrimaryCommands.Add(b);
            return b;
        }

        /// <summary>A text-only command in the "More" menu.</summary>
        public static AppBarButton Menu(Page page, string label, RoutedEventHandler click)
        {
            var m = new AppBarButton { Label = Text(label) };
            m.Click += click;
            Bar(page).SecondaryCommands.Add(m);
            return m;
        }

        /// <summary>Words set in code for a title, label or button: sentence case on Windows 10
        /// ("sign in again" becomes "Sign in again").</summary>
        public static string Text(string s) { return Format.Sentence(s); }

        /// <summary>The small title above a page title: as written on Windows 10.</summary>
        public static string Caps(string s) { return s ?? ""; }

        /// <summary>A short menu of choices next to an element; returns the chosen value, or null.</summary>
        public static Task<object> PickAsync(FrameworkElement at, string title, IEnumerable<Tuple<string, object>> items)
        {
            var done = new TaskCompletionSource<object>();
            var menu = new MenuFlyout();
            foreach (var i in items)
            {
                var value = i.Item2;
                var item = new MenuFlyoutItem { Text = i.Item1 };
                item.Click += (s, e) => done.TrySetResult(value);
                menu.Items.Add(item);
            }
            if (menu.Items.Count == 0) return Task.FromResult<object>(null);
            menu.Closed += (s, e) => done.TrySetResult(null);
            menu.Placement = FlyoutPlacementMode.Bottom;
            menu.ShowAt(at);
            return done.Task;
        }

        /// <summary>The keyboard for a text box.</summary>
        public static void Scope(TextBox box, Windows.UI.Xaml.Input.InputScopeNameValue name)
        {
            var scope = new Windows.UI.Xaml.Input.InputScope();
            scope.Names.Add(new Windows.UI.Xaml.Input.InputScopeName { NameValue = name });
            box.InputScope = scope;
        }

        /// <summary>A failure in plain words (Core/Inbox.cs, shared with the phone app).</summary>
        public static string Describe(Exception e) { return Inbox.Describe(e); }

        // Windows allows one content dialog at a time
        static readonly SemaphoreSlim _dialog = new SemaphoreSlim(1);
        static string _shownText;

        static async Task<bool> ShowAsync(string text, string title, string ok, string cancel)
        {
            if (cancel == null && text == _shownText) return false;   // the same box isn't queued twice
            await _dialog.WaitAsync();
            _shownText = text;
            try
            {
                var d = new ContentDialog
                {
                    Title = Format.Sentence(title ?? ""),
                    Content = new ScrollViewer
                    {
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        Content = new TextBlock { Text = text ?? "", TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
                    },
                    PrimaryButtonText = ok,
                };
                if (cancel != null) d.SecondaryButtonText = cancel;
                return await d.ShowAsync() == ContentDialogResult.Primary;
            }
            catch (Exception e)
            {
                InAppHelper.Log("dialog \"" + title + "\" failed: " + e);
                return false;
            }
            finally
            {
                _shownText = null;
                _dialog.Release();
            }
        }

        public static Task AlertAsync(string text, string title) { return ShowAsync(text, title, "OK", null); }

        public static Task<bool> ConfirmAsync(string text, string title) { return ShowAsync(text, title, "OK", "Cancel"); }

        public static Task ErrorAsync(Exception e, string title = "Retrobird") { return AlertAsync(Describe(e), title); }
    }
}
