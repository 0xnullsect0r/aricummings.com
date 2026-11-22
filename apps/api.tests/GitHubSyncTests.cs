using Ari.Features.GitHub;
using System.Text.Json;
using Xunit;

namespace AriApi.Tests;

/// <summary>Regression: only the first 100 repos were cached, so the site showed 100 of 120.</summary>
public class GitHubSyncTests
{
    [Fact]
    public void Merges_every_page_in_order()
    {
        var first = JsonSerializer.Serialize(Enumerable.Range(1, 100).Select(id => new { id }));
        var second = JsonSerializer.Serialize(Enumerable.Range(101, 20).Select(id => new { id }));
        using var merged = JsonDocument.Parse(GitHubSyncJob.MergeRepoPages([first, second]));
        Assert.Equal(120, merged.RootElement.GetArrayLength());
        Assert.Equal(120, merged.RootElement[119].GetProperty("id").GetInt32());
    }

    [Fact]
    public void Refuses_a_page_that_is_not_an_array()
    {
        Assert.Throws<InvalidOperationException>(() => GitHubSyncJob.MergeRepoPages(["{\"message\":\"rate limited\"}"]));
    }
}
