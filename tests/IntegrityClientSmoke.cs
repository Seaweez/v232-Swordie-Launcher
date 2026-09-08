using System;
using v232.Launcher.WPF.Services;

internal static class IntegrityClientSmoke
{
    private static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("Usage: IntegrityClientSmoke <client-directory>");
            return 2;
        }

        IntegrityVerificationResult result = IntegrityService.Verify(args[0]);
        Console.WriteLine(result.ToUserMessage(16));
        Console.WriteLine("Passed={0}; ManifestFound={1}; FilesWithFailures={2}; Duration={3}", result.Passed, result.ManifestFound, result.Failures.Count, result.Duration);
        return result.Passed ? 0 : 1;
    }
}
