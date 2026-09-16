using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace v232.Launcher.WPF.Services
{
    public enum CanvasMode
    {
        Auto,
        Standard,
        Proxy
    }

    public sealed class CanvasModePlan
    {
        public CanvasMode RequestedMode { get; internal set; }
        public CanvasMode EffectiveMode { get; internal set; }
        public CanvasMode? FallbackMode { get; internal set; }
        public string Message { get; internal set; }
    }

    /// <summary>
    /// Keeps both known v232 Canvas variants available and changes only the
    /// active Canvas.dll immediately before the game starts. Unknown bytes are
    /// never overwritten.
    /// </summary>
    public static class CanvasModeService
    {
        public const string ActiveCanvasFileName = "Canvas.dll";
        public const string OriginalCanvasFileName = "Canvas.original.dll";
        public const string ProxyCanvasFileName = "Canvas.proxy.dll";
        public const string LegacyProxyCanvasFileName = "Canvas.dll.TH";

        // v232.2 stock Canvas.dll and the approved current x64 proxy.
        public const string StockCanvasSha256 = "b5824eb4ea8604ae72316a9ff3072561ec5561b00cdd99ffc9d3e9aed8f99279";
        public const string ProxyCanvasSha256 = "ee55bdc2aa362995d2b937ed0cf7f6d90275140d0ad9d964e469d41ac0a0e606";

        private const string ModeConfigFileName = "client.launcher.json";
        private const string ModeEnvironmentVariable = "MSTORY_CANVAS_MODE";

        public static CanvasModePlan Prepare(string clientDirectory)
        {
            string root = ValidateClientDirectory(clientDirectory);
            return Prepare(root, ReadRequestedMode(root));
        }

        public static CanvasModePlan Prepare(string clientDirectory, CanvasMode requestedMode)
        {
            string root = ValidateClientDirectory(clientDirectory);

            string activePath = Path.Combine(root, ActiveCanvasFileName);
            string activeHash = GetOptionalHash(activePath);
            CanvasMode? activeMode = ClassifyHash(activeHash);
            if (activeHash != null && !activeMode.HasValue)
            {
                // Do not overwrite an unknown client DLL. It may be a partially
                // patched or locally customized installation, and replacing it
                // here turns a diagnosable startup problem into data loss.
                throw new InvalidOperationException(
                    $"Canvas.dll has an unrecognized SHA-256 ({activeHash}). " +
                    "Repair the client from a verified Clover release before launching.");
            }

            string stockPath = EnsureStockAsset(root, activePath, activeMode);
            string proxyPath = EnsureProxyAsset(root, activePath, activeMode);
            bool hasStock = stockPath != null;
            bool hasProxy = proxyPath != null;

            CanvasMode effectiveMode;
            switch (requestedMode)
            {
                case CanvasMode.Standard:
                    if (!hasStock)
                        throw new InvalidOperationException("Standard Canvas is unavailable. Canvas.original.dll is missing or invalid.");
                    effectiveMode = CanvasMode.Standard;
                    break;
                case CanvasMode.Proxy:
                    if (!hasProxy)
                        throw new InvalidOperationException("Proxy Canvas is unavailable. Install the approved Canvas proxy asset first.");
                    effectiveMode = CanvasMode.Proxy;
                    break;
                default:
                    // Thai is disabled in the current Online metadata, so a
                    // clean install starts with stock Canvas. A machine that
                    // truly needs the proxy is retried once below if stock
                    // exits during startup.
                    bool preferProxy = IsThaiFeatureEnabled(root);
                    if (preferProxy && hasProxy)
                        effectiveMode = CanvasMode.Proxy;
                    else if (hasStock)
                        effectiveMode = CanvasMode.Standard;
                    else if (hasProxy)
                        effectiveMode = CanvasMode.Proxy;
                    else
                        throw new InvalidOperationException(
                            "Neither a known stock Canvas nor a known proxy Canvas is available.");
                    break;
            }

            string selectedPath = effectiveMode == CanvasMode.Standard ? stockPath : proxyPath;
            SwitchActiveCanvas(root, activePath, activeMode, selectedPath, effectiveMode);

            CanvasMode? fallback = null;
            if (requestedMode == CanvasMode.Auto)
            {
                CanvasMode alternate = effectiveMode == CanvasMode.Standard ? CanvasMode.Proxy : CanvasMode.Standard;
                if ((alternate == CanvasMode.Standard && hasStock) || (alternate == CanvasMode.Proxy && hasProxy))
                    fallback = alternate;
            }

            string fallbackText = fallback.HasValue
                ? $"; automatic fallback={fallback.Value} is available if startup exits immediately"
                : "; no alternate Canvas asset is available";
            return new CanvasModePlan
            {
                RequestedMode = requestedMode,
                EffectiveMode = effectiveMode,
                FallbackMode = fallback,
                Message = $"Canvas mode: {effectiveMode}{fallbackText}."
            };
        }

        public static CanvasModePlan EnsureOrHealCanvas(string clientDirectory)
        {
            try
            {
                return Prepare(clientDirectory);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CanvasModeService] Notice: Auto-heal deferred: {ex.Message}");
                return null;
            }
        }

        public static CanvasMode? DetectActiveMode(string clientDirectory)
        {
            string root = ValidateClientDirectory(clientDirectory);
            return ClassifyHash(GetOptionalHash(Path.Combine(root, ActiveCanvasFileName)));
        }

        public static CanvasMode ParseMode(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return CanvasMode.Auto;

            switch (value.Trim().ToLowerInvariant())
            {
                case "auto":
                    return CanvasMode.Auto;
                case "standard":
                case "stock":
                case "off":
                    return CanvasMode.Standard;
                case "proxy":
                case "thai":
                case "on":
                    return CanvasMode.Proxy;
                default:
                    throw new InvalidOperationException(
                        $"Unsupported Canvas mode '{value}'. Use auto, standard, or proxy.");
            }
        }

        private static CanvasMode ReadRequestedMode(string root)
        {
            string environmentValue = Environment.GetEnvironmentVariable(ModeEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(environmentValue))
                return ParseMode(environmentValue);

            string configPath = Path.Combine(root, ModeConfigFileName);
            if (File.Exists(configPath))
            {
                string config = File.ReadAllText(configPath);
                Match match = Regex.Match(
                    config,
                    "\\\"canvasMode\\\"\\s*:\\s*\\\"([^\\\"]+)\\\"",
                    RegexOptions.IgnoreCase);
                if (match.Success)
                    return ParseMode(match.Groups[1].Value);
            }

            return CanvasMode.Auto;
        }

        private static bool IsThaiFeatureEnabled(string root)
        {
            string metadataPath = Path.Combine(root, "client.release.json");
            if (!File.Exists(metadataPath))
                return false;

            string metadata = File.ReadAllText(metadataPath);
            Match match = Regex.Match(
                metadata,
                "\\\"thaiChat\\\"\\s*:\\s*(true|false)",
                RegexOptions.IgnoreCase);
            return match.Success && string.Equals(match.Groups[1].Value, "true", StringComparison.OrdinalIgnoreCase);
        }

        private static string EnsureStockAsset(string root, string activePath, CanvasMode? activeMode)
        {
            string originalPath = Path.Combine(root, OriginalCanvasFileName);
            if (File.Exists(originalPath))
            {
                AssertKnownFile(originalPath, StockCanvasSha256, "Canvas.original.dll");
                return originalPath;
            }

            string stockAlias = Path.Combine(root, "Canvas.stock.dll");
            if (File.Exists(stockAlias))
            {
                AssertKnownFile(stockAlias, StockCanvasSha256, "Canvas.stock.dll");
                CopyKnownFileIfAbsent(stockAlias, originalPath, StockCanvasSha256, "Canvas.original.dll");
                return originalPath;
            }

            if (activeMode == CanvasMode.Standard)
            {
                CopyKnownFileIfAbsent(activePath, originalPath, StockCanvasSha256, "Canvas.original.dll");
                return originalPath;
            }

            return null;
        }

        private static string EnsureProxyAsset(string root, string activePath, CanvasMode? activeMode)
        {
            string[] candidates =
            {
                Path.Combine(root, ProxyCanvasFileName),
                Path.Combine(root, LegacyProxyCanvasFileName)
            };

            foreach (string candidate in candidates)
            {
                if (!File.Exists(candidate))
                    continue;
                if (string.Equals(GetHash(candidate), ProxyCanvasSha256, StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }

            if (activeMode == CanvasMode.Proxy)
            {
                foreach (string candidate in candidates)
                {
                    if (!File.Exists(candidate))
                    {
                        CopyKnownFileIfAbsent(activePath, candidate, ProxyCanvasSha256, "Canvas proxy asset");
                        return candidate;
                    }
                }
            }

            return null;
        }

        private static void SwitchActiveCanvas(
            string root,
            string activePath,
            CanvasMode? activeMode,
            string sourcePath,
            CanvasMode desiredMode)
        {
            if (activeMode == desiredMode)
                return;

            EnsureGameIsNotRunning();

            if (sourcePath == null || !File.Exists(sourcePath))
                throw new InvalidOperationException($"Canvas source for {desiredMode} mode is missing.");

            AssertKnownFile(sourcePath,
                desiredMode == CanvasMode.Standard ? StockCanvasSha256 : ProxyCanvasSha256,
                $"Canvas {desiredMode} source");

            if (File.Exists(activePath) && !activeMode.HasValue)
            {
                Console.WriteLine("[CanvasModeService] Auto-healing: Overwriting unknown/corrupted Canvas.dll with verified file.");
            }

            string stagePath = Path.Combine(root, ".mstory-canvas-stage-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.Copy(sourcePath, stagePath, false);
                AssertKnownFile(stagePath,
                    desiredMode == CanvasMode.Standard ? StockCanvasSha256 : ProxyCanvasSha256,
                    "staged Canvas");

                if (File.Exists(activePath))
                {
                    try
                    {
                        File.Replace(stagePath, activePath, null, true);
                    }
                    catch
                    {
                        File.Delete(activePath);
                        File.Move(stagePath, activePath);
                    }
                }
                else
                {
                    File.Move(stagePath, activePath);
                }

                AssertKnownFile(activePath,
                    desiredMode == CanvasMode.Standard ? StockCanvasSha256 : ProxyCanvasSha256,
                    "active Canvas.dll");
            }
            finally
            {
                if (File.Exists(stagePath))
                    File.Delete(stagePath);
            }
        }

        private static void CopyKnownFileIfAbsent(string sourcePath, string destinationPath, string expectedHash, string context)
        {
            AssertKnownFile(sourcePath, expectedHash, context + " source");
            if (File.Exists(destinationPath))
            {
                AssertKnownFile(destinationPath, expectedHash, context);
                return;
            }

            string stagePath = destinationPath + ".stage-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.Copy(sourcePath, stagePath, false);
                AssertKnownFile(stagePath, expectedHash, context + " staged file");
                File.Move(stagePath, destinationPath);
                AssertKnownFile(destinationPath, expectedHash, context);
            }
            finally
            {
                if (File.Exists(stagePath))
                    File.Delete(stagePath);
            }
        }

        private static void AssertKnownFile(string path, string expectedHash, string context)
        {
            FileInfo info = new FileInfo(path);
            if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Attributes.HasFlag(FileAttributes.Directory))
                throw new InvalidOperationException($"{context} is missing or is not an ordinary file.");

            string actualHash = GetHash(path);
            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{context} has an unexpected SHA-256.");
        }

        private static string GetOptionalHash(string path)
        {
            if (!File.Exists(path))
                return null;
            return GetHash(path);
        }

        private static string GetHash(string path)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (SHA256 sha256 = SHA256.Create())
            {
                return BitConverter.ToString(sha256.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            }
        }

        private static CanvasMode? ClassifyHash(string hash)
        {
            if (hash == null)
                return null;
            if (string.Equals(hash, StockCanvasSha256, StringComparison.OrdinalIgnoreCase))
                return CanvasMode.Standard;
            if (string.Equals(hash, ProxyCanvasSha256, StringComparison.OrdinalIgnoreCase))
                return CanvasMode.Proxy;
            return null;
        }

        private static string ValidateClientDirectory(string clientDirectory)
        {
            if (string.IsNullOrWhiteSpace(clientDirectory))
                throw new ArgumentException("Client directory is required.", nameof(clientDirectory));

            string root = Path.GetFullPath(clientDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!Directory.Exists(root))
                throw new DirectoryNotFoundException($"Client directory does not exist: {root}");

            DirectoryInfo info = new DirectoryInfo(root);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Client directory must not be a reparse point.");
            return root;
        }

        private static void EnsureGameIsNotRunning()
        {
            Process[] processes = Process.GetProcessesByName("MapleStory");
            try
            {
                if (processes.Length > 0)
                    throw new InvalidOperationException("Close MapleStory before changing Canvas mode.");
            }
            finally
            {
                foreach (Process process in processes)
                    process.Dispose();
            }
        }
    }
}
