namespace Llamachant.Count;

/// <summary>The dashboard page (dashboard.html, embedded) and the policy it's served with.</summary>
internal static class Dashboard
{
    /// <summary>
    /// Only its own inline script and style run, and it talks only to this app (the stats) and Microsoft's sign-in (for
    /// the token). Nothing may frame it, and forms post nowhere.
    /// </summary>
    public const string ContentSecurityPolicy =
        "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; " +
        "connect-src 'self' https://login.microsoftonline.com; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    private static readonly Lazy<string> html = new(() =>
    {
        using var stream = typeof(Dashboard).Assembly.GetManifestResourceStream("dashboard.html")
            ?? throw new InvalidOperationException("dashboard.html isn't embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    public static string Html => html.Value;

    /// <summary>
    /// The page with the sign-in's tenant and client IDs filled in (neither is secret), or left empty when sign-in isn't
    /// set up, which the page takes as a local run that needs none.
    /// </summary>
    public static string For(SignIn signIn) => Html
        .Replace("{{TENANT_ID}}", Attribute(signIn.TenantId), StringComparison.Ordinal)
        .Replace("{{CLIENT_ID}}", Attribute(signIn.ClientId), StringComparison.Ordinal);

    private static string Attribute(string? value) => System.Net.WebUtility.HtmlEncode(value ?? "");
}
