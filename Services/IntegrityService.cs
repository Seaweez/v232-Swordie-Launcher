using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using v232.Launcher.WPF.Models;

namespace v232.Launcher.WPF.Services
{
    public sealed class IntegrityFailure
    {
        public string Path { get; set; }
        public string Reason { get; set; }
        public string Expected { get; set; }
        public string Actual { get; set; }
    }

    public sealed class IntegrityVerificationResult
    {
        public bool Passed { get; set; }
        public bool ManifestFound { get; set; }
        public string Release { get; set; }
        public TimeSpan Duration { get; set; }
        public List<IntegrityFailure> Failures { get; } = new List<IntegrityFailure>();

        public string ToUserMessage(int maxFailures = 8)
        {
            if (Passed)
                return $"Client files verified ({Release ?? "unknown release"}).";

            var builder = new StringBuilder();
            builder.AppendLine("Client integrity check failed.");
            if (!ManifestFound)
                builder.AppendLine("Integrity manifest is missing or unreadable.");

            foreach (IntegrityFailure failure in Failures.Take(maxFailures))
            {
                builder.Append("• ").Append(failure.Path).Append(": ").AppendLine(failure.Reason);
            }

            if (Failures.Count > maxFailures)
                builder.AppendLine($"…and {Failures.Count - maxFailures} more file(s).");

            return builder.ToString().TrimEnd();
        }
    }

    /// <summary>
    /// Verifies the local client against a release manifest. This protects
    /// against incomplete/corrupt installs and accidental edits. It is not a
    /// replacement for server-side anti-cheat because a local client can be
    /// modified by a determined attacker.
    /// </summary>
    public static class IntegrityService
    {
        public const string ManifestFileName = "client.integrity.json";
        private const int BufferSize = 1024 * 1024;

        public static Task<IntegrityVerificationResult> VerifyAsync(string clientDirectory, CancellationToken cancellationToken = default(CancellationToken))
        {
            return Task.Run(() => Verify(clientDirectory, cancellationToken), cancellationToken);
        }

        public static IntegrityVerificationResult Verify(string clientDirectory, CancellationToken cancellationToken = default(CancellationToken))
        {
            var started = DateTime.UtcNow;
            var result = new IntegrityVerificationResult();
            string root = Path.GetFullPath(clientDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string manifestPath = Path.Combine(clientDirectory, ManifestFileName);

            if (!File.Exists(manifestPath))
            {
                result.Failures.Add(new IntegrityFailure
                {
                    Path = ManifestFileName,
                    Reason = "missing"
                });
                result.Duration = DateTime.UtcNow - started;
                return result;
            }

            IntegrityManifest manifest;
            try
            {
                var serializer = new DataContractJsonSerializer(typeof(IntegrityManifest));
                byte[] manifestBytes = File.ReadAllBytes(manifestPath);
                int offset = manifestBytes.Length >= 3 && manifestBytes[0] == 0xEF && manifestBytes[1] == 0xBB && manifestBytes[2] == 0xBF ? 3 : 0;
                using (var stream = new MemoryStream(manifestBytes, offset, manifestBytes.Length - offset, false))
                {
                    manifest = serializer.ReadObject(stream) as IntegrityManifest;
                }
            }
            catch (Exception ex)
            {
                result.Failures.Add(new IntegrityFailure
                {
                    Path = ManifestFileName,
                    Reason = "invalid manifest: " + ex.Message
                });
                result.Duration = DateTime.UtcNow - started;
                return result;
            }

            result.ManifestFound = manifest != null;
            result.Release = !string.IsNullOrWhiteSpace(manifest?.Release) ? manifest.Release : manifest?.ReleaseId;
            bool algorithmIsCompatible = string.IsNullOrWhiteSpace(manifest?.Algorithm) || string.Equals(manifest.Algorithm, "SHA-256", StringComparison.OrdinalIgnoreCase);
            if (manifest == null || manifest.Format != 1 || !algorithmIsCompatible || manifest.Files == null)
            {
                result.Failures.Add(new IntegrityFailure
                {
                    Path = ManifestFileName,
                    Reason = "unsupported manifest format"
                });
                result.Duration = DateTime.UtcNow - started;
                return result;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (IntegrityFile entry in manifest.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string relativeValue = !string.IsNullOrWhiteSpace(entry?.RelativePath) ? entry.RelativePath : entry?.Path;
                string relative = relativeValue?.Replace('/', '\\');
                if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(".."))
                {
                    result.Failures.Add(new IntegrityFailure
                    {
                        Path = relative ?? "<empty>",
                        Reason = "unsafe relative path in manifest"
                    });
                    continue;
                }

                string fullPath = Path.GetFullPath(Path.Combine(clientDirectory, relative));
                if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    result.Failures.Add(new IntegrityFailure
                    {
                        Path = relative,
                        Reason = "path escapes client directory"
                    });
                    continue;
                }
                if (!seen.Add(relative))
                {
                    result.Failures.Add(new IntegrityFailure
                    {
                        Path = relative,
                        Reason = "duplicate manifest entry"
                    });
                    continue;
                }
                if (!File.Exists(fullPath))
                {
                    result.Failures.Add(new IntegrityFailure
                    {
                        Path = relative,
                        Reason = "missing"
                    });
                    continue;
                }

                var info = new FileInfo(fullPath);
                if (entry.Length < 0 || info.Length != entry.Length)
                {
                    result.Failures.Add(new IntegrityFailure
                    {
                        Path = relative,
                        Reason = "size mismatch",
                        Expected = entry.Length.ToString(),
                        Actual = info.Length.ToString()
                    });
                    continue;
                }

                string actualHash = ComputeSha256(fullPath, cancellationToken);
                if (!string.Equals(actualHash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    result.Failures.Add(new IntegrityFailure
                    {
                        Path = relative,
                        Reason = "SHA-256 mismatch",
                        Expected = entry.Sha256,
                        Actual = actualHash
                    });
                }
            }

            result.Passed = result.Failures.Count == 0 && seen.Count > 0;
            result.Duration = DateTime.UtcNow - started;
            return result;
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
