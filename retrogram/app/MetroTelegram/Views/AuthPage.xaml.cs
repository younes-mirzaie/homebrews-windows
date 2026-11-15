using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Navigation;
using Microsoft.Phone.Controls;
using Microsoft.Phone.Shell;
using MetroTelegram.Crypto;
using MetroTelegram.TL;
using MetroTelegram.Transport;

namespace MetroTelegram.Views
{
    public partial class AuthPage : PhoneApplicationPage
    {
        private MtprotoTcpTransport _transport;
        private AuthKeyStorage _storage;
        private TelegramRpcEngine _rpcEngine;
        private AuthService _authService;

        private string _phoneNumber;
        private string _phoneCodeHash;
        private PasswordKdfParams _kdfParams;
        private ProgressIndicator _progressIndicator;

        public AuthPage()
        {
            InitializeComponent();

            _storage = new AuthKeyStorage();
            _storage.Load();

            _progressIndicator = new ProgressIndicator();
            SystemTray.SetProgressIndicator(this, _progressIndicator);
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            _storage.Load();

            _transport?.Disconnect();
            _transport = null;

            PhoneStepGrid.Visibility = Visibility.Visible;
            CodeStepGrid.Visibility = Visibility.Collapsed;
            PasswordStepGrid.Visibility = Visibility.Collapsed;
            TitleTextBlock.Text = "your number";
        }

        private async Task ConnectOnceAsync()
        {
            if (_transport == null || !_transport.IsConnected)
            {
                _storage.Load();
                DataCenter dc = DataCenter.GetDc(_storage.CurrentDcId);

                UpdateUiState(true, string.Format("Connecting...", dc.Id));

                _transport = new MtprotoTcpTransport();
                await _transport.ConnectAsync(dc);

                if (!_storage.HasAuthKey)
                {
                    UpdateUiState(true, "Setting up encryption...");
                    var handshake = new AuthKeyHandshake(_transport);
                    await handshake.ExecuteAsync(_storage);
                }

                _rpcEngine = new TelegramRpcEngine(_transport, _storage);
                _authService = new AuthService(_rpcEngine, _transport, _storage);

                UpdateUiState(true, "Connecting...");
                byte[] configQuery;
                using (var writer = new TlBinaryWriter())
                {
                    writer.WriteUInt32(0xc4f9186b);
                    configQuery = writer.ToByteArray();
                }

                await _rpcEngine.SendRpcQueryAsync(configQuery, wrapInitConnection: true);
                Debug.WriteLine("[AuthPage] Единая сессия сокета успешно открыта!");
            }
        }

        private async void SendCodeButton_Click(object sender, RoutedEventArgs e)
        {
            _phoneNumber = PhoneTextBox.Text.Trim();

            if (string.IsNullOrEmpty(_phoneNumber) || _phoneNumber.Length < 6)
            {
                MessageBox.Show("Enter your phone number.", "Retrogram", MessageBoxButton.OK);
                return;
            }

            try
            {
                UpdateUiState(true, "Sending...");
                await ConnectOnceAsync();

                SentCodeResult result = await _authService.SendCodeAsync(_phoneNumber);
                _phoneCodeHash = result.PhoneCodeHash;

                Dispatcher.BeginInvoke(() =>
                {
                    UpdateUiState(false);

                    PhoneStepGrid.Visibility = Visibility.Collapsed;
                    CodeStepGrid.Visibility = Visibility.Visible;
                    TitleTextBlock.Text = "code";

                    CodePromptTextBlock.Text = result.DeliveryTypeDescription;
                    CodeTextBox.Text = string.Empty;
                    CodeTextBox.Focus();
                });
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    UpdateUiState(false);
                    MessageBox.Show(MetroTelegram.Services.FriendlyErrors.ToText(ex), "Can't send", MessageBoxButton.OK);
                });
            }
        }

        private async void SignInButton_Click(object sender, RoutedEventArgs e)
        {
            string code = CodeTextBox.Text.Trim();

            if (string.IsNullOrEmpty(code))
            {
                MessageBox.Show("Enter the code.", "Retrogram", MessageBoxButton.OK);
                return;
            }

            try
            {
                UpdateUiState(true, "Checking the code...");

                AuthUserResult user = await _authService.SignInAsync(_phoneNumber, _phoneCodeHash, code);

                if (user.Requires2Fa)
                {
                    UpdateUiState(true, "Checking your account...");
                    _kdfParams = await _authService.GetPasswordSettingsAsync();

                    Dispatcher.BeginInvoke(() =>
                    {
                        UpdateUiState(false);

                        CodeStepGrid.Visibility = Visibility.Collapsed;
                        PasswordStepGrid.Visibility = Visibility.Visible;
                        TitleTextBlock.Text = "password";

                        if (!string.IsNullOrEmpty(_kdfParams.Hint))
                        {
                            PasswordHintTextBlock.Text = "Hint: " + _kdfParams.Hint;
                            PasswordHintTextBlock.Visibility = Visibility.Visible;
                        }
                        else
                        {
                            PasswordHintTextBlock.Visibility = Visibility.Collapsed;
                        }

                        CloudPasswordBox.Password = string.Empty;
                        CloudPasswordBox.Focus();
                    });
                    return;
                }

                CompleteLogin(user);
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    UpdateUiState(false);
                    MessageBox.Show(MetroTelegram.Services.FriendlyErrors.ToText(ex), "Can't sign in", MessageBoxButton.OK);
                });
            }
        }

        private async void SubmitPasswordButton_Click(object sender, RoutedEventArgs e)
        {
            string password = CloudPasswordBox.Password;

            if (string.IsNullOrEmpty(password))
            {
                MessageBox.Show("Enter your password.", "Retrogram", MessageBoxButton.OK);
                return;
            }

            try
            {
                UpdateUiState(true, "Signing in...");

                AuthUserResult user = await _authService.CheckPasswordAsync(password, _kdfParams, _phoneNumber);

                CompleteLogin(user);
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    UpdateUiState(false);
                    MessageBox.Show(MetroTelegram.Services.FriendlyErrors.ToText(ex), "Can't sign in", MessageBoxButton.OK);
                    CloudPasswordBox.Password = string.Empty;
                    CloudPasswordBox.Focus();
                });
            }
        }

        private void CompleteLogin(AuthUserResult user)
        {
            Dispatcher.BeginInvoke(() =>
            {
                string fullName = (user.FirstName + " " + user.LastName).Trim();
                _storage.SaveUserProfile(user.UserId, user.Phone, fullName);

                UpdateUiState(false);

                MessageBox.Show(string.Format("Welcome, {0}!", fullName), "Signed in", MessageBoxButton.OK);

                NavigationService.Navigate(new Uri("/MainPage.xaml", UriKind.Relative));
            });
        }

        private void ChangePhoneButton_Click(object sender, RoutedEventArgs e)
        {
            _storage.ClearPending2FaState();
            _transport?.Disconnect();
            _transport = null;

            PhoneStepGrid.Visibility = Visibility.Visible;
            CodeStepGrid.Visibility = Visibility.Collapsed;
            PasswordStepGrid.Visibility = Visibility.Collapsed;
            TitleTextBlock.Text = "your number";
            PhoneTextBox.Focus();
        }

        private void UpdateUiState(bool isLoading, string text = "")
        {
            Dispatcher.BeginInvoke(() =>
            {
                _progressIndicator.IsVisible = isLoading;
                _progressIndicator.IsIndeterminate = isLoading;
                _progressIndicator.Text = text;

                SendCodeButton.IsEnabled = !isLoading;
                SignInButton.IsEnabled = !isLoading;
                SubmitPasswordButton.IsEnabled = !isLoading;
                PhoneTextBox.IsEnabled = !isLoading;
                CodeTextBox.IsEnabled = !isLoading;
                CloudPasswordBox.IsEnabled = !isLoading;
            });
        }
    }
}