using System;
using System.Windows.Navigation;
using Microsoft.Phone.Controls;
using AppLoader.Core;

namespace AppLoader
{
    /// <summary>The technical details (installer exit codes, download replies) behind the plain messages.</summary>
    public partial class LogPage : PhoneApplicationPage
    {
        readonly Action _changed;
        bool _leaving;

        public LogPage()
        {
            InitializeComponent();
            AppTitle.Text = AppInfo.Name.ToUpperInvariant();
            _changed = () => Dispatcher.BeginInvoke(() => LogText.Text = AppLog.Text);
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            LogText.Text = AppLog.Text;
            AppLog.Changed += _changed;
            Turnstile.FeatherIn(LayoutRoot, e.NavigationMode == NavigationMode.Back);
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            AppLog.Changed -= _changed;
            base.OnNavigatedFrom(e);
        }

        protected override void OnBackKeyPress(System.ComponentModel.CancelEventArgs e)
        {
            base.OnBackKeyPress(e);
            if (e.Cancel || !NavigationService.CanGoBack) return;
            e.Cancel = true;
            if (_leaving) return;
            _leaving = true;
            Turnstile.FeatherOut(LayoutRoot, true, () => NavigationService.GoBack());
        }
    }
}
