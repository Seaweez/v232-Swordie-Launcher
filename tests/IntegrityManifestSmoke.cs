using System;
using System.IO;
using System.Text;
using v232.Launcher.WPF.Services;

internal static class IntegrityManifestSmoke
{
    private static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "mstory-x-integrity-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "probe.bin"), "test", Encoding.ASCII);
            File.WriteAllText(
                Path.Combine(root, IntegrityService.ManifestFileName),
                "{\"format\":1,\"releaseId\":\"release-id-fixture\",\"files\":[{\"relativePath\":\"probe.bin\",\"length\":4,\"sha256\":\"9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08\"}]}",
                Encoding.UTF8);

            IntegrityVerificationResult result = IntegrityService.Verify(root);
            Console.WriteLine(result.ToUserMessage());
            if (!result.Passed || !string.Equals(result.Release, "release-id-fixture", StringComparison.Ordinal))
            {
                Console.Error.WriteLine("FAIL: releaseId/relativePath manifest was not accepted.");
                return 1;
            }

            Console.WriteLine("PASS: releaseId/relativePath manifest is verified with SHA-256.");
            return 0;
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch
            {
                // The test result is independent of cleanup.
            }
        }
    }
}
