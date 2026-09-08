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
                    ProcessStartInfo proc = new ProcessStartInfo
                    {
                        UseShellExecute = true,
                        WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
                        FileName = Process.GetCurrentProcess().MainModule.FileName,
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

            base.OnStartup(e);
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
