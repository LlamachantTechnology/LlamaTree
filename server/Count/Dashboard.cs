namespace Llamachant.Count;

/// <summary>The dashboard page (dashboard.html, embedded) and the policy it's served with.</summary>
internal static class Dashboard
{
    /// <summary>
    /// Only its own inline script and style run, and it talks only to this app (the stats). Nothing may frame it, and
    /// forms post nowhere.
    /// </summary>
    public const string ContentSecurityPolicy =
        "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'; " +
        "base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    private static readonly Lazy<string> html = new(() =>
    {
        using var stream = typeof(Dashboard).Assembly.GetManifestResourceStream("dashboard.html")
            ?? throw new InvalidOperationException("dashboard.html isn't embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    public static string Html => html.Value;
}
