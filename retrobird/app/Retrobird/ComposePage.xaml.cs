// Writing a message: new, reply, reply all or forward (AppState.PendingDraft fills it in).
using System;
using System.Threading.Tasks;
using Retrobird.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace Retrobird
{
    public sealed partial class ComposePage : RbPage
    {
        Draft _draft;
        bool _sending, _sent;
        AppBarButton _send;

        public ComposePage()
        {
            InitializeComponent();
            _send = Ui.Button(this, Symbol.Send, "send", async (s, e) => await Send());
            Ui.Button(this, Symbol.People, "cc/bcc", (s, e) => CcPanel.Visibility = Visibility.Visible);
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
            Loaded += (s, a) =>
            {
                if (ToBox.Text.Length == 0) ToBox.Focus(FocusState.Programmatic);
                else { BodyBox.Focus(FocusState.Programmatic); BodyBox.Select(0, 0); }   // replies: type above the quote
            };
        }

        // the text box keeps line breaks as \r
        static string N(string s) { return (s ?? "").Replace("\r\n", "\n").Replace('\r', '\n'); }

        bool Changed
        {
            get
            {
                return N(ToBox.Text) != N(_draft.To) || N(CcBox.Text) != N(_draft.Cc) || N(BccBox.Text) != N(_draft.Bcc) ||
                       N(SubjectBox.Text) != N(_draft.Subject) || N(BodyBox.Text) != N(_draft.Body);
            }
        }

        public override bool HandleBack()
        {
            if (_sending) return true;
            if (_sent || !Changed) return false;
            AskDiscard();
            return true;
        }

        async void AskDiscard()
        {
            if (await Ui.ConfirmAsync("Discard this message?", "new message"))
            {
                _sent = true;   // nothing left to keep
                GoBack();
            }
        }

        async Task Send()
        {
            var acct = AppState.CurrentAccount;
            if (acct == null || _sending) return;
            if (ToBox.Text.Trim().Length == 0 && CcBox.Text.Trim().Length == 0 && BccBox.Text.Trim().Length == 0)
            {
                await Ui.AlertAsync("Add at least one person to send it to.", "new message");
                return;
            }
            if (SubjectBox.Text.Trim().Length == 0 && !await Ui.ConfirmAsync("Send without a subject?", "new message")) return;
            var d = new Draft
            {
                To = ToBox.Text, Cc = CcBox.Text, Bcc = BccBox.Text, Subject = SubjectBox.Text,
                Body = BodyBox.Text.Replace("\r\n", "\n").Replace('\r', '\n'),
                InReplyTo = _draft.InReplyTo, References = _draft.References,
                ReplyFolder = _draft.ReplyFolder, ReplyUid = _draft.ReplyUid
            };
            _sending = true;
            _send.IsEnabled = false;
            Busy("sending");
            try
            {
                var warning = await AppState.Client.SendAsync(acct.Id, d);
                _sent = true;
                AppState.ListDirty = true;
                if (!string.IsNullOrEmpty(warning)) await Ui.AlertAsync(warning, "sent");
                GoBack();
            }
            catch (Exception ex) { await Ui.ErrorAsync(ex, "not sent"); }
            finally
            {
                _sending = false;
                _send.IsEnabled = true;
                Busy(null);
            }
        }
    }
}
