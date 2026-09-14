using System;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows;

namespace v232.Launcher.WPF
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // Enforce software rendering to eliminate WPF HwndTarget hardware acceleration / layered window deadlocks
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

            this.DispatcherUnhandledException += (s, args) =>
            {
                try
                {
                    System.IO.File.AppendAllText(
                        System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "launcher_error.log"),
                        $"[{DateTime.Now}] Dispatcher Unhandled: {args.Exception}\n");
                }
                catch { }
                args.Handled = true;
            };

            if (!IsAdministrator())
            {
                try
                {
                    string[] rawArgs = Environment.GetCommandLineArgs();
                    string forwardedArgs = rawArgs.Length > 1
                        ? string.Join(" ", System.Linq.Enumerable.Select(System.Linq.Enumerable.Skip(rawArgs, 1), a => $"\"{a}\""))
                        : "";

                    ProcessStartInfo proc = new ProcessStartInfo
                    {
                        UseShellExecute = true,
                        WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
                        FileName = Process.GetCurrentProcess().MainModule.FileName,
                        Arguments = forwardedArgs,
                        Verb = "runas"
                    };
                    Process.Start(proc);
                }
                catch
                {
                    // User declined UAC
                }
                Shutdown();
                return;
            }

            RegisterCloverProtocol();

            base.OnStartup(e);
        }

        private static void RegisterCloverProtocol()
        {
            try
            {
                string exePath = Process.GetCurrentProcess().MainModule.FileName;
                using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\clover"))
                {
                    if (key != null)
                    {
                        key.SetValue("", "URL:Clover Idle Story Protocol");
                        key.SetValue("URL Protocol", "");
                        using (var iconKey = key.CreateSubKey("DefaultIcon"))
                        {
                            iconKey?.SetValue("", $"\"{exePath}\",0");
                        }
                        using (var shellKey = key.CreateSubKey(@"shell\open\command"))
                        {
                            shellKey?.SetValue("", $"\"{exePath}\" \"%1\"");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Protocol] Registration notice: {ex.Message}");
            }
        }

        private static bool IsAdministrator()
        {
            try
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                {
                    WindowsPrincipal principal = new WindowsPrincipal(identity);
                    return principal.IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch
            {
                return false;
            }
        }
    }
}
