using System;
using AppLoader.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace Unesbrews.Pages
{
    public sealed partial class LogPage : Page, ITitled
    {
        public string Title { get { return "Activity log"; } }

        public LogPage() { InitializeComponent(); }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            LogText.Text = AppLog.Text;
            AppLog.Changed += Changed;
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            AppLog.Changed -= Changed;
            base.OnNavigatedFrom(e);
        }

        // AppLog can change from a background thread
        void Changed() { var t = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => LogText.Text = AppLog.Text); }

        void Copy_Click(object sender, RoutedEventArgs e)
        {
            var data = new DataPackage();
            data.SetText(AppLog.Text);
            Clipboard.SetContent(data);
        }
    }
}
