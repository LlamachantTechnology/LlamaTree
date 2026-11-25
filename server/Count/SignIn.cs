using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Llamachant.Count;

/// <summary>Whether a request for the stats may have them: what <see cref="SignIn.CheckAsync"/> found.</summary>
internal enum Access
{
    /// <summary>A valid sign-in by someone on the readers list, or a local run with no sign-in set up.</summary>
    Allowed,

    /// <summary>No sign-in, or one that isn't valid (wrong app or tenant, expired, forged): sign in again.</summary>
    SignInNeeded,

    /// <summary>A valid sign-in by someone who isn't on the readers list.</summary>
    NotAReader,
}

/// <summary>
/// Who may read the stats: people signed in with Microsoft (Entra ID) in our tenant, through our app registration,
/// whose sign-in name is on the readers list. The dashboard signs them in and sends the ID token it gets as
/// <c>Authorization: Bearer</c>; this checks it was signed by Entra for this app and tenant and hasn't expired.
/// </summary>
/// <remarks>
/// Set by three app settings (count.bicep): <c>COUNT_TENANT_ID</c>, <c>COUNT_CLIENT_ID</c> (the app registration's
/// Application (client) ID) and <c>COUNT_READERS</c> (sign-in names, separated by commas or semicolons). When the
/// client ID isn't set, a local run (<c>func start</c>) lets everyone read and a deployed one lets no one.
/// </remarks>
internal sealed class SignIn
{
    private readonly string? tenantId, clientId;
    private readonly HashSet<string> readers;
    private readonly bool local;
    private readonly IConfigurationManager<OpenIdConnectConfiguration>? entra;
    private readonly JsonWebTokenHandler handler = new() { MapInboundClaims = false };

    public SignIn(string? tenantId, string? clientId, string? readers, bool local,
        IConfigurationManager<OpenIdConnectConfiguration>? entra = null)
    {
        this.tenantId = Clean(tenantId);
        this.clientId = Clean(clientId);
        this.readers = new((readers ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);
        this.local = local;
        if (IsSetUp)
            this.entra = entra ?? new ConfigurationManager<OpenIdConnectConfiguration>(
                $"{Authority}/.well-known/openid-configuration", new OpenIdConnectConfigurationRetriever(), new HttpDocumentRetriever());
    }

    public static SignIn FromEnvironment() => new(
        Environment.GetEnvironmentVariable("COUNT_TENANT_ID"),
        Environment.GetEnvironmentVariable("COUNT_CLIENT_ID"),
        Environment.GetEnvironmentVariable("COUNT_READERS"),
        string.Equals(Environment.GetEnvironmentVariable("AZURE_FUNCTIONS_ENVIRONMENT"), "Development", StringComparison.OrdinalIgnoreCase));

    /// <summary>Both IDs are set, so readers sign in.</summary>
    public bool IsSetUp => tenantId is not null && clientId is not null;

    /// <summary>What the dashboard needs to sign someone in: nothing secret.</summary>
    public string? TenantId => IsSetUp ? tenantId : null;

    public string? ClientId => IsSetUp ? clientId : null;

    private string Authority => $"https://login.microsoftonline.com/{tenantId}/v2.0";

    /// <summary>Checks the request's <c>Authorization</c> header.</summary>
    public async Task<Access> CheckAsync(string? authorization, CancellationToken ct = default)
    {
        if (!IsSetUp) return local ? Access.Allowed : Access.SignInNeeded;
        if (authorization is null || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return Access.SignInNeeded;

        OpenIdConnectConfiguration config;
        try { config = await entra!.GetConfigurationAsync(ct); }
        catch (Exception e) when (e is not OperationCanceledException) { return Access.SignInNeeded; }

        var result = await handler.ValidateTokenAsync(authorization["Bearer ".Length..].Trim(), new TokenValidationParameters
        {
            ValidIssuer = Authority,
            ValidAudience = clientId,
            IssuerSigningKeys = config.SigningKeys,
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(2),
        });
        if (!result.IsValid) return Access.SignInNeeded;

        var who = result.ClaimsIdentity;
        if (!string.Equals(Claim(who, "tid"), tenantId, StringComparison.OrdinalIgnoreCase)) return Access.SignInNeeded;
        var name = Claim(who, "preferred_username") ?? Claim(who, "email");
        return name is not null && readers.Contains(name) ? Access.Allowed : Access.NotAReader;
    }

    private static string? Claim(ClaimsIdentity who, string type) => who.FindFirst(type)?.Value;

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
