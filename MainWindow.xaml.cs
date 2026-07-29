using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace Zcwws
{
    public class ConfigData
    {
        public bool SlideMacroEnabled { get; set; } = true;
        public bool RequireSprint { get; set; } = true;
        public ushort TriggerVk { get; set; } = 67;
        public string TriggerText { get; set; } = "[ C ]";
        public ushort CrouchVk { get; set; } = 162;
        public string CrouchText { get; set; } = "[ LeftCtrl ]";
        public double SlideDelayMs { get; set; } = 25.0;
        public int SlideMode { get; set; } = 0;
        public bool SoundAlertEnabled { get; set; } = true;
        public bool OverlayLedEnabled { get; set; } = true;
        public bool MinimizeToTray { get; set; } = false;
    }

    public class UpdatePayload
    {
        public string Version { get; set; } = "1.0.5";
        public string DownloadUrl { get; set; } = "";
        public string Changelog { get; set; } = "";
    }

    public partial class MainWindow : Window
    {
        public const string CurrentVersion = "1.0.5";

        #region WinAPI P/Invoke Declarations & Memory Optimizer
        [DllImport("psapi.dll")]
        private static extern bool EmptyWorkingSet(IntPtr hProcess);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, int dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint uCode, uint uMapType);

        private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        private const int WH_KEYBOARD_LL = 13;
        private const int WH_MOUSE_LL = 14;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_MBUTTONDOWN = 0x0207;
        private const int WM_XBUTTONDOWN = 0x020B;

        private const uint KEYEVENTF_KEYUP_FLAG = 0x0002;
        private const uint KEYEVENTF_SCANCODE_FLAG = 0x0008;

        private const byte VK_W_KEY = 0x57;
        private const byte VK_A_KEY = 0x41;
        private const byte VK_D_KEY = 0x44;
        private const byte VK_SHIFT_KEY = 0x10;
        private const byte VK_SPACE_KEY = 0x20;
        private const byte VK_Z_KEY = 0x5A;

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public int ptX;
            public int ptY;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        public static void TrimProcessMemory()
        {
            try
            {
                GC.Collect(2, GCCollectionMode.Forced, true);
                GC.WaitForPendingFinalizers();
                EmptyWorkingSet(Process.GetCurrentProcess().Handle);
            }
            catch
            {
            }
        }
        #endregion

        private IntPtr _kHook = IntPtr.Zero;
        private IntPtr _mHook = IntPtr.Zero;
        private HookProc _kProc;
        private HookProc _mProc;

        private bool _isBindingTrigger = false;
        private bool _isBindingCrouch = false;

        private ConfigData _config = new ConfigData();
        private readonly string _configPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "zcwws_config.json");

        public MainWindow()
        {
            InitializeComponent();
            _kProc = LowLevelKeyboardProc;
            _mProc = LowLevelMouseProc;
            LoadConfig();
            ApplyConfigToUi();
            InstallHooks();

            this.Loaded += (s, e) =>
            {
                TrimProcessMemory();
                Task.Run(CheckForUpdatesAsync);
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { Console.Beep(900, 100); } catch { }
                });
            };
        }

        protected override void OnClosed(EventArgs e)
        {
            UninstallHooks();
            SaveConfig();
            base.OnClosed(e);
        }

        #region Auto-Update Engine
        private async Task CheckForUpdatesAsync()
        {
            try
            {
                string updateUrl = "https://zcwws.gt.tc/version.json";
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) })
                {
                    var response = await http.GetAsync(updateUrl);
                    if (response.IsSuccessStatusCode)
                    {
                        string json = await response.Content.ReadAsStringAsync();
                        var info = JsonSerializer.Deserialize<UpdatePayload>(json);
                        if (info != null && IsNewerVersion(info.Version, CurrentVersion))
                        {
                            Dispatcher.Invoke(() =>
                            {
                                StatusSubtext.Text = $"✨ Đang tự động cập nhật v{info.Version}...";
                                StatusSubtext.Foreground = new SolidColorBrush(Color.FromRgb(59, 130, 246));
                            });

                            await DownloadAndExecuteUpdate(info.DownloadUrl);
                        }
                    }
                }
            }
            catch
            {
            }
        }

        private bool IsNewerVersion(string newVersion, string currentVersion)
        {
            try
            {
                return Version.Parse(newVersion) > Version.Parse(currentVersion);
            }
            catch
            {
                return false;
            }
        }

        private async Task DownloadAndExecuteUpdate(string downloadUrl)
        {
            if (string.IsNullOrWhiteSpace(downloadUrl)) return;

            try
            {
                string currentExe = Process.GetCurrentProcess().MainModule?.FileName ?? System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Zcwws.exe");
                string tempExe = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Zcwws_new.exe");

                using (var http = new HttpClient())
                {
                    byte[] bytes = await http.GetByteArrayAsync(downloadUrl);
                    await File.WriteAllBytesAsync(tempExe, bytes);
                }

                string cmd = $"Start-Sleep -Seconds 1; Move-Item -Path '{tempExe}' -Destination '{currentExe}' -Force; Start-Process '{currentExe}'";
                Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell",
                    Arguments = $"-NoProfile -WindowStyle Hidden -Command \"{cmd}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false
                });

                Dispatcher.Invoke(() => System.Windows.Application.Current.Shutdown());
            }
            catch
            {
            }
        }
        #endregion

        #region Config Management
        private void LoadConfig()
        {
            try
            {
                if (File.Exists(_configPath))
                {
                    string json = File.ReadAllText(_configPath);
                    var loaded = JsonSerializer.Deserialize<ConfigData>(json);
                    if (loaded != null)
                    {
                        _config = loaded;
                    }
                }
            }
            catch
            {
            }
        }

        private void SaveConfig()
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(_config, options);
                File.WriteAllText(_configPath, json);
            }
            catch
            {
            }
        }

        private void ApplyConfigToUi()
        {
            ToggleMacroEnabled.IsChecked = _config.SlideMacroEnabled;
            ToggleRequireSprint.IsChecked = _config.RequireSprint;
            if (ToggleMinimizeToTray != null) ToggleMinimizeToTray.IsChecked = _config.MinimizeToTray;

            BtnTriggerKey.Content = _config.TriggerText;
            BtnCrouchKey.Content = _config.CrouchText;
            SliderDelay.Value = _config.SlideDelayMs;
            TxtDelayValue.Text = $"{_config.SlideDelayMs:F0} ms";

            UpdateStatusUi();
        }

        private void UpdateStatusUi()
        {
            if (StatusLed == null || StatusSubtext == null) return;

            if (_config.SlideMacroEnabled)
            {
                StatusLed.Fill = new SolidColorBrush(Color.FromRgb(16, 185, 129));
                if (StatusLed.Effect is DropShadowEffect glow)
                {
                    glow.Color = Color.FromRgb(16, 185, 129);
                }
                StatusSubtext.Text = "Active • Ready in-game";
                StatusSubtext.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));
            }
            else
            {
                StatusLed.Fill = new SolidColorBrush(Color.FromRgb(107, 114, 128));
                if (StatusLed.Effect is DropShadowEffect glow)
                {
                    glow.Color = Color.FromRgb(107, 114, 128);
                }
                StatusSubtext.Text = "Paused • Macro disabled";
                StatusSubtext.Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128));
            }
        }

        private void PlaySoundAlert(bool enabled)
        {
            if (!_config.SoundAlertEnabled) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    if (enabled) Console.Beep(900, 70);
                    else Console.Beep(450, 70);
                }
                catch
                {
                }
            });
        }
        #endregion

        #region Low-Level Hook Engine
        private void InstallHooks()
        {
            using (Process curProcess = Process.GetCurrentProcess())
            using (ProcessModule? curModule = curProcess.MainModule)
            {
                if (curModule != null)
                {
                    IntPtr modHandle = GetModuleHandle(curModule.ModuleName);
                    _kHook = SetWindowsHookEx(WH_KEYBOARD_LL, _kProc, modHandle, 0);
                    _mHook = SetWindowsHookEx(WH_MOUSE_LL, _mProc, modHandle, 0);
                }
            }
        }

        private void UninstallHooks()
        {
            if (_kHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_kHook);
                _kHook = IntPtr.Zero;
            }
            if (_mHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_mHook);
                _mHook = IntPtr.Zero;
            }
        }

        private IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
            {
                int vk = Marshal.ReadInt32(lParam);

                // Global Hotkey: HOME key (vk == 36) to toggle Show/Hide window
                if (vk == 36)
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (this.IsVisible && this.WindowState != WindowState.Minimized)
                        {
                            if (_config.MinimizeToTray) this.Hide();
                            else this.WindowState = WindowState.Minimized;
                        }
                        else
                        {
                            this.Show();
                            this.ShowInTaskbar = true;
                            this.WindowState = WindowState.Normal;
                            this.Activate();
                        }
                    });
                    return (IntPtr)1;
                }

                if (_isBindingTrigger || _isBindingCrouch)
                {
                    HandleKeybindCaptured((ushort)vk, KeyInterop.KeyFromVirtualKey(vk).ToString());
                    return (IntPtr)1;
                }

                CheckAndExecuteSlideMacro(vk);
            }
            return CallNextHookEx(_kHook, nCode, wParam, lParam);
        }

        private IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = (int)wParam;
                ushort mouseVk = 0;
                string mouseText = "";

                if (msg == WM_XBUTTONDOWN)
                {
                    var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    uint xbutton = (hookStruct.mouseData >> 16) & 0xFFFF;
                    if (xbutton == 1) { mouseVk = 5; mouseText = "[ Mouse4 ]"; }
                    else if (xbutton == 2) { mouseVk = 6; mouseText = "[ Mouse5 ]"; }
                }
                else if (msg == WM_MBUTTONDOWN)
                {
                    mouseVk = 4; mouseText = "[ MiddleMouse ]";
                }

                if (mouseVk > 0)
                {
                    if (_isBindingTrigger || _isBindingCrouch)
                    {
                        HandleKeybindCaptured(mouseVk, mouseText);
                        return (IntPtr)1;
                    }

                    CheckAndExecuteSlideMacro(mouseVk);
                }
            }
            return CallNextHookEx(_mHook, nCode, wParam, lParam);
        }

        private void HandleKeybindCaptured(ushort vk, string text)
        {
            Dispatcher.Invoke(() =>
            {
                string formattedText = text.StartsWith("[") ? text : $"[ {text} ]";
                if (_isBindingTrigger)
                {
                    _config.TriggerVk = vk;
                    _config.TriggerText = formattedText;
                    BtnTriggerKey.Content = formattedText;
                    _isBindingTrigger = false;
                }
                else if (_isBindingCrouch)
                {
                    _config.CrouchVk = vk;
                    _config.CrouchText = formattedText;
                    BtnCrouchKey.Content = formattedText;
                    _isBindingCrouch = false;
                }
                SaveConfig();
            });
        }

        private bool _isExecuting = false;

        private void CheckAndExecuteSlideMacro(int vk)
        {
            try
            {
                if (!_config.SlideMacroEnabled) return;
                if (_isExecuting) return;

                if (_config.TriggerVk > 0 && vk == (int)_config.TriggerVk)
                {
                    bool isWPressed = (GetAsyncKeyState(VK_W_KEY) & 0x8000) != 0;
                    bool isAPressed = (GetAsyncKeyState(VK_A_KEY) & 0x8000) != 0;
                    bool isDPressed = (GetAsyncKeyState(VK_D_KEY) & 0x8000) != 0;
                    bool isShiftPressed = (GetAsyncKeyState(VK_SHIFT_KEY) & 0x8000) != 0;

                    bool isMoving = isWPressed || isAPressed || isDPressed;

                    if (!_config.RequireSprint || (isMoving && isShiftPressed))
                    {
                        _isExecuting = true;

                        byte crouchByte = (byte)(_config.CrouchVk > 0 ? _config.CrouchVk : 67);
                        byte crouchScan = (byte)MapVirtualKey((uint)crouchByte, 0);
                        if (crouchScan == 0) crouchScan = (crouchByte == 67) ? (byte)0x2E : (byte)0x1D;

                        byte wScan = (byte)MapVirtualKey(VK_W_KEY, 0); if (wScan == 0) wScan = 0x11;
                        byte aScan = (byte)MapVirtualKey(VK_A_KEY, 0); if (aScan == 0) aScan = 0x1E;
                        byte dScan = (byte)MapVirtualKey(VK_D_KEY, 0); if (dScan == 0) dScan = 0x20;

                        byte shiftScan = (byte)MapVirtualKey(VK_SHIFT_KEY, 0); if (shiftScan == 0) shiftScan = 0x2A;
                        byte spaceScan = (byte)MapVirtualKey(VK_SPACE_KEY, 0); if (spaceScan == 0) spaceScan = 0x39;
                        byte zScan = (byte)MapVirtualKey(VK_Z_KEY, 0); if (zScan == 0) zScan = 0x2C;

                        int delay = (int)_config.SlideDelayMs;
                        if (delay < 15) delay = 50;
                        if (delay > 200) delay = 200;

                        int mode = _config.SlideMode;

                        ThreadPool.QueueUserWorkItem(_ =>
                        {
                            try
                            {
                                // Briefly release active movement keys to break momentum
                                if (isWPressed) keybd_event(VK_W_KEY, wScan, KEYEVENTF_SCANCODE_FLAG | KEYEVENTF_KEYUP_FLAG, 0);
                                if (isAPressed) keybd_event(VK_A_KEY, aScan, KEYEVENTF_SCANCODE_FLAG | KEYEVENTF_KEYUP_FLAG, 0);
                                if (isDPressed) keybd_event(VK_D_KEY, dScan, KEYEVENTF_SCANCODE_FLAG | KEYEVENTF_KEYUP_FLAG, 0);

                                Thread.Sleep(6);

                                keybd_event(crouchByte, crouchScan, KEYEVENTF_SCANCODE_FLAG, 0);
                                Thread.Sleep(delay);
                                keybd_event(crouchByte, crouchScan, KEYEVENTF_SCANCODE_FLAG | KEYEVENTF_KEYUP_FLAG, 0);

                                if (isWPressed) keybd_event(VK_W_KEY, wScan, KEYEVENTF_SCANCODE_FLAG, 0);
                                if (isAPressed) keybd_event(VK_A_KEY, aScan, KEYEVENTF_SCANCODE_FLAG, 0);
                                if (isDPressed) keybd_event(VK_D_KEY, dScan, KEYEVENTF_SCANCODE_FLAG, 0);
                                keybd_event(VK_SHIFT_KEY, shiftScan, KEYEVENTF_SCANCODE_FLAG, 0);

                                // Automatic Stand-Up tap
                                Thread.Sleep(15);
                                keybd_event(crouchByte, crouchScan, KEYEVENTF_SCANCODE_FLAG, 0);
                                Thread.Sleep(15);
                                keybd_event(crouchByte, crouchScan, KEYEVENTF_SCANCODE_FLAG | KEYEVENTF_KEYUP_FLAG, 0);
                            }
                            catch
                            {
                            }
                            finally
                            {
                                Thread.Sleep(80);
                                _isExecuting = false;
                            }
                        });
                    }
                }
            }
            catch
            {
                _isExecuting = false;
            }
        }
        #endregion

        #region UI Event Handlers
        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                this.DragMove();
        }

        private void Minimize_Click(object sender, RoutedEventArgs e)
        {
            if (_config.MinimizeToTray)
            {
                this.Hide();
            }
            else
            {
                this.ShowInTaskbar = true;
                this.WindowState = WindowState.Minimized;
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void OnMacroToggleChanged(object sender, RoutedEventArgs e)
        {
            _config.SlideMacroEnabled = ToggleMacroEnabled.IsChecked == true;
            UpdateStatusUi();
            SaveConfig();
        }

        private void OnConfigChanged(object sender, RoutedEventArgs e)
        {
            _config.RequireSprint = ToggleRequireSprint.IsChecked == true;
            if (ToggleMinimizeToTray != null)
            {
                _config.MinimizeToTray = ToggleMinimizeToTray.IsChecked == true;
                if (!_config.MinimizeToTray)
                {
                    this.ShowInTaskbar = true;
                }
            }
            SaveConfig();
        }

        private void OnStartTriggerBind(object sender, RoutedEventArgs e)
        {
            _isBindingTrigger = true;
            _isBindingCrouch = false;
            BtnTriggerKey.Content = "[ Press Key... ]";
        }

        private void OnStartCrouchBind(object sender, RoutedEventArgs e)
        {
            _isBindingCrouch = true;
            _isBindingTrigger = false;
            BtnCrouchKey.Content = "[ Press Key... ]";
        }

        private void OnDelayValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtDelayValue != null)
            {
                _config.SlideDelayMs = e.NewValue;
                TxtDelayValue.Text = $"{e.NewValue:F0} ms";
                SaveConfig();
            }
        }
        #endregion
    }
}
