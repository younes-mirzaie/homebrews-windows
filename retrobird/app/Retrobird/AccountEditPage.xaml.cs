// Adding or editing an account. Server settings come from the address (Providers.cs) unless
// changed by hand. Saving logs in to both servers first; a self-signed certificate can be
// trusted by its fingerprint.
using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using Microsoft.Phone.Controls;
using Microsoft.Phone.Shell;
using Retrobird.Core;

namespace Retrobird
{
    public partial class AccountEditPage : PhoneApplicationPage
    {
        static readonly string[] Secs = { "tls", "starttls", "plain" };
        string _id;
        string _imapSec = "tls", _smtpSec = "tls", _imapPin, _smtpPin;
        bool _manual, _filling, _saving, _loaded;
        Provider _provider;
        ApplicationBarIconButton _save;

        public AccountEditPage()
        {
            InitializeComponent();
            ApplicationBar = new ApplicationBar();
            _save = Ui.Button(ApplicationBar, "check", "save", async (s, e) => await Save());
            ShowSec();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (_loaded) return;
            _loaded = true;
            if (!NavigationContext.QueryString.TryGetValue("id", out _id)) { _id = null; return; }
            TitleText.Text = "edit account";
            PassHint.Visibility = Visibility.Visible;
            Ui.Menu(ApplicationBar, "delete this account", async (s, ev) => await Delete());
            Ui.Busy(this, "loading");
            try
            {
                var d = await AppState.Client.GetAccountAsync(_id);
                Func<string, string> g = k => d.ContainsKey(k) ? d[k] : "";
                _filling = true;
                _manual = true;   // keep the stored servers even if the address looks like a known provider
                EmailBox.Text = g("email");
                NameBox.Text = g("name");
                ImapHostBox.Text = g("imap_host");
                ImapPortBox.Text = g("imap_port");
                SmtpHostBox.Text = g("smtp_host");
                SmtpPortBox.Text = g("smtp_port");
                UserBox.Text = g("user");
                _imapSec = g("imap_sec");
                _smtpSec = g("smtp_sec");
                _imapPin = g("imap_pin");
                _smtpPin = g("smtp_pin");
                SaveSentBox.IsChecked = g("save_sent") != "0";
                if (g("has_pass") != "1") PassHint.Text = "the saved password can't be used any more: enter it again";
                _filling = false;
                ShowSec();
                ShowPins();
            }
            catch (Exception ex) { Ui.Error(ex); }
            finally { Ui.Busy(this, null); }
        }

        void EmailBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_filling) return;
            var p = Provider.ForEmail(EmailBox.Text);
            _provider = p;
            if (p != null && !string.IsNullOrEmpty(p.Name))
            {
                ProviderText.Text = p.Name + (string.IsNullOrEmpty(p.Note) ? "" : ": " + p.Note);
                ProviderText.Visibility = Visibility.Visible;
            }
            else ProviderText.Visibility = Visibility.Collapsed;
            if (_manual || p == null) return;
            _filling = true;
            ImapHostBox.Text = p.ImapHost;
            ImapPortBox.Text = p.ImapPort.ToString();
            SmtpHostBox.Text = p.SmtpHost;
            SmtpPortBox.Text = p.SmtpPort.ToString();
            _imapSec = p.ImapSec;
            _smtpSec = p.SmtpSec;
            UserBox.Text = EmailBox.Text.Trim();
            SaveSentBox.IsChecked = p.SaveSent;
            _filling = false;
            ShowSec();
        }

        void Server_Changed(object sender, TextChangedEventArgs e)
        {
            if (!_filling) _manual = true;
        }

        static string SecLabel(string s)
        {
            return s == "starttls" ? "STARTTLS" : s == "plain" ? "none (unsafe)" : "SSL/TLS";
        }

        void ShowSec()
        {
            ImapSecButton.Content = SecLabel(_imapSec);
            SmtpSecButton.Content = SecLabel(_smtpSec);
        }

        static string Next(string s) { return Secs[(Array.IndexOf(Secs, s) + 1) % Secs.Length]; }

        void ImapSecButton_Click(object sender, RoutedEventArgs e)
        {
            _manual = true;
            _imapSec = Next(_imapSec);
            // follow the usual port unless one was typed
            if (ImapPortBox.Text == "993" || ImapPortBox.Text == "143" || ImapPortBox.Text == "")
                ImapPortBox.Text = _imapSec == "tls" ? "993" : "143";
            ShowSec();
        }

        void SmtpSecButton_Click(object sender, RoutedEventArgs e)
        {
            _manual = true;
            _smtpSec = Next(_smtpSec);
            if (SmtpPortBox.Text == "465" || SmtpPortBox.Text == "587" || SmtpPortBox.Text == "25" || SmtpPortBox.Text == "")
                SmtpPortBox.Text = _smtpSec == "tls" ? "465" : "587";
            ShowSec();
        }

        void AdvancedButton_Click(object sender, RoutedEventArgs e)
        {
            AdvancedPanel.Visibility = AdvancedPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        }

        static string Pretty(string fp)
        {
            var b = new StringBuilder();
            for (int i = 0; i + 1 < fp.Length; i += 2)
            {
                if (i > 0) b.Append(i % 16 == 0 ? "\n" : ":");
                b.Append(fp.Substring(i, 2).ToUpperInvariant());
            }
            return b.ToString();
        }

        void ShowPins()
        {
            var t = "";
            if (!string.IsNullOrEmpty(_imapPin)) t += "trusted IMAP certificate (SHA-256):\n" + Pretty(_imapPin) + "\n";
            if (!string.IsNullOrEmpty(_smtpPin)) t += "trusted SMTP certificate (SHA-256):\n" + Pretty(_smtpPin);
            PinsText.Text = t.Trim();
            PinsText.Visibility = t.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            ForgetPinsButton.Visibility = PinsText.Visibility;
        }

        void ForgetPinsButton_Click(object sender, RoutedEventArgs e)
        {
            _imapPin = "";
            _smtpPin = "";
            ShowPins();
        }

        Dictionary<string, string> Fields()
        {
            var email = EmailBox.Text.Trim();
            if (string.IsNullOrEmpty(ImapHostBox.Text) && _provider != null) EmailBox_TextChanged(null, null);
            return new Dictionary<string, string>
            {
                { "name", NameBox.Text.Trim() },
                { "email", email },
                { "user", string.IsNullOrEmpty(UserBox.Text.Trim()) ? email : UserBox.Text.Trim() },
                { "pass", PassBox.Password },
                { "imap_host", ImapHostBox.Text.Trim() },
                { "imap_port", ImapPortBox.Text.Trim() },
                { "imap_sec", _imapSec },
                { "smtp_host", SmtpHostBox.Text.Trim() },
                { "smtp_port", SmtpPortBox.Text.Trim() },
                { "smtp_sec", _smtpSec },
                { "imap_pin", _imapPin ?? "" },
                { "smtp_pin", _smtpPin ?? "" },
                { "save_sent", SaveSentBox.IsChecked == true ? "1" : "0" },
            };
        }

        async System.Threading.Tasks.Task Save()
        {
            if (_saving) return;
            var email = EmailBox.Text.Trim();
            if (email.IndexOf('@') <= 0)
            {
                MessageBox.Show("Enter your email address.", "account", MessageBoxButton.OK);
                return;
            }
            if (_id == null && PassBox.Password.Length == 0)
            {
                MessageBox.Show("Enter the password (or the app password your mail service gave you).", "account", MessageBoxButton.OK);
                return;
            }
            if ((_imapSec == "plain" || _smtpSec == "plain") &&
                !Ui.Confirm("Without encryption your password and mail travel readable by anyone on the network. Continue?", "no security"))
                return;
            _saving = true;
            _save.IsEnabled = false;
            Ui.Busy(this, "signing in");
            try
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        var id = await AppState.Client.SaveAccountAsync(_id, Fields());
                        AppState.Accounts = await AppState.Client.GetAccountsAsync();
                        if (_id == null)
                        {
                            foreach (var a in AppState.Accounts) if (a.Id == id) AppState.SelectAccount(a);
                        }
                        AppState.ListDirty = true;
                        if (NavigationService.CanGoBack) NavigationService.GoBack();
                        return;
                    }
                    catch (MailException ex)
                    {
                        if (ex.Code != "CERT" || string.IsNullOrEmpty(ex.Fingerprint)) throw;
                        var which = ex.Side == "smtp" ? "outgoing (SMTP)" : "incoming (IMAP)";
                        if (!Ui.Confirm("The " + which + " server's certificate can't be verified: " + ex.Message +
                                        "\n\nIf this is your own or your provider's server and you expect this, you can trust exactly this certificate:\n" +
                                        Pretty(ex.Fingerprint), "certificate"))
                            return;
                        if (ex.Side == "smtp") _smtpPin = ex.Fingerprint;
                        else _imapPin = ex.Fingerprint;
                        ShowPins();
                    }
                }
            }
            catch (Exception ex)
            {
                var msg = Ui.Describe(ex);
                if (_provider != null && !string.IsNullOrEmpty(_provider.Note)) msg += "\n\n" + _provider.Note;
                MessageBox.Show(msg, "couldn't sign in", MessageBoxButton.OK);
                AdvancedPanel.Visibility = Visibility.Visible;
            }
            finally
            {
                _saving = false;
                _save.IsEnabled = true;
                Ui.Busy(this, null);
            }
        }

        async System.Threading.Tasks.Task Delete()
        {
            if (_id == null) return;
            if (!Ui.Confirm("Remove " + EmailBox.Text + " from this phone? Mail on the server stays.", "delete account")) return;
            try
            {
                await AppState.Client.DeleteAccountAsync(_id);
                AppState.Accounts = await AppState.Client.GetAccountsAsync();
                if (AppState.CurrentAccountId == _id) AppState.SelectAccount(AppState.Accounts.Count > 0 ? AppState.Accounts[0] : null);
                if (NavigationService.CanGoBack) NavigationService.GoBack();
            }
            catch (Exception ex) { Ui.Error(ex); }
        }
    }
}
