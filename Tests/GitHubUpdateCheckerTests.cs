using RadioRelay.Client;

namespace RadioRelay.Tests;

public class GitHubUpdateCheckerTests
{
    [Theory]
    [InlineData("1.5.8", "v1.5.9", true)]
    [InlineData("1.5.8", "1.6.0", true)]
    [InlineData("1.5.8", "v1.5.8", false)]
    [InlineData("1.5.8", "v1.5.7", false)]
    [InlineData("1.5.8", "not-a-version", false)]
    public void Release_tags_are_compared_to_the_current_client_version(
        string currentVersion,
        string releaseTag,
        bool expected)
    {
        Assert.Equal(expected, GitHubUpdateChecker.IsNewerVersion(currentVersion, releaseTag));
    }
}
