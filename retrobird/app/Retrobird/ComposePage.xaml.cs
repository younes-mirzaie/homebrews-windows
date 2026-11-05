// Writing a message: new, reply, reply all or forward (AppState.PendingDraft fills it in).
using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Navigation;
using Microsoft.Phone.Controls;
using Microsoft.Phone.Shell;
using Retrobird.Core;

namespace Retrobird
{
    public partial class ComposePage : PhoneApplicationPage
    {
        Draft _draft;
        bool _sending, _sent;
        ApplicationBarIconButton _send;

        public ComposePage()
        {
            InitializeComponent();
            ApplicationBar = new ApplicationBar();
            _send = Ui.Button(ApplicationBar, "send", "send", async (s, e) => await Send());
            Ui.Button(ApplicationBar, "cc", "cc/bcc", (s, e) => CcPanel.Visibility = Visibility.Visible);
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (_draft != null) return;   // coming back from somewhere: keep what's typed
            _draft = AppState.PendingDraft ?? new Draft();
            AppState.PendingDraft = null;
            var acct = AppState.CurrentAccount;
            FromText.Text = acct == null ? "NEW MESSAGE" : "FROM " + acct.Email.ToUpperInvariant();
            ToBox.Text = _draft.To ?? "";
            CcBox.Text = _draft.Cc ?? "";
            BccBox.Text = _draft.Bcc ?? "";
            if (CcBox.Text.Length > 0 || BccBox.Text.Length > 0) CcPanel.Visibility = Visibility.Visible;
            SubjectBox.Text = _draft.Subject ?? "";
            BodyBox.Text = _draft.Body ?? "";
            Dispatcher.BeginInvoke(() =>
            {
                if (ToBox.Text.Length == 0) ToBox.Focus();
                else { BodyBox.Focus(); BodyBox.Select(0, 0); }   // replies: type above the quote
            });
        }

        bool Changed
        {
            get
            {
                return ToBox.Text != (_draft.To ?? "") || CcBox.Text != (_draft.Cc ?? "") || BccBox.Text != (_draft.Bcc ?? "") ||
                       SubjectBox.Text != (_draft.Subject ?? "") || BodyBox.Text != (_draft.Body ?? "");
            }
        }

        protected override void OnBackKeyPress(CancelEventArgs e)
        {
            base.OnBackKeyPress(e);
            if (_sending) { e.Cancel = true; return; }
            if (!_sent && Changed && !Ui.Confirm("Discard this message?", "new message")) e.Cancel = true;
        }

        async System.Threading.Tasks.Task Send()
        {
            var acct = AppState.CurrentAccount;
            if (acct == null || _sending) return;
            if (ToBox.Text.Trim().Length == 0 && CcBox.Text.Trim().Length == 0 && BccBox.Text.Trim().Length == 0)
            {
                MessageBox.Show("Add at least one recipient.", "new message", MessageBoxButton.OK);
                return;
            }
            if (SubjectBox.Text.Trim().Length == 0 && !Ui.Confirm("Send without a subject?", "new message")) return;
            var d = new Draft
            {
                To = ToBox.Text, Cc = CcBox.Text, Bcc = BccBox.Text, Subject = SubjectBox.Text,
                Body = BodyBox.Text.Replace("\r\n", "\n").Replace('\r', '\n'),
                InReplyTo = _draft.InReplyTo, References = _draft.References,
                ReplyFolder = _draft.ReplyFolder, ReplyUid = _draft.ReplyUid
            };
            _sending = true;
            _send.IsEnabled = false;
            Ui.Busy(this, "sending");
            try
            {
                var warning = await AppState.Client.SendAsync(acct.Id, d);
                _sent = true;
                AppState.ListDirty = true;
                if (!string.IsNullOrEmpty(warning)) MessageBox.Show(warning, "sent", MessageBoxButton.OK);
                if (NavigationService.CanGoBack) NavigationService.GoBack();
            }
            catch (Exception ex) { Ui.Error(ex, "not sent"); }
            finally
            {
                _sending = false;
                _send.IsEnabled = true;
                Ui.Busy(this, null);
            }
        }
    }
}
