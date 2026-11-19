using System.Text.RegularExpressions;

namespace Llamachant.Count.Tests;

/// <summary>The dashboard page is built in, holds no numbers, and only talks to this app.</summary>
public sealed class DashboardTests
{
    [Fact]
    public void The_page_is_embedded() =>
        Assert.StartsWith("<!doctype html>", Dashboard.Html);

    [Fact]
    public void It_reads_the_stats_with_the_key_in_a_header_not_the_address()
    {
        Assert.Contains("fetch(`/count/stats?days=", Dashboard.Html);
        Assert.Contains("\"x-functions-key\"", Dashboard.Html);
        Assert.DoesNotContain("code=", Dashboard.Html);
    }

    [Fact]
    public void It_loads_nothing_from_elsewhere() =>
        Assert.DoesNotMatch(new Regex(@"(src|href)\s*=\s*""(https?:)?//", RegexOptions.IgnoreCase), Dashboard.Html);

    [Fact]
    public void Its_policy_lets_it_reach_only_this_app()
    {
        Assert.Contains("default-src 'none'", Dashboard.ContentSecurityPolicy);
        Assert.Contains("connect-src 'self'", Dashboard.ContentSecurityPolicy);
        Assert.Contains("frame-ancestors 'none'", Dashboard.ContentSecurityPolicy);
    }
}
