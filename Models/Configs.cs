using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace v232.Launcher.WPF.Models
{
    public static class Configs
    {
        // The client.release.json beside the launcher selects the runtime
        // profile. A missing file or LocalTest profile remains local-only;
        // the Online profile uses its declared server endpoint.
        public static bool LocalLogin
        {
            get { return !string.Equals(ReadMetadataValue("profile"), "Online", StringComparison.OrdinalIgnoreCase); }
        }

        // The current Online API does not expose the legacy per-WZ checksum
        // request. Keep that extra round-trip disabled unless the release
        // metadata explicitly advertises support for it.
        public static bool RemoteWzChecksums
        {
            get { return string.Equals(ReadMetadataValue("remoteWzChecksums"), "true", StringComparison.OrdinalIgnoreCase); }
        }

        // The Thai client hook is an instrumented development build. Keep it
        // opt-in so a normal launch uses the untouched v232 window/input path.
        // Set MAPLE_ENABLE_THAI_CHAT_HOOK=1 only for hook diagnostics.
        public static bool EnableThaiChatHook = false;

        // Base64 encoded IPs - decode these to get actual IP
        // To encode: Convert.ToBase64String(Encoding.UTF8.GetBytes("127.0.0.1"))
        // To decode: Encoding.UTF8.GetString(Convert.FromBase64String(encoded))

        // Base64 encoded IPs - decode these to get actual IP
        // To encode: Convert.ToBase64String(Encoding.UTF8.GetBytes("127.0.0.1"))
        // To decode: Encoding.UTF8.GetString(Convert.FromBase64String(encoded))

        public static string LocalIP = "MTI3LjAuMC4x"; // Base64: 127.0.0.1
        public static string ServerIP = "MjAzLjE1OS45NC4xNTg="; // Base64: 203.159.94.158

        public static string WebServerToken = "djIxNF9VcGRhdGVyOk1hcGxldjIxNFVwZGF0ZXI3MTYhQA==";

        public static int APIServerPort
        {
            get { return GetAPIServerPort(); }
        }

        public static int WebServerPort = 80;

        /// <summary>
        /// Gets the decoded server IP based on LocalLogin setting
        /// </summary>
        public static string GetServerIP()
        {
            try
            {
                if (!LocalLogin)
                {
                    string metadataHost = ReadMetadataValue("server.host");
                    if (!string.IsNullOrWhiteSpace(metadataHost))
                        return metadataHost;
                }

                string encoded = LocalLogin ? LocalIP : ServerIP;
                return Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            }
            catch
            {
                return "127.0.0.1"; // Fallback
            }
        }

        public static int GetAPIServerPort()
        {
            try
            {
                // Launcher auth API (CLIENT_API_PORT). Game login uses 8484 separately.
                string portStr = ReadMetadataValue("server.port");
                if (!string.IsNullOrWhiteSpace(portStr) && int.TryParse(portStr, out int p))
                    return p;
                return 8483;
            }
            catch
            {
                return 8483;
            }
        }

        public static string GetConfigUrl()
        {
            try
            {
                string raw = ReadMetadataValue("configUrl");
                if (!string.IsNullOrWhiteSpace(raw))
                    return raw;

                string host = GetServerIP();
                if (host == "203.159.94.158" && GetAPIServerPort() == 9483)
                {
                    return "https://programmer-roster-attract-pcs.trycloudflare.com/api/site-config";
                }
                return "https://mstory-x.com/api/site_config.php";
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Public OBT open time: 2026-08-22 12:00 GMT+7. Override with client.release.json
        /// "publicOpenAt": "2026-08-22T12:00:00+07:00" or force "status": "maintenance".
        /// </summary>
        public static DateTimeOffset PublicOpenAt
        {
            get
            {
                string raw = ReadMetadataValue("publicOpenAt");
                if (!string.IsNullOrWhiteSpace(raw) && DateTimeOffset.TryParse(raw, out DateTimeOffset parsed))
                    return parsed;
                return new DateTimeOffset(2026, 8, 22, 15, 0, 0, TimeSpan.FromHours(7));
            }
        }

        public static bool IsPublicPlayOpen()
        {
            string status = ReadMetadataValue("status");
            if (string.Equals(status, "maintenance", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "offline", StringComparison.OrdinalIgnoreCase))
                return false;
            if (string.Equals(status, "online", StringComparison.OrdinalIgnoreCase))
                return true;
            return DateTimeOffset.Now >= PublicOpenAt;
        }

        public static bool IsStaffUsername(string username)
        {
            if (string.IsNullOrWhiteSpace(username))
                return false;
            string u = username.Trim().ToLowerInvariant();
            return u.StartsWith("msx") || u.StartsWith("admin") || u.StartsWith("test");
        }

        public static string PublicOpenMessage()
        {
            return "Server is currently under Maintenance (MA Mode) before OBT Launch.\nGrand Opening: August 22, 2026 at 15:00 (3:00 PM GMT+7)\n\nเซิร์ฟเวอร์อยู่ระหว่าง Maintenance ก่อนเปิด OBT\nเปิดให้เล่นอย่างเป็นทางการวันนี้ 22 ส.ค. 2569 เวลา 15:00 น. (บ่าย 3)\n(Only Authorized Staff / Testing Accounts Allowed)";
        }

        public static string GetBranding()
        {
            try
            {
                return ReadMetadataValue("branding") ?? "MStory : X";
            }
            catch
            {
                return "MStory : X";
            }
        }

        private static string ReadMetadataValue(string key)
        {
            try
            {
                string metadataPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "client.release.json");
                if (!File.Exists(metadataPath))
                    return null;

                string json = File.ReadAllText(metadataPath);
                string pattern;
                if (key == "server.host")
                {
                    pattern = "\\\"server\\\"\\s*:\\s*\\{[^}]*\\\"host\\\"\\s*:\\s*\\\"([^\\\"]+)\\\"";
                }
                else if (key == "server.port")
                {
                    pattern = "\\\"server\\\"\\s*:\\s*\\{[^}]*\\\"port\\\"\\s*:\\s*(\\d+)";
                }
                else
                {
                    pattern = "\\\"" + Regex.Escape(key) + "\\\"\\s*:\\s*\\\"([^\\\"]+)\\\"";
                }

                Match match = Regex.Match(json, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
                return match.Success ? match.Groups[1].Value.Trim() : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
