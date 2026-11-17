// Content dialogs, one at a time (Windows allows only one open ContentDialog). Buttons carry the
// specific verb, the action on the left and Cancel on the right, as the Windows 10 guidelines ask.
using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.UI.Xaml.Controls;

namespace Unesbrews
{
    public static class Dialogs
    {
        static readonly SemaphoreSlim _one = new SemaphoreSlim(1);

        /// <summary>A question: true when the user picked the action.</summary>
        public static async Task<bool> AskAsync(string title, string message, string action, string cancel = "Cancel")
        {
            await _one.WaitAsync();
            try
            {
                var d = new ContentDialog
                {
                    Title = title,
                    Content = Body(message),
                    PrimaryButtonText = action,
                    SecondaryButtonText = cancel,
                };
                return await d.ShowAsync() == ContentDialogResult.Primary;
            }
            finally { _one.Release(); }
        }

        /// <summary>A message with a single Close button.</summary>
        public static async Task TellAsync(string title, string message)
        {
            await _one.WaitAsync();
            try
            {
                var d = new ContentDialog { Title = title, Content = Body(message), SecondaryButtonText = "Close" };
                await d.ShowAsync();
            }
            finally { _one.Release(); }
        }

        static ScrollViewer Body(string text)
        {
            return new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new TextBlock { Text = text ?? "", TextWrapping = Windows.UI.Xaml.TextWrapping.Wrap, IsTextSelectionEnabled = true },
            };
        }
    }
}
