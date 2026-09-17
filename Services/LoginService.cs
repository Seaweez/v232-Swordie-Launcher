using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows;
using v232.Launcher.WPF.Models;

namespace v232.Launcher.WPF.Services
{
    public class LoginService
    {
        private readonly static uint EVENT_SYNCHRONIZE = 0x00100000;
        private readonly static uint WAIT_OBJECT_0 = 0x00000000;

        public Client CClient { get; set; }
        public string User { get; set; }
        public string Pass { get; set; }
        public string Token { get; set; }
        public bool Auth { get; set; }

        public LoginService()
        {
        }

        public LoginService(string user, string pass)
        {
            User = user;
            Pass = pass;
            Token = null;
            Auth = false;
            CClient = null;
        }

        #region Win32 API

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern int CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr OpenEvent(uint dwDesiredAccess, bool bInheritHandle, string lpName);

        #endregion

        private enum ThaiPatchStartupStatus
        {
            Ready,
            Failed,
            Timeout
        }

        private static bool IsEventSignaled(string eventName)
        {
            IntPtr handle = OpenEvent(EVENT_SYNCHRONIZE, false, eventName);
            if (handle == IntPtr.Zero)
                return false;
            try
            {
                return WaitForSingleObject(handle, 0) == WAIT_OBJECT_0;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        private static ThaiPatchStartupStatus WaitForThaiPatch(uint processID, int timeoutMilliseconds)
        {
            string prefix = $"Local\\MapleStoryX.V232ThaiPatch.";
            string readyEvent = $"{prefix}Ready.{processID}";
            string failedEvent = $"{prefix}Failed.{processID}";
            Stopwatch stopwatch = Stopwatch.StartNew();
            while (stopwatch.ElapsedMilliseconds < timeoutMilliseconds)
            {
                if (IsEventSignaled(readyEvent))
                    return ThaiPatchStartupStatus.Ready;
                if (IsEventSignaled(failedEvent))
                    return ThaiPatchStartupStatus.Failed;
                System.Threading.Thread.Sleep(20);
            }
            return ThaiPatchStartupStatus.Timeout;
        }

        private enum LaunchFailureKind
        {
            None,
            CreateProcess,
            ExistingProcess,
            ImmediateExit,
            Unknown
        }

        private sealed class LaunchAttemptResult
        {
            public LaunchFailureKind FailureKind { get; set; }
            public string Message { get; set; }
            public bool Started { get { return FailureKind == LaunchFailureKind.None; } }
        }

        private Task<byte> GetFileChecksum(string filename, string checksum)
        {
            this.CClient.Send(OutPackets.FileChecksum(filename, checksum));
            InPacket inPacket = this.CClient.Receive();
            inPacket.readInt();
            int num = (int)inPacket.readShort();
            return Task.FromResult(inPacket.readByte());
        }

        private async Task<bool> VerifyRemoteWzChecksumsAsync(string clientDirectory)
        {
            string[] wzFiles = Directory.GetFiles(clientDirectory, "*.wz", SearchOption.TopDirectoryOnly);
            if (wzFiles.Length != 29)
            {
                MessageBox.Show(
                    $"Expected 29 .wz files, found {wzFiles.Length}. Please repair the client before launching.",
                    "WZ Integrity Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            string[] wzIgnore = { "Effect", "Sound", "Morph", "Reactor", "String", "TamingMob", "Base" };
            const int bufferSize = 10 * 1024 * 1024;

            foreach (string wzFile in wzFiles.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                if (wzIgnore.Any(s => wzFile.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0))
                    continue;

                string fileName = Path.GetFileNameWithoutExtension(wzFile);
                byte[] fileHash;
                using (var md5 = MD5.Create())
                using (var fileStream = new FileStream(wzFile, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, FileOptions.SequentialScan))
                using (var bufferedStream = new BufferedStream(fileStream, bufferSize))
                {
                    fileHash = md5.ComputeHash(bufferedStream);
                }

                string hashString = BitConverter.ToString(fileHash).Replace("-", "").ToLowerInvariant();
                byte checkFileChecksum = await GetFileChecksum(fileName, hashString);
                if (checkFileChecksum == 1)
                {
                    MessageBox.Show(
                        $"The server rejected {fileName}.wz. Please repair the client before launching.",
                        "WZ Integrity Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return false;
                }
            }

            return true;
        }

        public async Task<bool> Authenticate()
        {
            Client authClient = null;
            try
            {
                authClient = new Client();
                if (!authClient.Connect())
                    return false;

                var result = await Handlers.SendAuthRequest(User, Pass, authClient);

                if (result.result == 0)
                {
                    Token = result.token;
                    Auth = true;
                    return true;
                }

                Auth = false;
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                Auth = false;
                return false;
            }
            finally
            {
                authClient?.Disconnect();
            }
        }

        public async Task<bool> RefreshAuthenticationForLaunchAsync()
        {
            Token = null;
            Auth = false;
            return await Authenticate();
        }

        public async Task<bool> LaunchMapleAsync()
        {
            string clientDirectory = AppContext.BaseDirectory;
            CanvasModePlan canvasPlan = null;
            LaunchAttemptResult attempt = null;

            try
            {
                ClientLanguageService.PrepareLaunch(clientDirectory);
                canvasPlan = CanvasModeService.Prepare(clientDirectory);
                Console.WriteLine(canvasPlan.Message);

                attempt = await LaunchMapleProcessAsync(clientDirectory).ConfigureAwait(false);
                if (attempt.Started)
                    return true;

                // An immediate native exit is the only failure that is safe to
                // retry with the alternate Canvas variant. Authentication and
                // process-start errors are not Canvas-mode problems and must
                // not start a second game process.
                if (attempt.FailureKind == LaunchFailureKind.ImmediateExit && canvasPlan.FallbackMode.HasValue)
                {
                    CanvasModePlan fallbackPlan = CanvasModeService.Prepare(clientDirectory, canvasPlan.FallbackMode.Value);
                    Console.WriteLine(fallbackPlan.Message);
                    LaunchAttemptResult fallbackAttempt = await LaunchMapleProcessAsync(clientDirectory).ConfigureAwait(false);
                    if (fallbackAttempt.Started)
                        return true;

                    attempt.Message = $"{attempt.Message}\nFallback {fallbackPlan.EffectiveMode} also failed: {fallbackAttempt.Message}";
                }

                MessageBox.Show(
                    $"Could not start the game.\n\nCanvas mode: {canvasPlan.EffectiveMode}\n{attempt.Message}\n\nEnsure MapleStory.exe, the BlackCipher folder, Canvas.original.dll, MStoryX.NetworkCompat.dll, and the required DirectX DLLs are in the same full client folder.",
                    "Launch Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return false;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Could not prepare or start the game.\n\nError: {ex.Message}\n\nRepair the client files and try again.",
                    "Launch Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return false;
            }
        }

        private async Task<LaunchAttemptResult> LaunchMapleProcessAsync(string clientDirectory)
        {
            try
            {
                string maplePath = Path.Combine(clientDirectory, "MapleStory.exe");
                if (!File.Exists(maplePath))
                {
                    return new LaunchAttemptResult
                    {
                        FailureKind = LaunchFailureKind.CreateProcess,
                        Message = "MapleStory.exe not found.\n\nPlease ensure Clover Launcher.exe is placed directly inside your full game client folder."
                    };
                }

                string[] requiredNgsRuntime =
                {
                    @"BlackCipher\BlackCall64.aes",
                    @"BlackCipher\BlackCipher64.aes",
                    @"BlackCipher\BlackXchg.aes",
                    @"BlackCipher\config.bc",
                    @"BlackCipher\CrashReporter_64.dll"
                };
                string[] missingNgsRuntime = requiredNgsRuntime
                    .Where(relativePath => !File.Exists(Path.Combine(clientDirectory, relativePath)))
                    .ToArray();
                if (missingNgsRuntime.Length > 0)
                {
                    return new LaunchAttemptResult
                    {
                        FailureKind = LaunchFailureKind.CreateProcess,
                        Message = "The BlackCipher/NGS runtime is incomplete. Missing:\n - " +
                                  string.Join("\n - ", missingNgsRuntime) +
                                  "\n\nRepair or reinstall the full Clover client, then press PLAY again."
                    };
                }

                // Never kill an unrelated game process. Ask the player to close
                // an existing instance so its save/logout path remains intact.
                if (Process.GetProcessesByName("MapleStory").Any() ||
                    Process.GetProcessesByName("BlackCipher").Any())
                {
                    return new LaunchAttemptResult
                    {
                        FailureKind = LaunchFailureKind.ExistingProcess,
                        Message = "MapleStory or BlackCipher is already running. Close it normally, then press PLAY again."
                    };
                }

                // 2. Enforce Windowed Mode and Direct3D9 DWM compatibility in Registry
                try
                {
                    using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Wizet\MapleStory"))
                    {
                        if (key != null)
                        {
                            key.SetValue("soScreenMode", 3, Microsoft.Win32.RegistryValueKind.DWord);
                            key.SetValue("ScreenMode", 3, Microsoft.Win32.RegistryValueKind.DWord);
                            key.SetValue("WindowMode", 1, Microsoft.Win32.RegistryValueKind.DWord);
                            key.SetValue("Resolution", 0, Microsoft.Win32.RegistryValueKind.DWord);
                            key.SetValue("GraphicDevice", 0, Microsoft.Win32.RegistryValueKind.DWord);
                            key.SetValue("ScreenQuality", 1, Microsoft.Win32.RegistryValueKind.DWord);
                        }
                    }
                    try
                    {
                        using (var hklmKey = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(@"SOFTWARE\WOW6432Node\Wizet\MapleStory"))
                        {
                            if (hklmKey != null)
                            {
                                hklmKey.SetValue("soScreenMode", 3, Microsoft.Win32.RegistryValueKind.DWord);
                                hklmKey.SetValue("ScreenMode", 3, Microsoft.Win32.RegistryValueKind.DWord);
                                hklmKey.SetValue("WindowMode", 1, Microsoft.Win32.RegistryValueKind.DWord);
                                hklmKey.SetValue("Resolution", 0, Microsoft.Win32.RegistryValueKind.DWord);
                                hklmKey.SetValue("GraphicDevice", 0, Microsoft.Win32.RegistryValueKind.DWord);
                            }
                        }
                    }
                    catch { }
                    using (var compatKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers"))
                    {
                        if (compatKey != null)
                        {
                            compatKey.SetValue(maplePath, "~ DISABLEDXMAXIMIZEDWINDOWEDMODE HIGHDPIAWARE", Microsoft.Win32.RegistryValueKind.String);
                        }
                    }
                }
                catch { }

                var startInfo = new ProcessStartInfo
                {
                    FileName = maplePath,
                    Arguments = $"WebStart {this.Token}",
                    WorkingDirectory = clientDirectory,
                    UseShellExecute = true,
                    Verb = "runas"
                };

                using (Process gameProcess = Process.Start(startInfo))
                {
                    if (gameProcess == null)
                    {
                        return new LaunchAttemptResult
                        {
                            FailureKind = LaunchFailureKind.CreateProcess,
                            Message = "Windows did not return a game process."
                        };
                    }

                    Console.WriteLine($"Normal game start pid={gameProcess.Id}");
                    // NGS initialization and the in-process Canvas bootstrap can
                    // fail several seconds after Process.Start returns.
                    await Task.Delay(8000).ConfigureAwait(false);

                    if (gameProcess.HasExited)
                    {
                        bool isDiscordRunning = false;
                        try { isDiscordRunning = Process.GetProcessesByName("Discord").Length > 0; } catch { }
                        string discordWarning = isDiscordRunning 
                            ? "\n\n⚠️ ตรวจพบโปรแกรม Discord กำลังเปิดอยู่!\nหากเปิด 'In-Game Overlay' ไว้จะทำให้เกม Direct3D9 เด้งดับทันที\nวิธีแก้: ไปที่ Discord Settings -> Game Overlay -> ปิด Enable In-Game Overlay แล้วลองใหม่"
                            : "";

                        return new LaunchAttemptResult
                        {
                            FailureKind = LaunchFailureKind.ImmediateExit,
                            Message = $"MapleStory.exe exited during startup (code {gameProcess.ExitCode}).{discordWarning}\n\nThe launcher used the normal zero-injection start path. Check the newest crash report before retrying."
                        };
                    }

                    return new LaunchAttemptResult { FailureKind = LaunchFailureKind.None };
                }
            }
            catch (Exception ex)
            {
                return new LaunchAttemptResult
                {
                    FailureKind = LaunchFailureKind.Unknown,
                    Message = ex.Message
                };
            }
        }

        public bool LaunchMaple()
        {
            return LaunchMapleAsync().GetAwaiter().GetResult();
        }
    }
}
