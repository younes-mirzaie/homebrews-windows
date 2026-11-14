// Adding or editing an account. Server settings come from the address (Providers.cs) unless
// changed by hand. Saving logs in to both servers first; a self-signed certificate can be
// trusted by its fingerprint.
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Retrobird.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;

namespace Retrobird
{
    public sealed partial class AccountEditPage : RbPage
    {
        static readonly string[] Secs = { "tls", "starttls", "plain" };
        string _id;
        string _imapSec = "tls", _smtpSec = "tls", _imapPin, _smtpPin;
        bool _manual, _filling, _saving, _loaded;
        Provider _provider;
        Provider _chosen;   // picked on the provider page: its servers apply whatever the domain
        AppBarButton _save;

        public AccountEditPage()
        {
            try { InitializeComponent(); }
            catch (Exception ex)
            {
                InAppHelper.Log("account page didn't build: " + ex);
                throw;
            }
            Ui.Scope(EmailBox, InputScopeNameValue.EmailSmtpAddress);
            Ui.Scope(NameBox, InputScopeNameValue.Default);   // no PersonalFullName on WP 8.1
            Ui.Scope(ImapHostBox, InputScopeNameValue.Url);
            Ui.Scope(ImapPortBox, InputScopeNameValue.Number);
            Ui.Scope(UserBox, InputScopeNameValue.EmailSmtpAddress);
            Ui.Scope(SmtpHostBox, InputScopeNameValue.Url);
            Ui.Scope(SmtpPortBox, InputScopeNameValue.Number);
            foreach (var box in new[] { ImapSecBox, SmtpSecBox })
                foreach (var sec in Secs) box.Items.Add(SecLabel(sec));
            _save = Ui.Button(this, Symbol.Accept, "save", async (s, e) => await Save());
            ShowSec();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (_loaded) return;
            _loaded = true;
            string chosen;
            if (Query.TryGetValue("provider", out chosen)) _chosen = Provider.ByName(chosen);
            if (!Query.TryGetValue("id", out _id))
            {
                _id = null;
                if (_chosen != null)
                {
                    PassBox.Header = "app password";
                    _provider = _chosen;
                    ShowProvider(_chosen);
                }
                return;
            }
            TitleText.Text = "edit account";
            PassHint.Visibility = Visibility.Visible;
            Ui.Menu(this, "delete account", async (s, ev) => await Delete());
            Busy("loading");
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
                if (g("has_pass") != "1") PassHint.Text = "The saved password can't be used any more. Enter it again.";
                _filling = false;
                _provider = Provider.ForEmail(EmailBox.Text);
                ShowProvider(_provider);
                ShowSec();
                ShowPins();
            }
            catch (Exception ex) { await Ui.ErrorAsync(ex); }
            finally { Busy(null); }
        }

        void ShowProvider(Provider p)
        {
            bool known = p != null && !string.IsNullOrEmpty(p.Name);
            ProviderText.Text = known ? p.Name : "";
            ProviderText.Visibility = known ? Visibility.Visible : Visibility.Collapsed;
            NoteText.Text = known && !string.IsNullOrEmpty(p.Note) ? p.Note : "";
            NoteText.Visibility = NoteText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            AppPasswordButton.Visibility = known && !string.IsNullOrEmpty(p.AppPasswordUrl) ? Visibility.Visible : Visibility.Collapsed;
        }

        async void AppPasswordButton_Click(object sender, RoutedEventArgs e)
        {
            if (_provider == null || string.IsNullOrEmpty(_provider.AppPasswordUrl)) return;
            await Windows.System.Launcher.LaunchUriAsync(new Uri(_provider.AppPasswordUrl));
        }

        void EmailBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_filling) return;
            // a service picked from the list wins (e.g. Gmail with your own domain); else guess from the address
            var guess = Provider.ForEmail(EmailBox.Text);
            var p = _chosen != null && (guess == null || string.IsNullOrEmpty(guess.Name)) ? _chosen : guess;
            if (p == null && _chosen != null) p = _chosen;
            _provider = p;
            ShowProvider(p);
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
            _filling = true;
            ImapSecBox.SelectedIndex = Math.Max(0, Array.IndexOf(Secs, _imapSec));
            SmtpSecBox.SelectedIndex = Math.Max(0, Array.IndexOf(Secs, _smtpSec));
            _filling = false;
        }

        void Sec_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_filling) return;
            var box = (ComboBox)sender;
            if (box.SelectedIndex < 0) return;
            var sec = Secs[box.SelectedIndex];
            _manual = true;
            _filling = true;
            // follow the usual port unless one was typed
            if (box == ImapSecBox)
            {
                _imapSec = sec;
                if (ImapPortBox.Text == "993" || ImapPortBox.Text == "143" || ImapPortBox.Text == "")
                    ImapPortBox.Text = sec == "tls" ? "993" : "143";
            }
            else
            {
                _smtpSec = sec;
                if (SmtpPortBox.Text == "465" || SmtpPortBox.Text == "587" || SmtpPortBox.Text == "25" || SmtpPortBox.Text == "")
                    SmtpPortBox.Text = sec == "tls" ? "465" : "587";
            }
            _filling = false;
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

        async Task Save()
        {
            if (_saving) return;
            var email = EmailBox.Text.Trim();
            if (email.IndexOf('@') <= 0)
            {
                await Ui.AlertAsync("Enter your email address. It looks like this: name@example.com", "account");
                return;
            }
            if (_id == null && PassBox.Password.Length == 0)
            {
                await Ui.AlertAsync("Enter the app password your mail service gave you (or your password).", "account");
                return;
            }
            if ((_imapSec == "plain" || _smtpSec == "plain") &&
                !await Ui.ConfirmAsync("Without encryption, your password and mail can be read by anyone on the network. Continue?", "no security"))
                return;
            _saving = true;
            _save.IsEnabled = false;
            Busy("signing in");
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
                            AppState.AccountAdded = true;
                            foreach (var a in AppState.Accounts) if (a.Id == id) AppState.SelectAccount(a);
                        }
                        AppState.ListDirty = true;
                        GoBack();
                        return;
                    }
                    catch (MailException ex)
                    {
                        if (ex.Code != "CERT" || string.IsNullOrEmpty(ex.Fingerprint)) throw;
                        var which = ex.Side == "smtp" ? "outgoing (SMTP)" : "incoming (IMAP)";
                        if (!await Ui.ConfirmAsync("The " + which + " server's certificate can't be verified: " + ex.Message +
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
                await Ui.AlertAsync(msg, "couldn't sign in");
                AdvancedPanel.Visibility = Visibility.Visible;
            }
            finally
            {
                _saving = false;
                _save.IsEnabled = true;
                Busy(null);
            }
        }

        async Task Delete()
        {
            if (_id == null) return;
            if (!await Ui.ConfirmAsync("Remove " + EmailBox.Text + " from this phone? Your mail stays on the server.", "delete account")) return;
            try
            {
                await AppState.Client.DeleteAccountAsync(_id);
                AppState.Accounts = await AppState.Client.GetAccountsAsync();
                if (AppState.CurrentAccountId == _id) AppState.SelectAccount(AppState.Accounts.Count > 0 ? AppState.Accounts[0] : null);
                GoBack();
            }
            catch (Exception ex) { await Ui.ErrorAsync(ex); }
        }
    }
}
