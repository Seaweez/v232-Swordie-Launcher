using System;
using System.IO;
using System.Reflection;
using v232.Launcher.WPF.Models;

internal static class LauncherOnlineModeSmoke
{
    private const string OnlineHost = "203.159.94.158";

    private static int Main()
    {
        string metadataPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "client.release.json");
        File.WriteAllText(metadataPath,
            "{\"format\":1,\"release\":\"test-online\",\"profile\":\"Online\",\"server\":{\"host\":\"" + OnlineHost + "\",\"port\":8483}}");

        try
        {
            if (Configs.LocalLogin)
                throw new InvalidOperationException("Online metadata must disable LocalLogin.");

            if (!string.Equals(Configs.GetServerIP(), OnlineHost, StringComparison.Ordinal))
                throw new InvalidOperationException("Online metadata host was not selected.");

            PropertyInfo remoteWzChecksums = typeof(Configs).GetProperty("RemoteWzChecksums", BindingFlags.Public | BindingFlags.Static);
            if (remoteWzChecksums == null || (bool)remoteWzChecksums.GetValue(null, null))
                throw new InvalidOperationException("Remote WZ checksums must be opt-in when the Online metadata does not enable them.");

            Console.WriteLine("PASS: Online metadata selects the .158 endpoint.");
            return 0;
        }
        finally
        {
            File.Delete(metadataPath);
        }
    }
}
