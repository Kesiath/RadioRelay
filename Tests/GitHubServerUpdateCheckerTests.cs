using RadioRelay.Server;

namespace RadioRelay.Tests;

public class GitHubServerUpdateCheckerTests
{
    [Theory]
    [InlineData("1.6.2", "1.6.3", true)]
    [InlineData("1.6.2", "v1.7.0", true)]
    [InlineData("1.6.2", "1.6.2", false)]
    [InlineData("1.6.2", "1.6.1", false)]
    public void Server_update_comparison_handles_release_tags(
        string currentVersion,
        string releaseVersion,
        bool expected)
    {
        Assert.Equal(expected, GitHubServerUpdateChecker.IsNewerVersion(currentVersion, releaseVersion));
    }
}
