using System.Text.RegularExpressions;

namespace Llamachant.Count.Tests;

/// <summary>The dashboard page is built in, holds no numbers, and only talks to this app and Microsoft's sign-in.</summary>
public sealed class DashboardTests
{
    [Fact]
    public void The_page_is_embedded() =>
        Assert.StartsWith("<!doctype html>", Dashboard.Html);

    [Fact]
    public void It_reads_the_stats_with_the_sign_in_in_a_header_not_the_address()
    {
        Assert.Contains("fetch(`/count/stats?days=", Dashboard.Html);
        Assert.Contains("Authorization: `Bearer ${t}`", Dashboard.Html);
        Assert.DoesNotContain("code=", Dashboard.Html);
        Assert.DoesNotContain("x-functions-key", Dashboard.Html);
    }

    [Fact]
    public void It_gets_the_sign_in_ids_filled_in_and_encoded()
    {
        var page = Dashboard.For(new SignIn("tenant-id", "client\"<id>", null, local: false));
        Assert.Contains("data-tenant=\"tenant-id\"", page);
        Assert.Contains("data-client=\"client&quot;&lt;id&gt;\"", page);
    }

    [Fact]
    public void Without_sign_in_set_up_the_ids_are_empty() =>
        Assert.Contains("data-tenant=\"\" data-client=\"\"", Dashboard.For(new SignIn(null, null, null, local: true)));

    [Fact]
    public void It_loads_nothing_from_elsewhere() =>
        Assert.DoesNotMatch(new Regex(@"(src|href)\s*=\s*""(https?:)?//", RegexOptions.IgnoreCase), Dashboard.Html);

    [Fact]
    public void Its_policy_lets_it_reach_only_this_app_and_the_sign_in()
    {
        Assert.Contains("default-src 'none'", Dashboard.ContentSecurityPolicy);
        Assert.Contains("connect-src 'self' https://login.microsoftonline.com;", Dashboard.ContentSecurityPolicy);
        Assert.Contains("frame-ancestors 'none'", Dashboard.ContentSecurityPolicy);
    }
}
