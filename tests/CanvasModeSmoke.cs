using System;
using System.IO;
using v232.Launcher.WPF.Services;

internal static class CanvasModeSmoke
{
    private static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("Usage: CanvasModeSmoke <client-directory>");
            return 2;
        }

        string sourceRoot = Path.GetFullPath(args[0]);
        string testRoot = Path.Combine(Path.GetTempPath(), "mstory-x-canvas-mode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);

        try
        {
            foreach (string fileName in new[] { "Canvas.dll.TH", "Canvas.original.dll" })
                File.Copy(Path.Combine(sourceRoot, fileName), Path.Combine(testRoot, fileName));

            // Start from a proxy-active install with Thai disabled. Auto mode
            // must select Standard and retain a one-shot Proxy fallback.
            File.Copy(Path.Combine(sourceRoot, "Canvas.dll"), Path.Combine(testRoot, "Canvas.dll"));
            File.WriteAllText(
                Path.Combine(testRoot, "client.release.json"),
                "{\"format\":1,\"profile\":\"Online\",\"features\":{\"thaiChat\":false}}");

            CanvasModePlan auto = CanvasModeService.Prepare(testRoot);
            Require(auto.RequestedMode == CanvasMode.Auto, "Default mode must be Auto.");
            Require(auto.EffectiveMode == CanvasMode.Standard, "Thai-off Auto mode must prefer Standard Canvas.");
            Require(auto.FallbackMode == CanvasMode.Proxy, "Auto mode must expose the Proxy fallback.");
            Require(CanvasModeService.DetectActiveMode(testRoot) == CanvasMode.Standard, "Auto mode did not activate stock Canvas.");

            CanvasModePlan proxy = CanvasModeService.Prepare(testRoot, CanvasMode.Proxy);
            Require(proxy.EffectiveMode == CanvasMode.Proxy, "Explicit Proxy mode was not selected.");
            Require(CanvasModeService.DetectActiveMode(testRoot) == CanvasMode.Proxy, "Proxy mode did not activate the proxy Canvas.");

            // A public install may contain only stock Canvas.dll. The first
            // launch must preserve that stock file as the rollback source
            // without requiring another full-client download.
            string stockOnlyRoot = Path.Combine(testRoot, "stock-only");
            Directory.CreateDirectory(stockOnlyRoot);
            File.Copy(Path.Combine(sourceRoot, "Canvas.original.dll"), Path.Combine(stockOnlyRoot, "Canvas.dll"));
            File.WriteAllText(Path.Combine(stockOnlyRoot, "client.release.json"),
                "{\"format\":1,\"profile\":\"Online\",\"features\":{\"thaiChat\":false}}");
            CanvasModePlan stockOnly = CanvasModeService.Prepare(stockOnlyRoot, CanvasMode.Auto);
            Require(stockOnly.EffectiveMode == CanvasMode.Standard, "Stock-only Auto mode must remain Standard.");
            Require(File.Exists(Path.Combine(stockOnlyRoot, "Canvas.original.dll")), "Stock-only mode did not create the rollback source.");

            File.WriteAllText(Path.Combine(testRoot, "Canvas.dll"), "unknown");
            bool rejected = false;
            try { CanvasModeService.Prepare(testRoot, CanvasMode.Standard); }
            catch (InvalidOperationException) { rejected = true; }
            Require(rejected, "Unknown active Canvas bytes must fail closed.");

            Console.WriteLine("PASS: Canvas Auto/Standard/Proxy switching and unknown-byte fail-closed guard.");
            return 0;
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, true);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
