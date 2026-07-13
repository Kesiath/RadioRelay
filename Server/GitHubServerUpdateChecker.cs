using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace RadioRelay.Server
{
    internal sealed record ServerReleaseStatus(string LatestVersion, string ReleaseUrl, bool UpdateAvailable);

    internal static class GitHubServerUpdateChecker
    {
        private const string LatestReleaseApiUrl =
            "https://api.github.com/repos/Kesiath/RadioRelay/releases/latest";

        private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(5) };

        internal static async Task<ServerReleaseStatus?> CheckAsync(
            string currentVersion,
            CancellationToken cancellationToken = default)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApiUrl);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.UserAgent.ParseAdd($"RadioRelay-Server/{currentVersion}");

            using var response = await Client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(
                stream,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (release == null || string.IsNullOrWhiteSpace(release.TagName)) return null;

            string latest = release.TagName.Trim().TrimStart('v', 'V');
            bool updateAvailable = IsNewerVersion(currentVersion, latest);
            return new ServerReleaseStatus(latest, release.HtmlUrl, updateAvailable);
        }

        internal static bool IsNewerVersion(string currentVersion, string releaseVersion) =>
            Version.TryParse(currentVersion.Trim().TrimStart('v', 'V'), out var current) &&
            Version.TryParse(releaseVersion.Trim().TrimStart('v', 'V'), out var latest) &&
            latest > current;

        private sealed class GitHubRelease
        {
            [JsonPropertyName("tag_name")]
            public string TagName { get; set; } = "";

            [JsonPropertyName("html_url")]
            public string HtmlUrl { get; set; } = "";
        }
    }
}
