using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using v232.Launcher.WPF.Models;

// Marshal is in System.Runtime.InteropServices

namespace v232.Launcher.WPF.Services
{
    public class LoginService
    {
        private readonly static string sDllPath = "Localhost.dll";
        private readonly static string sThaiChatDllPath = "ThaiChatInputPatch.dll";
        private readonly static uint CREATE_SUSPENDED = 0x00000004;
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

        public struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public uint dwProcessId;
            public uint dwThreadId;
        }

        public struct STARTUPINFO
        {
            public uint cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public uint dwX;
            public uint dwY;
            public uint dwXSize;
            public uint dwYSize;
            public uint dwXCountChars;
            public uint dwYCountChars;
            public uint dwFillAttribute;
            public uint dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CreateProcess(string lpApplicationName, string lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
                        bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment,
                        string lpCurrentDirectory, ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern uint ResumeThread(IntPtr hThread);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint dwDesiredAccess, int bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern int CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr OpenEvent(uint dwDesiredAccess, bool bInheritHandle, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, IntPtr dwSize, uint flAllocationType, uint flProtect);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern int WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] buffer, uint size, int lpNumberOfBytesWritten);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr CreateRemoteThread(IntPtr hProcess, IntPtr lpThreadAttribute, IntPtr dwStackSize, IntPtr lpStartAddress,
            IntPtr lpParameter, uint dwCreationFlags, out IntPtr lpThreadId);

        #endregion

        private static int Inject(uint processID, string dllPath)
        {
            // Check if DLL exists first
            if (!File.Exists(dllPath))
            {
                MessageBox.Show($"DLL not found: {dllPath}", "Inject Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }

            if (processID == 0)
                return 1;

            IntPtr pLoadLibraryAddress = GetProcAddress(GetModuleHandle("Kernel32.dll"), "LoadLibraryA");
            if (pLoadLibraryAddress == (IntPtr)0)
                return 2;

            IntPtr processHandle = OpenProcess((0x2 | 0x8 | 0x10 | 0x20 | 0x400), 1, (uint)processID);
            if (processHandle == (IntPtr)0)
                return 3;

            // Allocate length + 1 for null terminator
            IntPtr lpAddress = VirtualAllocEx(processHandle, (IntPtr)null, (IntPtr)(dllPath.Length + 1), (0x1000 | 0x2000), 0X40);
            if (lpAddress == (IntPtr)0)
                return 4;

            byte[] bytes = Encoding.ASCII.GetBytes(dllPath + "\0"); // Add null terminator
            if (WriteProcessMemory(processHandle, lpAddress, bytes, (uint)bytes.Length, 0) == 0)
                return 5;

            IntPtr threadId;
            IntPtr hThread = CreateRemoteThread(processHandle, IntPtr.Zero, IntPtr.Zero, pLoadLibraryAddress, lpAddress, 0, out threadId);
            if (hThread == (IntPtr)0)
            {
                int err = Marshal.GetLastWin32Error();
                MessageBox.Show($"CreateRemoteThread failed.\nError: {err}\nDLL: {dllPath}", "Inject Error", MessageBoxButton.OK, MessageBoxImage.Error);
                CloseHandle(processHandle);
                return 6;
            }

            // Wait for DLL to load (max 10 seconds)
            WaitForSingleObject(hThread, 10000);

            CloseHandle(hThread);
            CloseHandle(processHandle);
            return 0;
        }

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
            LocalhostInjection,
            Resume,
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
                canvasPlan = CanvasModeService.Prepare(clientDirectory);
                Console.WriteLine(canvasPlan.Message);

                attempt = await LaunchMapleProcessAsync(clientDirectory).ConfigureAwait(false);
                if (attempt.Started)
                    return true;

                // An immediate native exit is the only failure that is safe to
                // retry with the alternate Canvas variant. Authentication,
                // injection, and CreateProcess errors are not Canvas-mode
                // problems and must not start a second game process.
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
                    $"Could not start the game.\n\nCanvas mode: {canvasPlan.EffectiveMode}\n{attempt.Message}\n\nEnsure MapleStory.exe, Localhost.dll, and the required DirectX DLLs are in the same full client folder.",
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
                // Enforce Windowed Mode in Registry to prevent silent crash on modern multi-refresh/high-DPI monitors.
                try
                {
                    using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Wizet\MapleStory"))
                    {
                        if (key != null)
                        {
                            key.SetValue("soScreenMode", 3, Microsoft.Win32.RegistryValueKind.DWord);
                            key.SetValue("WindowMode", 1, Microsoft.Win32.RegistryValueKind.DWord);
                        }
                    }
                }
                catch { }

                STARTUPINFO si = new STARTUPINFO();
                si.cb = (uint)Marshal.SizeOf(typeof(STARTUPINFO));
                PROCESS_INFORMATION pi = new PROCESS_INFORMATION();
                string maplePath = Path.Combine(clientDirectory, "MapleStory.exe");
                Environment.SetEnvironmentVariable("MAPLE_SERVER_IP", Configs.GetServerIP());
                bool created = CreateProcess(maplePath, $"\"{maplePath}\" WebStart {this.Token}", IntPtr.Zero, IntPtr.Zero, false, CREATE_SUSPENDED, IntPtr.Zero, clientDirectory, ref si, out pi);
                int createError = Marshal.GetLastWin32Error();
                Console.WriteLine($"CreateProcess result={created} error={createError} pid={pi.dwProcessId}");

                if (!created)
                {
                    return new LaunchAttemptResult
                    {
                        FailureKind = LaunchFailureKind.CreateProcess,
                        Message = $"CreateProcess failed with Windows error {createError}."
                    };
                }

                try
                {
                    string fullDllPath = Path.Combine(clientDirectory, sDllPath);
                    int injectionResult = Inject(pi.dwProcessId, fullDllPath);
                    if (injectionResult != 0)
                    {
                        TerminateProcess(pi.hProcess, 1);
                        return new LaunchAttemptResult
                        {
                            FailureKind = LaunchFailureKind.LocalhostInjection,
                            Message = $"Localhost.dll could not be loaded (code {injectionResult})."
                        };
                    }

                    // Thai input is deliberately disabled for the recovery
                    // release. The proxy, when selected, remains an optional
                    // Canvas runtime variant; no Thai DLL is injected here.
                    if (Configs.EnableThaiChatHook)
                    {
                        string fullThaiChatDllPath = Path.Combine(clientDirectory, sThaiChatDllPath);
                        int thaiChatInject = Inject(pi.dwProcessId, fullThaiChatDllPath);
                        if (thaiChatInject != 0)
                            Console.WriteLine($"Thai chat hook inject skipped/failed with code {thaiChatInject}");
                    }

                    if (ResumeThread(pi.hThread) == uint.MaxValue)
                    {
                        int resumeError = Marshal.GetLastWin32Error();
                        TerminateProcess(pi.hProcess, 1);
                        return new LaunchAttemptResult
                        {
                            FailureKind = LaunchFailureKind.Resume,
                            Message = $"MapleStory.exe could not be resumed (Windows error {resumeError})."
                        };
                    }

                    await Task.Delay(1500).ConfigureAwait(false);

                    uint exitCode;
                    if (GetExitCodeProcess(pi.hProcess, out exitCode) && exitCode != 259)
                    {
                        return new LaunchAttemptResult
                        {
                            FailureKind = LaunchFailureKind.ImmediateExit,
                            Message = $"MapleStory.exe exited immediately (code {exitCode})."
                        };
                    }

                    return new LaunchAttemptResult { FailureKind = LaunchFailureKind.None };
                }
                finally
                {
                    CloseHandle(pi.hThread);
                    CloseHandle(pi.hProcess);
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
