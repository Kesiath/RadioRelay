using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace RadioRelay.Client
{
    internal sealed record AvailableUpdate(string Version, string ReleaseUrl);

    internal static class GitHubUpdateChecker
    {
        private const string LatestReleaseApiUrl =
            "https://api.github.com/repos/Kesiath/RadioRelay/releases/latest";

        private static readonly HttpClient Client = new()
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        internal static async Task<AvailableUpdate?> CheckAsync(
            string currentVersion,
            CancellationToken cancellationToken = default)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApiUrl);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.UserAgent.ParseAdd($"RadioRelay/{currentVersion}");

            using var response = await Client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(
                stream,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (release == null || string.IsNullOrWhiteSpace(release.HtmlUrl)) return null;

            var normalizedTag = release.TagName.Trim().TrimStart('v', 'V');
            if (!IsNewerVersion(currentVersion, release.TagName)) return null;

            return new AvailableUpdate(normalizedTag, release.HtmlUrl);
        }

        internal static bool IsNewerVersion(string currentVersion, string releaseTag)
        {
            var normalizedTag = releaseTag.Trim().TrimStart('v', 'V');
            return Version.TryParse(currentVersion, out var current) &&
                   Version.TryParse(normalizedTag, out var latest) &&
                   latest > current;
        }

        private sealed class GitHubRelease
        {
            [JsonPropertyName("tag_name")]
            public string TagName { get; set; } = "";

            [JsonPropertyName("html_url")]
            public string HtmlUrl { get; set; } = "";
        }
    }
}
