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
        // Microsoft (Outlook.com) sign-in
        bool _ms, _msStored, _signedIn;
        string _refresh;      // from a finished sign-in, saved as the account's token
        OAuthDevice _device;
        int _pollGen;         // bumped to stop a running poll

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
            Ui.Scope(ClientBox, InputScopeNameValue.Default);
            ClientBox.Text = AppState.MicrosoftClientId;
            foreach (var box in new[] { ImapSecBox, SmtpSecBox })
                foreach (var sec in Secs) box.Items.Add(SecLabel(sec));
            _save = Ui.Button(this, Symbol.Accept, "sign in", async (s, e) => await Save());
            Ui.Button(this, Symbol.Cancel, "cancel", (s, e) => GoBack());
            Ui.Menu(this, "server settings", AdvancedButton_Click);
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
                    _provider = _chosen;
                    ShowProvider(_chosen);
                }
                return;
            }
            TitleText.Text = Ui.Text("account");
            _save.Label = Ui.Text("save");
            PassHint.Visibility = Visibility.Visible;
            Ui.Menu(this, "remove account", async (s, ev) => await Delete());
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
                _msStored = g("auth") == "microsoft";
                if (_msStored)
                {
                    if (g("oauth_client").Length > 0) ClientBox.Text = g("oauth_client");
                    _signedIn = g("has_pass") == "1";
                    MsStatus.Text = _signedIn
                        ? "Signed in with Microsoft. Sign in again only if Retrobird asks you to."
                        : "The Microsoft sign-in has expired. Sign in again.";
                    MsSignInButton.Content = Ui.Text("sign in again");
                }
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
            _ms = _msStored || (p != null && p.Microsoft);
            var ms = _ms ? Visibility.Visible : Visibility.Collapsed;
            var pw = _ms ? Visibility.Collapsed : Visibility.Visible;
            MsPanel.Visibility = ms;
            ClientBox.Visibility = ms;
            PassBox.Visibility = pw;
            AppPassText.Visibility = pw;
            if (_ms) PassHint.Visibility = Visibility.Collapsed;
            else if (_id != null) PassHint.Visibility = Visibility.Visible;
            AppTitleText.Text = Ui.Caps(known ? p.ShortName : "Retrobird");
            if (known && !string.IsNullOrEmpty(p.AppPasswordUrl)) PassBox.Header = "App password";
            NoteText.Text = known && !string.IsNullOrEmpty(p.Note) ? p.Note : "";
            NoteText.Visibility = NoteText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            AppPasswordButton.Visibility = known && !string.IsNullOrEmpty(p.AppPasswordUrl) ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---------------- Microsoft sign-in ----------------

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            _pollGen++;   // stop waiting for a sign-in
        }

        async void MsSignIn_Click(object sender, RoutedEventArgs e)
        {
            var client = ClientBox.Text.Trim();
            if (client.Length == 0)
            {
                AdvancedPanel.Visibility = Visibility.Visible;
                await Ui.AlertAsync("This copy of Retrobird doesn't have a Microsoft app ID yet. Enter it under server settings, in \"Microsoft app ID\".", "Microsoft sign-in");
                return;
            }
            int gen = ++_pollGen;
            MsSignInButton.IsEnabled = false;
            Busy("contacting Microsoft");
            try
            {
                _device = await AppState.Client.OAuthStartAsync(client);
            }
            catch (Exception ex)
            {
                MsStatus.Text = Ui.Describe(ex);
                MsSignInButton.IsEnabled = true;
                return;
            }
            finally { Busy(null); }
            AppState.MicrosoftClientId = client;
            CodeText.Text = _device.UserCode;
            CodeHelp.Text = "Open " + _device.Url + ", sign in with your Microsoft account and enter this code. Then come back here: Retrobird notices by itself.";
            CodePanel.Visibility = Visibility.Visible;
            MsStatus.Text = "Waiting for you to sign in on Microsoft's page.";
            MsSignInButton.Content = Ui.Text("get a new code");
            MsSignInButton.IsEnabled = true;
            await Poll(gen, client, _device);
        }

        async Task Poll(int gen, string client, OAuthDevice d)
        {
            var until = DateTime.UtcNow.AddSeconds(d.ExpiresIn > 0 ? d.ExpiresIn : 900);
            int interval = d.Interval, failures = 0;
            while (gen == _pollGen)
            {
                await Task.Delay(interval * 1000);
                if (gen != _pollGen) return;
                if (DateTime.UtcNow > until)
                {
                    CodePanel.Visibility = Visibility.Collapsed;
                    MsStatus.Text = "The code expired before it was entered. Tap \"sign in with Microsoft\" for a new one.";
                    MsSignInButton.Content = Ui.Text("sign in with Microsoft");
                    return;
                }
                Tuple<string, string> r;
                try
                {
                    r = await AppState.Client.OAuthPollAsync(client, d.DeviceCode);
                    failures = 0;
                }
                catch (MailException ex)
                {
                    // the phone may be offline for a moment while you switch apps: keep waiting
                    if (ex.Code == "NET" && ++failures < 5) { interval = Math.Min(interval * 2, 30); continue; }
                    if (gen != _pollGen) return;
                    CodePanel.Visibility = Visibility.Collapsed;
                    MsStatus.Text = Ui.Describe(ex);
                    MsSignInButton.Content = Ui.Text("sign in with Microsoft");
                    return;
                }
                if (gen != _pollGen) return;
                if (r == null) continue;
                _pollGen++;
                _refresh = r.Item1;
                _signedIn = true;
                CodePanel.Visibility = Visibility.Collapsed;
                MsSignInButton.Content = Ui.Text("sign in again");
                var email = r.Item2 ?? "";
                if (email.Length > 0 && EmailBox.Text.Trim().Length == 0) EmailBox.Text = email;
                var typed = EmailBox.Text.Trim();
                if (email.Length > 0 && !string.Equals(email, typed, StringComparison.OrdinalIgnoreCase))
                {
                    MsStatus.Text = "Microsoft signed you in as " + email + ", not " + typed + ". Change the address above or sign in again with the right account.";
                    return;
                }
                MsStatus.Text = "Signed in" + (email.Length > 0 ? " as " + email : "") + ". Checking your mail servers.";
                await Save();
                return;
            }
        }

        async void OpenRetrofox_Click(object sender, RoutedEventArgs e)
        {
            if (_device == null) return;
            bool ok = false;
            try { ok = await Windows.System.Launcher.LaunchUriAsync(new Uri("retrofox:" + _device.Url)); }
            catch (Exception ex) { InAppHelper.Log("retrofox launch: " + ex.Message); }
            if (!ok)
            {
                await Ui.AlertAsync("Retrofox couldn't be opened from here. It may need an update. The page will open in Internet Explorer instead.", "Microsoft sign-in");
                OpenBrowser_Click(sender, e);
            }
        }

        async void OpenBrowser_Click(object sender, RoutedEventArgs e)
        {
            if (_device == null) return;
            await Windows.System.Launcher.LaunchUriAsync(new Uri(_device.Url));
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
            Fill(ImapHostBox, p.ImapHost);
            Fill(ImapPortBox, p.ImapPort.ToString());
            Fill(SmtpHostBox, p.SmtpHost);
            Fill(SmtpPortBox, p.SmtpPort.ToString());
            _imapSec = p.ImapSec;
            _smtpSec = p.SmtpSec;
            Fill(UserBox, EmailBox.Text.Trim());
            SaveSentBox.IsChecked = p.SaveSent;
            _filling = false;
            ShowSec();
        }

        // What the app itself put in each server box. TextChanged arrives later on WinRT, after
        // _filling is cleared, so the app's own fill must not count as a change by hand (that froze
        // the user name at the first letter typed).
        readonly Dictionary<TextBox, string> _auto = new Dictionary<TextBox, string>();

        void Fill(TextBox box, string text)
        {
            _auto[box] = text;
            box.Text = text;
        }

        void Server_Changed(object sender, TextChangedEventArgs e)
        {
            if (_filling) return;
            var box = sender as TextBox;
            string auto;
            if (box != null && _auto.TryGetValue(box, out auto) && auto == box.Text) return;
            _manual = true;
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
                // a known service signs in with the full address unless it was changed by hand
                { "user", (!_manual && _provider != null) || string.IsNullOrEmpty(UserBox.Text.Trim()) ? email : UserBox.Text.Trim() },
                { "pass", _ms ? (_refresh ?? "") : PassBox.Password },
                { "auth", _ms ? "microsoft" : "password" },
                { "oauth_client", _ms ? ClientBox.Text.Trim() : "" },
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
            if (_ms && _refresh == null && !_signedIn)
            {
                await Ui.AlertAsync("Tap \"sign in with Microsoft\" first. Outlook.com doesn't let mail apps in with a password.", "account");
                return;
            }
            if (!_ms && _id == null && PassBox.Password.Length == 0)
            {
                await Ui.AlertAsync("Enter the app password your mail service gave you (or your password).", "account");
                return;
            }
            if ((_imapSec == "plain" || _smtpSec == "plain") &&
                !await Ui.ConfirmAsync("Without encryption, your password and mail can be read by anyone on the network. Continue?", "no security"))
                return;
            _saving = true;
            _save.IsEnabled = false;
            Busy("checking your account");
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
            if (!await Ui.ConfirmAsync("Remove " + EmailBox.Text + " from this phone? Your mail stays on the server.", "remove account")) return;
            try
            {
                var gone = AppState.Accounts.Find(a => a.Id == _id);
                if (gone != null) await Pins.UnpinAsync(gone);
                await AppState.Client.DeleteAccountAsync(_id);
                AppState.Accounts = await AppState.Client.GetAccountsAsync();
                if (AppState.CurrentAccountId == _id) AppState.SelectAccount(AppState.Accounts.Count > 0 ? AppState.Accounts[0] : null);
                GoBack();
            }
            catch (Exception ex) { await Ui.ErrorAsync(ex); }
        }
    }
}
