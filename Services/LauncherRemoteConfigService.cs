using System;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using v232.Launcher.WPF.Models;

namespace v232.Launcher.WPF.Services
{
    public class LauncherRemoteConfigService
    {
        private static readonly Lazy<LauncherRemoteConfigService> _instance =
            new Lazy<LauncherRemoteConfigService>(() => new LauncherRemoteConfigService());
        public static LauncherRemoteConfigService Instance => _instance.Value;

        // Default fallbacks
        public string DiscordUrl { get; private set; } = "";
        public string FacebookUrl { get; private set; } = "";
        public string WebsiteUrl { get; private set; } = "https://mstory-x.com";
        public string AnnouncementText { get; private set; } = "";
        public string LatestNewsTitle { get; private set; } = "";
        public string LatestNewsUrl { get; private set; } = "";

        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };

        public async Task RefreshAsync()
        {
            try
            {
                string configUrl = Configs.GetConfigUrl();
                if (string.IsNullOrWhiteSpace(configUrl))
                    return;

                string json = await _http.GetStringAsync(configUrl).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(json))
                    return;

                // 1. Parse links.discord
                Match mDiscord = Regex.Match(json, "\"discord\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
                if (mDiscord.Success && !string.IsNullOrWhiteSpace(mDiscord.Groups[1].Value))
                    DiscordUrl = mDiscord.Groups[1].Value.Trim();

                // 2. Parse links.facebook
                Match mFb = Regex.Match(json, "\"facebook\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
                if (mFb.Success && !string.IsNullOrWhiteSpace(mFb.Groups[1].Value))
                    FacebookUrl = mFb.Groups[1].Value.Trim();

                // 3. Parse links.website
                Match mWeb = Regex.Match(json, "\"website\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
                if (mWeb.Success && !string.IsNullOrWhiteSpace(mWeb.Groups[1].Value))
                    WebsiteUrl = mWeb.Groups[1].Value.Trim();

                // 4. Parse announcement
                Match mAnnounce = Regex.Match(json, "\"announcement\"\\s*:\\s*\\{[^}]*\"text\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
                if (mAnnounce.Success && !string.IsNullOrWhiteSpace(mAnnounce.Groups[1].Value))
                    AnnouncementText = Regex.Unescape(mAnnounce.Groups[1].Value.Trim());

                // 5. Parse latest news title and url
                Match mNewsTitle = Regex.Match(json, "\"title\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
                if (mNewsTitle.Success && !string.IsNullOrWhiteSpace(mNewsTitle.Groups[1].Value))
                    LatestNewsTitle = Regex.Unescape(mNewsTitle.Groups[1].Value.Trim());

                Match mNewsUrl = Regex.Match(json, "\"news\"\\s*:\\s*\\[[^\\]]*\"url\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
                if (mNewsUrl.Success && !string.IsNullOrWhiteSpace(mNewsUrl.Groups[1].Value))
                    LatestNewsUrl = mNewsUrl.Groups[1].Value.Trim();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RemoteConfig] Warning: Failed to fetch remote config: {ex.Message}");
            }
        }
    }
}
