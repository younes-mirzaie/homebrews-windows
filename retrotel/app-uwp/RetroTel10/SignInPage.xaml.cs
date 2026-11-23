// Sign-in: phone number, then the code, then the two-step verification password if the account
// has one. The same calls as the 8.1 app's AuthPage (TL/AuthService.cs, shared).
using System;
using System.Threading.Tasks;
using MetroTelegram;
using MetroTelegram.Crypto;
using MetroTelegram.TL;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;

namespace RetroTel
{
    public sealed partial class SignInPage : Page
    {
        enum Step { Phone, Code, Password }
        Step _step = Step.Phone;
        bool _busy;

        AuthService _auth;
        string _phone, _codeHash;
        PasswordKdfParams _kdf;

        public SignInPage()
        {
            InitializeComponent();
            Loaded += (s, e) => PhoneBox.Focus(FocusState.Programmatic);
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            SystemNavigationManager.GetForCurrentView().BackRequested += OnBack;
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            SystemNavigationManager.GetForCurrentView().BackRequested -= OnBack;
            base.OnNavigatedFrom(e);
        }

        // Back from the code or password step returns to the phone number
        void OnBack(object sender, BackRequestedEventArgs e)
        {
            if (e.Handled || _step == Step.Phone || _busy) return;
            e.Handled = true;
            ShowStep(Step.Phone);
        }

        void Box_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != VirtualKey.Enter) return;
            e.Handled = true;
            Next_Click(sender, null);
        }

        async void Next_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            switch (_step)
            {
                case Step.Phone: await SendCodeAsync(); break;
                case Step.Code: await CheckCodeAsync(); break;
                case Step.Password: await CheckPasswordAsync(); break;
            }
        }

        void ChangeNumber_Click(object sender, RoutedEventArgs e)
        {
            if (!_busy) ShowStep(Step.Phone);
        }

        async Task SendCodeAsync()
        {
            string phone = (PhoneBox.Text ?? "").Trim();
            if (AuthService.SanitizePhoneNumber(phone).Length < 6)
            {
                await Ui.AlertAsync("Enter your phone number with the country code, like +374 for Armenia.", "Phone number");
                return;
            }
            SetBusy(true, "Connecting to Telegram...");
            try
            {
                await TgSession.EnsureConnectedAsync(s => SetStatus(s));
                _auth = new AuthService(TgSession.RpcEngine, TgSession.Transport, TgSession.Storage);
                SetStatus("Asking Telegram for a code...");
                SentCodeResult sent = await _auth.SendCodeAsync(phone);
                _phone = phone;
                _codeHash = sent.PhoneCodeHash;
                ShowStep(Step.Code);
                if (!string.IsNullOrEmpty(sent.DeliveryTypeDescription)) StepText.Text = sent.DeliveryTypeDescription;
            }
            catch (Exception ex)
            {
                // a failed connection is started over on the next try
                TgSession.Disconnect();
                await Ui.ErrorAsync("Couldn't get a code.", ex, "Sign in");
            }
            finally { SetBusy(false); }
        }

        async Task CheckCodeAsync()
        {
            string code = (CodeBox.Text ?? "").Trim();
            if (code.Length == 0) { await Ui.AlertAsync("Enter the code Telegram sent you.", "Code"); return; }
            SetBusy(true, "Checking the code...");
            try
            {
                AuthUserResult user = await _auth.SignInAsync(_phone, _codeHash, code);
                if (user.Requires2Fa)
                {
                    SetStatus("Getting your password settings...");
                    _kdf = await _auth.GetPasswordSettingsAsync();
                    ShowStep(Step.Password);
                    return;
                }
                await FinishAsync(user);
            }
            catch (Exception ex) { await Ui.ErrorAsync("Couldn't sign in.", ex, "Sign in"); }
            finally { SetBusy(false); }
        }

        async Task CheckPasswordAsync()
        {
            string password = PasswordBox.Password ?? "";
            if (password.Length == 0) { await Ui.AlertAsync("Enter your two-step verification password.", "Password"); return; }
            SetBusy(true, "Checking the password...");
            try
            {
                AuthUserResult user = await _auth.CheckPasswordAsync(password, _kdf, _phone);
                await FinishAsync(user);
            }
            catch (Exception ex)
            {
                PasswordBox.Password = "";
                await Ui.ErrorAsync("Couldn't sign in.", ex, "Sign in");
            }
            finally { SetBusy(false); }
        }

        Task FinishAsync(AuthUserResult user)
        {
            string name = ((user.FirstName ?? "") + " " + (user.LastName ?? "")).Trim();
            TgSession.Storage.SaveUserProfile(user.UserId, user.Phone, name);
            // the chats page opens its own connection with the signed-in key
            TgSession.Disconnect();
            App.StartOver(typeof(MainPage));
            return Task.CompletedTask;
        }

        void ShowStep(Step step)
        {
            _step = step;
            PhoneBox.Visibility = step == Step.Phone ? Visibility.Visible : Visibility.Collapsed;
            CodeBox.Visibility = step == Step.Code ? Visibility.Visible : Visibility.Collapsed;
            PasswordBox.Visibility = step == Step.Password ? Visibility.Visible : Visibility.Collapsed;
            ChangeNumber.Visibility = step == Step.Phone ? Visibility.Collapsed : Visibility.Visible;
            HintText.Visibility = Visibility.Collapsed;
            SetStatus("");
            switch (step)
            {
                case Step.Phone:
                    StepTitle.Text = "Sign in to Telegram";
                    StepText.Text = "Enter your phone number with the country code. Telegram will send you a code.";
                    NextButton.Label = "Next";
                    PhoneBox.Focus(FocusState.Programmatic);
                    break;
                case Step.Code:
                    StepTitle.Text = "Enter the code";
                    StepText.Text = "Telegram sent a code to your other Telegram apps or by text message to " + _phone + ".";
                    NextButton.Label = "Sign in";
                    CodeBox.Text = "";
                    CodeBox.Focus(FocusState.Programmatic);
                    break;
                case Step.Password:
                    StepTitle.Text = "Enter your password";
                    StepText.Text = "This account has two-step verification. Enter the password you set for it.";
                    NextButton.Label = "Sign in";
                    if (_kdf != null && !string.IsNullOrEmpty(_kdf.Hint))
                    {
                        HintText.Text = "Hint: " + _kdf.Hint;
                        HintText.Visibility = Visibility.Visible;
                    }
                    PasswordBox.Password = "";
                    PasswordBox.Focus(FocusState.Programmatic);
                    break;
            }
        }

        void SetBusy(bool busy, string status = null)
        {
            _busy = busy;
            Busy.IsIndeterminate = busy;
            Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            NextButton.IsEnabled = !busy;
            PhoneBox.IsEnabled = CodeBox.IsEnabled = PasswordBox.IsEnabled = !busy;
            if (status != null) SetStatus(status);
            else if (!busy) SetStatus("");
        }

        void SetStatus(string text)
        {
            var unused = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => StatusText.Text = text ?? "");
        }
    }
}
