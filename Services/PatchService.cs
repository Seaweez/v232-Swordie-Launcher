using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using v232.Launcher.WPF.Models;

namespace v232.Launcher.WPF.Services
{
    public sealed class PatchResult
    {
        public bool Success { get; set; }
        public int FilesUpdated { get; set; }
        public string Message { get; set; }
        public bool ManifestFound { get; set; }

        public static PatchResult Skipped(string message) => new PatchResult
        {
            Success = true,
            FilesUpdated = 0,
            Message = message,
            ManifestFound = false
        };

        public static PatchResult Done(int count, string message = null) => new PatchResult
        {
            Success = true,
            FilesUpdated = count,
            Message = message ?? (count > 0 ? $"Updated {count} file(s) successfully." : "All files are up to date."),
            ManifestFound = true
        };

        public static PatchResult Failed(string error) => new PatchResult
        {
            Success = false,
            FilesUpdated = 0,
            Message = error,
            ManifestFound = false
        };
    }

    public static class PatchService
    {
        public const string DefaultManifestName = "clover.manifest.json";
        public const string DefaultBaseUrl = "https://clover-portal.203.159.94.158.sslip.io/downloads/";
        private const int BufferSize = 1024 * 1024;
        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };

        public static async Task<PatchResult> CheckAndApplyUpdatesAsync(
            string clientDirectory,
            Action<string, double> onProgress = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            string root = Path.GetFullPath(clientDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string baseUrl = ResolveBaseUrl(clientDirectory);
            string manifestUrl = baseUrl.TrimEnd('/') + "/" + DefaultManifestName;

            IntegrityManifest manifest = null;
            try
            {
                onProgress?.Invoke("Checking for updates...", 0.05);
                using (var response = await _http.GetAsync(manifestUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        return PatchResult.Skipped("Patch server manifest not found; starting with local files.");
                    }
                    else
                    {
                        byte[] rawBytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                        manifest = DeserializeManifest(rawBytes);
                    }
                }
            }
            catch (Exception ex)
            {
                // Network unavailable or offline: fail open so players can continue offline or on local setup
                Console.WriteLine($"[PatchService] Notice: Could not reach patch server: {ex.Message}");
                return PatchResult.Skipped("Could not contact patch server; running with existing files.");
            }

            if (manifest?.Files == null || manifest.Files.Count == 0)
            {
                return PatchResult.Skipped("No update entries in manifest.");
            }

            // Find files that need download
            var filesToUpdate = new List<IntegrityFile>();
            int totalEntries = manifest.Files.Count;
            int scanned = 0;

            foreach (IntegrityFile file in manifest.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                scanned++;
                onProgress?.Invoke($"Verifying files ({scanned}/{totalEntries})...", 0.10 + (0.30 * scanned / totalEntries));

                string relPath = !string.IsNullOrWhiteSpace(file.RelativePath) ? file.RelativePath : file.Path;
                if (string.IsNullOrWhiteSpace(relPath) || relPath.Contains(".."))
                    continue;

                string fullPath = Path.Combine(clientDirectory, relPath.Replace('/', '\\'));

                if (!File.Exists(fullPath))
                {
                    filesToUpdate.Add(file);
                    continue;
                }

                var fileInfo = new FileInfo(fullPath);
                if (file.Length > 0 && fileInfo.Length != file.Length)
                {
                    filesToUpdate.Add(file);
                    continue;
                }

                // For files < 60MB, verify hash
                if (fileInfo.Length < 60 * 1024 * 1024)
                {
                    string localHash = ComputeSha256(fullPath, cancellationToken);
                    if (!string.Equals(localHash, file.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        filesToUpdate.Add(file);
                    }
                }
            }

            if (filesToUpdate.Count == 0)
            {
                onProgress?.Invoke("All files are up to date.", 1.0);
                return PatchResult.Done(0, "All files are up to date.");
            }

            // Download files
            int updatedCount = 0;
            string downloadBase = baseUrl.TrimEnd('/') + "/";

            for (int i = 0; i < filesToUpdate.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IntegrityFile file = filesToUpdate[i];
                string relPath = !string.IsNullOrWhiteSpace(file.RelativePath) ? file.RelativePath : file.Path;
                string fullPath = Path.Combine(clientDirectory, relPath.Replace('/', '\\'));

                string dir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                string fileDownloadUrl = downloadBase + (downloadBase.Contains("downloads/clover") ? "files/" : "files/") + relPath.Replace('\\', '/');
                string progressMsg = $"Downloading {Path.GetFileName(relPath)} ({i + 1}/{filesToUpdate.Count})...";
                double progress = 0.40 + (0.55 * (i + 1) / filesToUpdate.Count);
                onProgress?.Invoke(progressMsg, progress);

                bool downloaded = await DownloadAndVerifyAsync(fileDownloadUrl, fullPath, file.Sha256, cancellationToken).ConfigureAwait(false);
                if (downloaded)
                    updatedCount++;
            }

            // Auto-heal Canvas mode if needed
            try
            {
                CanvasModeService.Prepare(clientDirectory);
            }
            catch { }

            onProgress?.Invoke("Update complete.", 1.0);
            return PatchResult.Done(updatedCount);
        }

        private static async Task<bool> DownloadAndVerifyAsync(string url, string destinationPath, string expectedHash, CancellationToken ct)
        {
            string stagePath = destinationPath + ".patch-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        Console.WriteLine($"[PatchService] Download failed HTTP {(int)response.StatusCode} for {url}");
                        return false;
                    }

                    using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var fs = new FileStream(stagePath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        await stream.CopyToAsync(fs, BufferSize, ct).ConfigureAwait(false);
                    }
                }

                if (!string.IsNullOrWhiteSpace(expectedHash))
                {
                    string hash = ComputeSha256(stagePath, ct);
                    if (!string.Equals(hash, expectedHash, StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine($"[PatchService] Hash mismatch on downloaded file {url}: expected {expectedHash}, got {hash}");
                        return false;
                    }
                }

                if (File.Exists(destinationPath))
                {
                    try
                    {
                        File.Replace(stagePath, destinationPath, null, true);
                    }
                    catch
                    {
                        File.Delete(destinationPath);
                        File.Move(stagePath, destinationPath);
                    }
                }
                else
                {
                    File.Move(stagePath, destinationPath);
                }

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PatchService] Exception downloading {url}: {ex.Message}");
                return false;
            }
            finally
            {
                if (File.Exists(stagePath))
                {
                    try { File.Delete(stagePath); } catch { }
                }
            }
        }

        private static string ResolveBaseUrl(string clientDirectory)
        {
            try
            {
                string releaseJson = Path.Combine(clientDirectory, "client.release.json");
                if (File.Exists(releaseJson))
                {
                    string content = File.ReadAllText(releaseJson);
                    var match = System.Text.RegularExpressions.Regex.Match(
                        content,
                        "\"patchUrl\"\\s*:\\s*\"([^\"]+)\"",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (match.Success)
                        return match.Groups[1].Value.Trim();
                }
            }
            catch { }

            return DefaultBaseUrl;
        }

        private static IntegrityManifest DeserializeManifest(byte[] manifestBytes)
        {
            var serializer = new DataContractJsonSerializer(typeof(IntegrityManifest));
            int offset = manifestBytes.Length >= 3 && manifestBytes[0] == 0xEF && manifestBytes[1] == 0xBB && manifestBytes[2] == 0xBF ? 3 : 0;
            using (var stream = new MemoryStream(manifestBytes, offset, manifestBytes.Length - offset, false))
            {
                return serializer.ReadObject(stream) as IntegrityManifest;
            }
        }

        private static string ComputeSha256(string path, CancellationToken cancellationToken)
        {
            using (var sha256 = SHA256.Create())
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan))
            {
                byte[] buffer = new byte[BufferSize];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    sha256.TransformBlock(buffer, 0, read, null, 0);
                }
                sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return BitConverter.ToString(sha256.Hash).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}
