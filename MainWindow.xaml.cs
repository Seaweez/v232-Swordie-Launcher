using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WpfAnimatedGif;
using v232.Launcher.WPF.Services;
using v232.Launcher.WPF.Models;

namespace v232.Launcher.WPF
{
    public partial class MainWindow : Window
    {
        private bool _isOnline = false;
        private Client _client;
        private LoginService _loginService;
        private RegisterService _registerService;
        private bool _isLoggedIn = false;
        private bool _isNeonTheme = true;
        private bool _languagePickerReady;
        private static string ConfigFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            (Configs.GetBranding() ?? "").IndexOf("Clover", StringComparison.OrdinalIgnoreCase) >= 0 ? "CloverLauncher" : "MStoryXLauncher");
        private static string ThemeConfigPath => Path.Combine(ConfigFolder, "theme.cfg");
        private static string UserConfigPath => Path.Combine(ConfigFolder, "user.cfg");
        private static string BgmConfigPath => Path.Combine(ConfigFolder, "bgm.cfg");

        public MainWindow()
        {
            InitializeComponent();
            ApplyBranding();
            ClientLanguagePicker.SelectedIndex = ClientLanguageService.Load(AppDomain.CurrentDomain.BaseDirectory) == ClientLanguage.TH ? 1 : 0;
            _languagePickerReady = true;
            _registerService = new RegisterService();

            // Load saved theme preference (just the flag, don't apply yet)
            LoadThemePreference();

            // Try to connect to server on startup
            InitializeConnection();

            // Fetch dynamic links & announcements from remote server asynchronously
            _ = LauncherRemoteConfigService.Instance.RefreshAsync();

            // Load GIFs and apply theme when window loads
            Loaded += MainWindow_Loaded;
        }

        private void ApplyBranding()
        {
            try
            {
                string brand = Configs.GetBranding();
                this.Title = brand + " Launcher";
                if (BrandingTitleText != null) BrandingTitleText.Text = brand;
                if (VersionTagText != null) VersionTagText.Text = "v232.2";
                if (LoadingOverlayTitle != null) LoadingOverlayTitle.Text = brand;

                if ((brand ?? "").IndexOf("Clover", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    this.Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/clover.ico"));
                }
                else
                {
                    this.Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/Slime.ico"));
                }
            }
            catch { }
        }

        private bool _classicGifReady = false;
        private bool _neonGifReady = false;
        private bool _isBgmPlaying = false;

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Hide all GIF backgrounds initially
            GifBackgroundClassic.Visibility = Visibility.Collapsed;
            GifOverlayClassic.Visibility = Visibility.Collapsed;
            GifBackgroundNeon.Visibility = Visibility.Collapsed;
            GifOverlayNeon.Visibility = Visibility.Collapsed;

            if (Configs.GetBranding().IndexOf("Clover", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                try
                {
                    var cloverBg = new BitmapImage(new Uri("pack://application:,,,/Assets/clover_bg.png"));
                    GifBackgroundClassic.Source = cloverBg;
                    GifBackgroundNeon.Source = cloverBg;
                }
                catch { }
                _classicGifReady = true;
                _neonGifReady = true;
            }
            else
            {
                // Load Classic GIF
                var classicImage = new BitmapImage(new Uri("pack://application:,,,/Assets/bg2.gif"));
                ImageBehavior.SetAnimatedSource(GifBackgroundClassic, classicImage);
                ImageBehavior.SetRepeatBehavior(GifBackgroundClassic, System.Windows.Media.Animation.RepeatBehavior.Forever);
                ImageBehavior.AddAnimationLoadedHandler(GifBackgroundClassic, OnClassicGifLoaded);

                // Load Neon GIF
                var neonImage = new BitmapImage(new Uri("pack://application:,,,/Assets/bg.gif"));
                ImageBehavior.SetAnimatedSource(GifBackgroundNeon, neonImage);
                ImageBehavior.SetRepeatBehavior(GifBackgroundNeon, System.Windows.Media.Animation.RepeatBehavior.Forever);
                ImageBehavior.AddAnimationLoadedHandler(GifBackgroundNeon, OnNeonGifLoaded);

                // Wait for BOTH GIFs to be ready (max 8 seconds)
                int waited = 0;
                while (waited < 8000)
                {
                    if (_classicGifReady && _neonGifReady) break;
                    await Task.Delay(100);
                    waited += 100;
                }
            }

            // Extra buffer for rendering
            await Task.Delay(300);

            // NOW apply the theme
            if (_isNeonTheme)
                ApplyNeonTheme();
            else
                ApplyClassicTheme();

            // Load saved username
            LoadSavedUsername();

            // Fade out loading overlay
            var fadeOut = new System.Windows.Media.Animation.DoubleAnimation
            {
                From = 1,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(400)
            };
            fadeOut.Completed += (s, args) => LoadingOverlay.Visibility = Visibility.Collapsed;
            LoadingOverlay.BeginAnimation(OpacityProperty, fadeOut);

            // Initialize and start BGM
            InitializeBgm();

            // Run auto-heal & differential patch check in background
            RunPatchAndAutoLaunchAsync();
        }

        private async void RunPatchAndAutoLaunchAsync()
        {
            try
            {
                // 1. Ensure local Canvas mode is auto-healed immediately
                CanvasModeService.EnsureOrHealCanvas(AppDomain.CurrentDomain.BaseDirectory);

                // 2. Differential patch check from remote CDN
                var patchResult = await PatchService.CheckAndApplyUpdatesAsync(
                    AppDomain.CurrentDomain.BaseDirectory,
                    (status, progress) =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            if (StatusText != null) StatusText.Text = status;
                        });
                    });

                Dispatcher.Invoke(() =>
                {
                    if (StatusText != null && _isOnline) StatusText.Text = "Online";
                });

                // 3. Handle clover:// protocol if invoked from browser
                CheckProtocolLaunch();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Launcher] Patch check notice: {ex.Message}");
            }
        }

        private void CheckProtocolLaunch()
        {
            try
            {
                string[] args = Environment.GetCommandLineArgs();
                for (int i = 1; i < args.Length; i++)
                {
                    string arg = args[i];
                    if (arg.StartsWith("clover://", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine($"[Protocol] Received: {arg}");
                        int qIdx = arg.IndexOf('?');
                        if (qIdx >= 0 && qIdx < arg.Length - 1)
                        {
                            string query = arg.Substring(qIdx + 1);
                            foreach (string param in query.Split('&'))
                            {
                                string[] parts = param.Split('=');
                                if (parts.Length == 2 && parts[0].Equals("user", StringComparison.OrdinalIgnoreCase))
                                {
                                    LoginUsername.Text = Uri.UnescapeDataString(parts[1]);
                                    LoginPassword.Focus();
                                }
                            }
                        }
                        break;
                    }
                }
            }
            catch { }
        }

        private void OnClassicGifLoaded(object sender, RoutedEventArgs e)
        {
            _classicGifReady = true;
        }

        private void OnNeonGifLoaded(object sender, RoutedEventArgs e)
        {
            _neonGifReady = true;
        }

        private void LoadThemePreference()
        {
            try
            {
                if (File.Exists(ThemeConfigPath))
                {
                    string savedTheme = File.ReadAllText(ThemeConfigPath).Trim().ToLowerInvariant();
                    _isNeonTheme = (savedTheme != "classic");
                }
                else
                {
                    _isNeonTheme = true;
                }
            }
            catch
            {
                _isNeonTheme = true;
            }
        }

        private void SaveThemePreference()
        {
            try
            {
                if (!Directory.Exists(ConfigFolder))
                    Directory.CreateDirectory(ConfigFolder);
                File.WriteAllText(ThemeConfigPath, _isNeonTheme ? "neon" : "classic");
            }
            catch { }
        }

        private void LoadSavedUsername()
        {
            try
            {
                if (File.Exists(UserConfigPath))
                {
                    string savedUser = File.ReadAllText(UserConfigPath).Trim();
                    if (!string.IsNullOrEmpty(savedUser))
                    {
                        LoginUsername.Text = savedUser;
                        RememberMeCheckbox.IsChecked = true;
                        LoginPassword.Focus();
                    }
                }
            }
            catch { }
        }

        private void SaveUsername()
        {
            try
            {
                if (!Directory.Exists(ConfigFolder))
                    Directory.CreateDirectory(ConfigFolder);

                if (RememberMeCheckbox.IsChecked == true)
                    File.WriteAllText(UserConfigPath, LoginUsername.Text.Trim());
                else if (File.Exists(UserConfigPath))
                    File.Delete(UserConfigPath);
            }
            catch { }
        }

        private void RememberMe_Changed(object sender, RoutedEventArgs e)
        {
            SaveUsername();
        }

        #region Server Connection

        private async void InitializeConnection()
        {
            await Task.Run(() =>
            {
                try
                {
                    _client = new Client();
                    _isOnline = _client.Connect();
                }
                catch
                {
                    _isOnline = false;
                }
            });

            Dispatcher.Invoke(() => UpdateServerStatus(_isOnline));
        }

        public void UpdateServerStatus(bool isOnline)
        {
            _isOnline = isOnline;

            if (isOnline)
            {
                StatusDot.Fill = (Brush)FindResource("SuccessBrush");
                StatusText.Text = "Online";
                StatusText.Foreground = (Brush)FindResource("SuccessBrush");
            }
            else
            {
                StatusDot.Fill = (Brush)FindResource("ErrorBrush");
                StatusText.Text = "Offline";
                StatusText.Foreground = (Brush)FindResource("ErrorBrush");
            }
        }

        #endregion

        #region Window Controls

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 1)
            {
                DragMove();
            }
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            _client?.Disconnect();
            Application.Current.Shutdown();
        }

        #endregion

        #region Form Switching

        private void ShowRegister_Click(object sender, RoutedEventArgs e)
        {
            LoginForm.Visibility = Visibility.Collapsed;
            RegisterForm.Visibility = Visibility.Visible;
        }

        private void ShowLogin_Click(object sender, RoutedEventArgs e)
        {
            RegisterForm.Visibility = Visibility.Collapsed;
            LoginForm.Visibility = Visibility.Visible;
        }

        private void ShowLoggedInPanel(string username)
        {
            LoginForm.Visibility = Visibility.Collapsed;
            RegisterForm.Visibility = Visibility.Collapsed;
            LoggedInPanel.Visibility = Visibility.Visible;
            UsernameDisplay.Text = username;
            _isLoggedIn = true;
        }

        private void ShowLoginForm()
        {
            LoggedInPanel.Visibility = Visibility.Collapsed;
            RegisterForm.Visibility = Visibility.Collapsed;
            LoginForm.Visibility = Visibility.Visible;
            _isLoggedIn = false;
        }

        #endregion

        #region Login

        private async void LoginButton_Click(object sender, RoutedEventArgs e)
        {
            string username = LoginUsername.Text.Trim();
            string password = LoginPassword.Password;

            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            {
                MessageBox.Show("Please enter both username and password.", "Login Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!_isOnline)
            {
                MessageBox.Show("Cannot connect to server. Please check your connection.", "Connection Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            LoginButton.IsEnabled = false;
            LoginButton.Content = "SIGNING IN...";

            try
            {
                _loginService = new LoginService(username, password);
                _loginService.CClient = _client;

                bool success = await _loginService.Authenticate();

                if (success)
                {
                    SaveUsername();
                    ShowLoggedInPanel(username);
                    LoginPassword.Password = "";
                }
                else
                {
                    MessageBox.Show("Invalid username or password.", "Login Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Login error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                LoginButton.IsEnabled = true;
                LoginButton.Content = "SIGN IN";
            }
        }

        #endregion

        #region Register

        private async void RegisterButton_Click(object sender, RoutedEventArgs e)
        {
            string username = RegisterUsername.Text.Trim();
            string email = RegisterEmail.Text.Trim();
            string password = RegisterPassword.Password;
            string confirmPassword = RegisterConfirmPassword.Password;

            // Validate input
            int validationResult = _registerService.HandleSignUpInput(username, password, confirmPassword, email);

            if (validationResult != 0)
            {
                MessageBox.Show(_registerService.GetErrorMessage(validationResult), "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!_isOnline)
            {
                MessageBox.Show("Cannot connect to server. Please check your connection.", "Connection Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            RegisterButton.IsEnabled = false;
            RegisterButton.Content = "CREATING...";

            try
            {
                var result = await _registerService.CreateAccount(username, password, email, _client);

                if (result.success)
                {
                    MessageBox.Show(result.message, "Success", MessageBoxButton.OK, MessageBoxImage.Information);

                    // Clear fields and switch to login
                    RegisterUsername.Text = "";
                    RegisterEmail.Text = "";
                    RegisterPassword.Password = "";
                    RegisterConfirmPassword.Password = "";

                    // Pre-fill login username
                    LoginUsername.Text = username;
                    ShowLoginForm();
                }
                else
                {
                    MessageBox.Show(result.message, "Registration Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Registration error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                RegisterButton.IsEnabled = true;
                RegisterButton.Content = "CREATE ACCOUNT";
            }
        }

        #endregion

        #region Play Game

        private void ClientLanguagePicker_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (!_languagePickerReady) return;
            string directory = AppDomain.CurrentDomain.BaseDirectory;
            var previous = ClientLanguageService.Load(directory);
            var selected = ClientLanguagePicker.SelectedIndex == 1 ? ClientLanguage.TH : ClientLanguage.EN;
            try
            {
                if (selected == ClientLanguage.TH && previous != selected &&
                    MessageBox.Show("โหมด TH ยังอยู่ระหว่างแก้อาการหลุดตอนเข้าแมพ และยังไม่พร้อมแจกผู้เล่น\nต้องการเลือกเพื่อทดสอบหรือไม่?\n\nEN เป็นโหมดที่ทดสอบเข้าแมพผ่านแล้ว",
                        "TH — Test mode", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                    return;
                ClientLanguageService.Save(directory, selected);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Language setting", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _languagePickerReady = false;
                ClientLanguagePicker.SelectedIndex = ClientLanguageService.Load(directory) == ClientLanguage.TH ? 1 : 0;
                _languagePickerReady = true;
            }
        }

        private async void PlayButton_Click(object sender, RoutedEventArgs e)
        {
            if (_loginService == null || !_loginService.Auth)
            {
                MessageBox.Show("Please login first.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            PlayButton.IsEnabled = false;

            try
            {
                bool refreshed = await _loginService.RefreshAuthenticationForLaunchAsync();
                if (!refreshed)
                {
                    MessageBox.Show("Your login session expired. Please sign in again.", "Login Expired", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                bool launched = await _loginService.LaunchMapleAsync();

                if (launched)
                {
                    this.WindowState = WindowState.Minimized;
                }
                else
                {
                    this.WindowState = WindowState.Normal;
                    this.Activate();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to launch game: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                PlayButton.IsEnabled = true;
            }
        }

        private void LogoutButton_Click(object sender, RoutedEventArgs e)
        {
            _loginService = null;
            _isLoggedIn = false;
            LoginUsername.Text = "";
            LoginPassword.Password = "";
            ShowLoginForm();
        }

        #endregion

        #region News Item

        private void NewsItem_MouseEnter(object sender, MouseEventArgs e)
        {
            if (sender is System.Windows.Controls.Border border)
            {
                border.Background = (Brush)FindResource("BackgroundElevatedBrush");
                border.BorderBrush = (Brush)FindResource("PrimaryAccentBrush");
            }
        }

        private void NewsItem_MouseLeave(object sender, MouseEventArgs e)
        {
            if (sender is System.Windows.Controls.Border border)
            {
                border.Background = (Brush)FindResource("BackgroundCardBrush");
                border.BorderBrush = (Brush)FindResource("BorderBrush");
            }
        }

        private void NewsItem1_Click(object sender, MouseButtonEventArgs e)
        {
            try
            {
                string target = !string.IsNullOrWhiteSpace(LauncherRemoteConfigService.Instance.LatestNewsUrl)
                    ? LauncherRemoteConfigService.Instance.LatestNewsUrl
                    : LauncherRemoteConfigService.Instance.DiscordUrl;

                Process.Start(new ProcessStartInfo
                {
                    FileName = target,
                    UseShellExecute = true
                });
            }
            catch { }
        }

        #endregion

        #region Website Button

        private void WebsiteButton_MouseEnter(object sender, MouseEventArgs e)
        {
            if (sender is System.Windows.Controls.Border border)
            {
                border.Background = (Brush)FindResource("BackgroundElevatedBrush");
                border.BorderBrush = (Brush)FindResource("PrimaryAccentBrush");
                border.Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 15,
                    ShadowDepth = 0,
                    Color = (Color)ColorConverter.ConvertFromString("#FF8C00"),
                    Opacity = 0.45
                };
            }
        }

        private void WebsiteButton_MouseLeave(object sender, MouseEventArgs e)
        {
            if (sender is System.Windows.Controls.Border border)
            {
                border.Background = (Brush)FindResource("BackgroundCardBrush");
                border.BorderBrush = (Brush)FindResource("BorderBrush");
                border.Effect = null;
            }
        }

        private void FacebookButton_Click(object sender, MouseButtonEventArgs e)
        {
            try
            {
                string fbUrl = LauncherRemoteConfigService.Instance.FacebookUrl;
                if (string.IsNullOrWhiteSpace(fbUrl))
                    fbUrl = Configs.ReadMetadataValue("facebookUrl");
                if (string.IsNullOrWhiteSpace(fbUrl))
                {
                    if (Configs.GetBranding().IndexOf("Clover", StringComparison.OrdinalIgnoreCase) >= 0)
                        fbUrl = "https://www.facebook.com/CloverIdlestory";
                    else
                        fbUrl = "https://facebook.com";
                }
                Process.Start(new ProcessStartInfo
                {
                    FileName = fbUrl,
                    UseShellExecute = true
                });
            }
            catch { }
        }

        private void DiscordButton_Click(object sender, MouseButtonEventArgs e)
        {
            try
            {
                string discordUrl = LauncherRemoteConfigService.Instance.DiscordUrl;
                if (string.IsNullOrWhiteSpace(discordUrl))
                    discordUrl = Configs.ReadMetadataValue("discordUrl");
                if (string.IsNullOrWhiteSpace(discordUrl))
                    discordUrl = "https://discord.gg";
                Process.Start(new ProcessStartInfo
                {
                    FileName = discordUrl,
                    UseShellExecute = true
                });
            }
            catch { }
        }

        private void WebsiteButton_Click(object sender, MouseButtonEventArgs e)
        {
            try
            {
                string webUrl = Configs.ReadMetadataValue("websiteUrl");
                if (string.IsNullOrWhiteSpace(webUrl))
                {
                    if (Configs.GetBranding().IndexOf("Clover", StringComparison.OrdinalIgnoreCase) >= 0)
                        webUrl = "https://clover-story.duckdns.org/";
                    else
                        webUrl = "https://mstory-x.com";
                }
                Process.Start(new ProcessStartInfo
                {
                    FileName = webUrl,
                    UseShellExecute = true
                });
            }
            catch { }
        }

        #endregion

        #region Theme Toggle

        private void ThemeToggle_MouseEnter(object sender, MouseEventArgs e)
        {
            if (sender is System.Windows.Controls.Border border)
            {
                border.Background = (Brush)FindResource("BackgroundElevatedBrush");
                border.BorderBrush = (Brush)FindResource("PrimaryAccentBrush");
            }
        }

        private void ThemeToggle_MouseLeave(object sender, MouseEventArgs e)
        {
            if (sender is System.Windows.Controls.Border border)
            {
                border.Background = (Brush)FindResource("BackgroundCardBrush");
                border.BorderBrush = (Brush)FindResource("BorderBrush");
            }
        }

        private void ThemeToggle_Click(object sender, MouseButtonEventArgs e)
        {
            _isNeonTheme = !_isNeonTheme;

            if (_isNeonTheme)
                ApplyNeonTheme();
            else
                ApplyClassicTheme();

            SaveThemePreference();
        }

        private void ApplyNeonTheme()
        {
            var app = Application.Current;
            var resources = app.Resources.MergedDictionaries;
            resources.Clear();
            resources.Add(new ResourceDictionary { Source = new Uri("Themes/NeonTheme.xaml", UriKind.Relative) });

            ThemeToggleText.Text = "Neon";
            ThemeToggleButton.ToolTip = "ธีมปัจจุบัน: Neon (คลิกเพื่อสลับเป็น Classic)";

            // Show Neon GIF, hide Classic GIF
            GifBackgroundNeon.Visibility = Visibility.Visible;
            GifOverlayNeon.Visibility = Visibility.Visible;
            GifBackgroundClassic.Visibility = Visibility.Collapsed;
            GifOverlayClassic.Visibility = Visibility.Collapsed;

            LeftPanel.Background = new SolidColorBrush(Color.FromArgb(0x60, 0x05, 0x05, 0x05));
        }

        private void ApplyClassicTheme()
        {
            var app = Application.Current;
            var resources = app.Resources.MergedDictionaries;
            resources.Clear();
            resources.Add(new ResourceDictionary { Source = new Uri("Themes/DarkTheme.xaml", UriKind.Relative) });

            ThemeToggleText.Text = "Classic";
            ThemeToggleButton.ToolTip = "ธีมปัจจุบัน: Classic (คลิกเพื่อสลับเป็น Neon)";

            // Show Classic GIF, hide Neon GIF
            GifBackgroundClassic.Visibility = Visibility.Visible;
            GifOverlayClassic.Visibility = Visibility.Visible;
            GifBackgroundNeon.Visibility = Visibility.Collapsed;
            GifOverlayNeon.Visibility = Visibility.Collapsed;

            LeftPanel.Background = new SolidColorBrush(Color.FromArgb(0x60, 0x0A, 0x0A, 0x0A));
        }

        #endregion

        #region Music Player

        private string _bgmTempPath;

        private void InitializeBgm()
        {
            try
            {
                // Load saved BGM preferences
                LoadBgmPreferences();

                // Extract BGM from embedded resource to temp file
                var resourceUri = new Uri("pack://application:,,,/Assets/bgm.mp3");
                var resourceStream = Application.GetResourceStream(resourceUri);

                if (resourceStream != null)
                {
                    _bgmTempPath = Path.Combine(Path.GetTempPath(), "MStoryX_bgm.mp3");

                    using (var fileStream = new FileStream(_bgmTempPath, FileMode.Create, FileAccess.Write))
                    {
                        resourceStream.Stream.CopyTo(fileStream);
                    }

                    BgmPlayer.Source = new Uri(_bgmTempPath);
                    BgmPlayer.Volume = VolumeSlider.Value;

                    if (_isBgmPlaying)
                    {
                        BgmPlayer.Play();
                    }
                    UpdatePlayPauseIcon();
                }
            }
            catch { }
        }

        private void LoadBgmPreferences()
        {
            try
            {
                if (File.Exists(BgmConfigPath))
                {
                    string[] lines = File.ReadAllLines(BgmConfigPath);
                    foreach (string line in lines)
                    {
                        if (line.StartsWith("volume="))
                        {
                            if (double.TryParse(line.Substring(7), out double vol))
                            {
                                VolumeSlider.Value = Math.Max(0, Math.Min(1, vol));
                            }
                        }
                        else if (line.StartsWith("playing="))
                        {
                            _isBgmPlaying = line.Substring(8) == "true";
                        }
                    }
                }
                else
                {
                    // Default: playing at 50% volume
                    _isBgmPlaying = true;
                    VolumeSlider.Value = 0.5;
                }
            }
            catch
            {
                _isBgmPlaying = true;
                VolumeSlider.Value = 0.5;
            }
        }

        private void SaveBgmPreferences()
        {
            try
            {
                if (!Directory.Exists(ConfigFolder))
                    Directory.CreateDirectory(ConfigFolder);

                string content = $"volume={VolumeSlider.Value:F2}\nplaying={(_isBgmPlaying ? "true" : "false")}";
                File.WriteAllText(BgmConfigPath, content);
            }
            catch { }
        }

        private void PlayPause_Click(object sender, MouseButtonEventArgs e)
        {
            if (_isBgmPlaying)
            {
                BgmPlayer.Pause();
                _isBgmPlaying = false;
            }
            else
            {
                BgmPlayer.Play();
                _isBgmPlaying = true;
            }
            UpdatePlayPauseIcon();
            SaveBgmPreferences();
        }

        private void UpdatePlayPauseIcon()
        {
            if (_isBgmPlaying)
            {
                // Show pause icon (two vertical bars)
                PlayPauseIcon.Data = System.Windows.Media.Geometry.Parse("M6,4 L10,4 L10,20 L6,20 Z M14,4 L18,4 L18,20 L14,20 Z");
            }
            else
            {
                // Show play icon (triangle)
                PlayPauseIcon.Data = System.Windows.Media.Geometry.Parse("M8,5 L19,12 L8,19 Z");
            }
        }

        private void PlayPause_MouseEnter(object sender, MouseEventArgs e)
        {
            if (sender is System.Windows.Controls.Border border)
            {
                border.Background = (Brush)FindResource("PrimaryAccentBrush");
                PlayPauseIcon.Fill = Brushes.White;
            }
        }

        private void PlayPause_MouseLeave(object sender, MouseEventArgs e)
        {
            if (sender is System.Windows.Controls.Border border)
            {
                border.Background = (Brush)FindResource("BackgroundElevatedBrush");
                PlayPauseIcon.Fill = (Brush)FindResource("PrimaryAccentBrush");
            }
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (BgmPlayer != null)
            {
                BgmPlayer.Volume = e.NewValue;
                SaveBgmPreferences();
            }
        }

        private void BgmPlayer_MediaEnded(object sender, RoutedEventArgs e)
        {
            // Loop the BGM
            BgmPlayer.Position = TimeSpan.Zero;
            BgmPlayer.Play();
        }

        #endregion

        #region Credits

        private void CreditsButton_MouseEnter(object sender, MouseEventArgs e)
        {
            if (sender is System.Windows.Controls.Border border)
            {
                border.Background = (Brush)FindResource("BackgroundElevatedBrush");
                border.BorderBrush = (Brush)FindResource("PrimaryAccentBrush");
            }
        }

        private void CreditsButton_MouseLeave(object sender, MouseEventArgs e)
        {
            if (sender is System.Windows.Controls.Border border)
            {
                border.Background = (Brush)FindResource("BackgroundCardBrush");
                border.BorderBrush = (Brush)FindResource("BorderBrush");
            }
        }

        private void CreditsButton_Click(object sender, MouseButtonEventArgs e)
        {
            CreditsPopup.Visibility = Visibility.Visible;
        }

        private void CloseCredits_Click(object sender, MouseButtonEventArgs e)
        {
            CreditsPopup.Visibility = Visibility.Collapsed;
        }

        #endregion
    }
}
