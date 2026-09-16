using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.Serialization.Json;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
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
        // Default patch server URL (can be overridden by client.release.json -> patchUrl)
        public const string DefaultBaseUrl = "https://clover-story.duckdns.org/downloads/";
        private const int BufferSize = 1024 * 1024;
        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };

        [DataContract]
        private sealed class VerifiedAsset
        {
            [DataMember] public long Length;
            [DataMember] public long WriteTicks;
            [DataMember] public string Hash;
        }

        // Performance cache for large assets only. Native/runtime files are always
        // hashed; this cache is not an authentication or code-signing boundary.
        private const string CacheName = "client.asset-hashes.json";
        private static Dictionary<string, VerifiedAsset> ReadAssetCache(string directory)
        {
            try
            {
                string path = Path.Combine(directory, CacheName);
                if (new FileInfo(path).Length > 4 * 1024 * 1024) return new Dictionary<string, VerifiedAsset>();
                using (var input = File.OpenRead(path))
                    return (Dictionary<string, VerifiedAsset>)new DataContractJsonSerializer(typeof(Dictionary<string, VerifiedAsset>)).ReadObject(input);
            }
            catch { return new Dictionary<string, VerifiedAsset>(); }
        }

        private static void WriteAssetCache(string directory, Dictionary<string, VerifiedAsset> cache)
        {
            try
            {
                using (var output = File.Create(Path.Combine(directory, CacheName)))
                    new DataContractJsonSerializer(typeof(Dictionary<string, VerifiedAsset>)).WriteObject(output, cache);
            }
            catch { /* A read-only client must remain usable; cache is optional. */ }
        }

        private static bool IsSafeEntry(string directory, IntegrityFile file)
        {
            if (file == null || file.Length < 0 || string.IsNullOrWhiteSpace(file.Sha256) ||
                !Regex.IsMatch(file.Sha256, "\\A[0-9a-fA-F]{64}\\z")) return false;
            string relative = !string.IsNullOrWhiteSpace(file.RelativePath) ? file.RelativePath : file.Path;
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) ||
                relative.Contains(":") || relative.Contains("..") || relative.Equals(CacheName, StringComparison.OrdinalIgnoreCase)) return false;
            foreach (string part in relative.Replace('\\', '/').Split('/'))
                if (string.IsNullOrWhiteSpace(part) || part == "." || part.EndsWith(".") || part.EndsWith(" ") ||
                    part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
            try
            {
                string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string path = Path.GetFullPath(Path.Combine(directory, relative.Replace('/', '\\')));
                if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
                for (string candidate = path; candidate.Length >= root.Length; candidate = Path.GetDirectoryName(candidate))
                    if ((File.Exists(candidate) || Directory.Exists(candidate)) &&
                        (File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0) return false;
                return true;
            }
            catch { return false; }
        }

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

            if (IsManifestDowngrade(clientDirectory, manifest))
            {
                string remoteVersion = FirstNonEmpty(manifest.Version, manifest.Release, manifest.ReleaseId);
                Console.WriteLine($"[PatchService] Ignoring older patch manifest {remoteVersion}.");
                return PatchResult.Done(0, $"Ignored older patch manifest {remoteVersion}; kept the installed client.");
            }

            // Validate the entire list before mutating any player files.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in manifest.Files)
            {
                if (!IsSafeEntry(clientDirectory, file)) return PatchResult.Failed("Invalid update path or checksum; no files were changed.");
                string relative = (!string.IsNullOrWhiteSpace(file.RelativePath) ? file.RelativePath : file.Path).Replace('/', '\\');
                if (!seen.Add(relative)) return PatchResult.Failed("Duplicate update entry; no files were changed.");
            }
            var assetCache = ReadAssetCache(clientDirectory) ?? new Dictionary<string, VerifiedAsset>();

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
                if (fileInfo.Length != file.Length)
                {
                    filesToUpdate.Add(file);
                    continue;
                }

                bool largeAsset = fileInfo.Length >= 60L * 1024 * 1024 &&
                    string.Equals(Path.GetExtension(fullPath), ".wz", StringComparison.OrdinalIgnoreCase);
                VerifiedAsset cached;
                if (largeAsset && assetCache.TryGetValue(relPath, out cached) && cached != null &&
                    cached.Length == fileInfo.Length && cached.WriteTicks == fileInfo.LastWriteTimeUtc.Ticks &&
                    string.Equals(cached.Hash, file.Sha256, StringComparison.OrdinalIgnoreCase)) continue;
                string localHash = ComputeSha256(fullPath, cancellationToken);
                if (!string.Equals(localHash, file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    filesToUpdate.Add(file);
                }
                else if (largeAsset)
                {
                    assetCache[relPath] = new VerifiedAsset { Length = fileInfo.Length, WriteTicks = fileInfo.LastWriteTimeUtc.Ticks, Hash = localHash };
                }
            }

            if (filesToUpdate.Count == 0)
            {
                WriteAssetCache(clientDirectory, assetCache);
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

                bool downloaded = await DownloadAndVerifyAsync(fileDownloadUrl, fullPath, file.Sha256, file.Length, cancellationToken).ConfigureAwait(false);
                if (downloaded)
                    updatedCount++;
                else
                    return PatchResult.Failed("Update incomplete: " + Path.GetFileName(relPath) + ". Please retry before playing.");
            }

            // Auto-heal Canvas mode if needed
            try
            {
                CanvasModeService.Prepare(clientDirectory);
            }
            catch { }

            onProgress?.Invoke("Update complete.", 1.0);
            WriteAssetCache(clientDirectory, assetCache);
            return PatchResult.Done(updatedCount);
        }

        private static async Task<bool> DownloadAndVerifyAsync(string url, string destinationPath, string expectedHash, long expectedLength, CancellationToken ct)
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

                if (new FileInfo(stagePath).Length != expectedLength) return false;

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

        public static bool IsManifestDowngrade(string clientDirectory, IntegrityManifest manifest)
        {
            if (manifest == null)
                return false;

            string localVersionText = null;
            try
            {
                string releaseJson = Path.Combine(clientDirectory, "client.release.json");
                if (File.Exists(releaseJson))
                {
                    string content = File.ReadAllText(releaseJson);
                    var match = Regex.Match(
                        content,
                        "\"version\"\\s*:\\s*\"([^\"]+)\"",
                        RegexOptions.IgnoreCase);
                    if (match.Success)
                        localVersionText = match.Groups[1].Value;
                }
            }
            catch
            {
                return false;
            }

            Version localVersion;
            if (!TryParseLooseVersion(localVersionText, out localVersion))
                return false;

            string remoteVersionText = FirstNonEmpty(manifest.Version, manifest.Release, manifest.ReleaseId);
            Version remoteVersion;
            if (!TryParseLooseVersion(remoteVersionText, out remoteVersion))
                return true;

            return remoteVersion < localVersion;
        }

        private static bool TryParseLooseVersion(string value, out Version version)
        {
            version = null;
            if (string.IsNullOrWhiteSpace(value))
                return false;

            var match = Regex.Match(value, @"\d+(?:\.\d+){1,3}");
            return match.Success && Version.TryParse(match.Value, out version);
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (string value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }

            return "unknown";
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
