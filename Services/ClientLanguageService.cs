using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace v232.Launcher.WPF.Services
{
    public enum ClientLanguage { EN, TH }

    // Use the existing bootstrap switch: no DLL swapping, injection or restart.
    // Missing/corrupt preferences always default to the stable EN path.
    public static class ClientLanguageService
    {
        private static string ConfigPath(string directory) =>
            Path.Combine(Path.GetFullPath(directory), "clover-diagnostics.ini");

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern uint GetPrivateProfileString(string section, string key,
            string defaultValue, StringBuilder value, uint size, string path);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WritePrivateProfileString(string section, string key,
            string value, string path);

        public static ClientLanguage Load(string directory)
        {
            var value = new StringBuilder(16);
            GetPrivateProfileString("diagnostics", "disableThai", "1", value,
                (uint)value.Capacity, ConfigPath(directory));
            return value.ToString() == "0" ? ClientLanguage.TH : ClientLanguage.EN;
        }

        public static void Save(string directory, ClientLanguage language)
        {
            if (language != ClientLanguage.EN && language != ClientLanguage.TH)
                throw new ArgumentOutOfRangeException(nameof(language));
            if (!WritePrivateProfileString("diagnostics", "disableThai",
                language == ClientLanguage.TH ? "0" : "1", ConfigPath(directory)))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot save the client language.");
            if (Load(directory) != language)
                throw new IOException("Client language verification failed.");
        }

        public static void PrepareLaunch(string directory)
        {
            // Materialize the safe default before Canvas reads its legacy default.
            Save(directory, Load(directory));
        }
    }
}
