using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using v232.Launcher.WPF.Services;

internal static class PatchReliabilitySmoke
{
    private static int failures;
    private static void Check(bool pass, string message) { if (!pass) { failures++; Console.WriteLine("FAIL: " + message); } }
    private static async Task<int> Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "clover-patch-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await Run(root, "download-failure", "runtime.dll", false, false, false);
            await Run(root, "missing-hash", "runtime.dll", true, false, false);
            await Run(root, "traversal", "../escaped.dll", false, false, false);
            await Run(root, "unchanged", "runtime.dll", false, true, false);
            await Run(root, "large-corruption", "asset.wz", false, false, true);
            await Run(root, "valid-download", "runtime.dll", false, false, false, true);
            await Run(root, "length-mismatch", "runtime.dll", false, false, false, true, true);
            Check(!File.Exists(Path.Combine(root, "escaped.dll")), "manifest must never escape client directory");
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine(failures == 0 ? "PASS: patch failures, path/hash validation, unchanged traffic and large-file integrity" : failures + " check(s) failed");
        return failures == 0 ? 0 : 1;
    }

    private static async Task Run(string root, string test, string relative, bool missingHash, bool unchanged, bool large,
        bool successfulResponse = false, bool incorrectLength = false)
    {
        string client = Path.Combine(root, test);
        Directory.CreateDirectory(client);
        byte[] expected = Encoding.ASCII.GetBytes("valid client data");
        string hash = Convert.ToHexString(SHA256.HashData(expected)).ToLowerInvariant();
        long size = expected.Length;
        if (incorrectLength) size++;
        if (unchanged) File.WriteAllBytes(Path.Combine(client, relative), expected);
        if (large)
        {
            using (var sparse = File.Create(Path.Combine(client, relative))) sparse.SetLength(61L * 1024 * 1024);
            size = 61L * 1024 * 1024;
        }
        var portProbe = new TcpListener(IPAddress.Loopback, 0);
        portProbe.Start();
        int port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
        portProbe.Stop();
        string url = "http://127.0.0.1:" + port + "/";
        File.WriteAllText(Path.Combine(client, "client.release.json"), JsonSerializer.Serialize(new { version = "1.0.2", patchUrl = url }));
        string manifest = JsonSerializer.Serialize(new { format = 1, version = "1.0.3", files = new[] { new { relativePath = relative, length = size, sha256 = missingHash ? "" : hash } } });
        int fileRequests = 0;
        using var listener = new HttpListener();
        listener.Prefixes.Add(url);
        listener.Start();
        var serving = Task.Run(async () =>
        {
            try
            {
                while (listener.IsListening)
                {
                    var request = await listener.GetContextAsync();
                    if (request.Request.Url.AbsolutePath.EndsWith("clover.manifest.json"))
                    {
                        byte[] data = Encoding.UTF8.GetBytes(manifest);
                        request.Response.ContentLength64 = data.Length;
                        await request.Response.OutputStream.WriteAsync(data);
                    }
                    else
                    {
                        fileRequests++;
                        request.Response.StatusCode = missingHash || successfulResponse ? 200 : 503;
                        if (missingHash || successfulResponse) await request.Response.OutputStream.WriteAsync(expected);
                    }
                    request.Response.Close();
                }
            }
            catch (HttpListenerException) { }
            catch (ObjectDisposedException) { }
        });
        var result = await PatchService.CheckAndApplyUpdatesAsync(client);
        listener.Stop();
        await serving;
        if (unchanged)
            Check(result.Success && result.FilesUpdated == 0 && fileRequests == 0, "unchanged files must not be downloaded");
        else if (successfulResponse && !incorrectLength)
            Check(result.Success && result.FilesUpdated == 1 &&
                File.ReadAllBytes(Path.Combine(client, relative)).AsSpan().SequenceEqual(expected), "valid download must be installed exactly");
        else
            Check(!result.Success, test + " must not be reported as a successful update");
        if (incorrectLength) Check(!File.Exists(Path.Combine(client, relative)), "wrong-length response must never be installed");
        if (missingHash || relative.Contains("..")) Check(fileRequests == 0, test + " must be rejected before downloading");
        if (large) Check(fileRequests == 1, "same-size large corrupted file must not skip integrity verification");
    }
}
