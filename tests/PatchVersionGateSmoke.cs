using System;
using System.IO;
using v232.Launcher.WPF.Models;
using v232.Launcher.WPF.Services;

internal static class PatchVersionGateSmoke
{
    private static int Main(string[] args)
    {
        if (args.Length == 1)
            return VerifyRemoteDowngradeIsNonMutating(Path.GetFullPath(args[0]));

        string root = Path.Combine(Path.GetTempPath(), "clover-patch-version-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            File.WriteAllText(
                Path.Combine(root, "client.release.json"),
                "{\"version\":\"1.0.2\",\"release\":\"v1.0.2-clover-golden-rc1\"}");

            var stale = new IntegrityManifest { Version = "1.0.1", ReleaseId = "v1.0.1-clover" };
            var current = new IntegrityManifest { Version = "1.0.2", ReleaseId = "v1.0.2-clover" };
            var future = new IntegrityManifest { Version = "1.0.3", ReleaseId = "v1.0.3-clover" };

            if (!PatchService.IsManifestDowngrade(root, stale))
            {
                Console.Error.WriteLine("FAIL: stale v1.0.1 manifest was not rejected for a v1.0.2 client.");
                return 1;
            }

            if (PatchService.IsManifestDowngrade(root, current) ||
                PatchService.IsManifestDowngrade(root, future))
            {
                Console.Error.WriteLine("FAIL: current or newer manifest was rejected.");
                return 1;
            }

            Console.WriteLine("PASS: patch version gate rejects downgrades and accepts current/newer manifests.");
            return 0;
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static int VerifyRemoteDowngradeIsNonMutating(string clientRoot)
    {
        string[] protectedFiles = { "Clover Launcher.exe", "client.release.json", "endpoint.json" };
        var before = new byte[protectedFiles.Length][];
        for (int i = 0; i < protectedFiles.Length; i++)
            before[i] = File.ReadAllBytes(Path.Combine(clientRoot, protectedFiles[i]));

        PatchResult result = PatchService.CheckAndApplyUpdatesAsync(clientRoot).GetAwaiter().GetResult();
        if (!result.Success || result.FilesUpdated != 0 || result.Message.IndexOf("older patch manifest", StringComparison.OrdinalIgnoreCase) < 0)
        {
            Console.Error.WriteLine("FAIL: live v1.0.1 manifest was not rejected: " + result.Message);
            return 1;
        }

        for (int i = 0; i < protectedFiles.Length; i++)
        {
            byte[] after = File.ReadAllBytes(Path.Combine(clientRoot, protectedFiles[i]));
            if (!ByteArraysEqual(before[i], after))
            {
                Console.Error.WriteLine("FAIL: downgrade check changed " + protectedFiles[i]);
                return 1;
            }
        }

        Console.WriteLine("PASS: live v1.0.1 manifest is rejected without changing the Golden client.");
        return 0;
    }

    private static bool ByteArraysEqual(byte[] left, byte[] right)
    {
        if (left.Length != right.Length)
            return false;
        for (int i = 0; i < left.Length; i++)
        {
            if (left[i] != right[i])
                return false;
        }
        return true;
    }
}
