using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Llamachant.Count.Tests;

/// <summary>Only a valid Entra sign-in, for this app and tenant, by someone on the readers list, reads the stats.</summary>
public sealed class SignInTests
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string Client = "22222222-2222-2222-2222-222222222222";
    private const string Issuer = $"https://login.microsoftonline.com/{Tenant}/v2.0";

    private static readonly RsaSecurityKey EntraKey = new(RSA.Create(2048)) { KeyId = "entra" };
    private static readonly RsaSecurityKey OtherKey = new(RSA.Create(2048)) { KeyId = "entra" };

    private static SignIn Readers(string readers = "reader@example.com, Second@Example.com")
    {
        var config = new OpenIdConnectConfiguration { Issuer = Issuer };
        config.SigningKeys.Add(EntraKey);
        return new SignIn(Tenant, Client, readers, local: false, new StaticConfigurationManager<OpenIdConnectConfiguration>(config));
    }

    private static string Token(string name = "reader@example.com", string audience = Client, string issuer = Issuer,
        string tenant = Tenant, SecurityKey? key = null, DateTime? expires = null) =>
        "Bearer " + new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            IssuedAt = DateTime.UtcNow.AddMinutes(-70),
            NotBefore = DateTime.UtcNow.AddMinutes(-70),
            Expires = expires ?? DateTime.UtcNow.AddMinutes(50),
            Claims = new Dictionary<string, object> { ["tid"] = tenant, ["preferred_username"] = name },
            SigningCredentials = new SigningCredentials(key ?? EntraKey, SecurityAlgorithms.RsaSha256),
        });

    [Fact]
    public async Task A_reader_is_allowed() =>
        Assert.Equal(Access.Allowed, await Readers().CheckAsync(Token()));

    [Fact]
    public async Task Names_on_the_list_match_whatever_their_case() =>
        Assert.Equal(Access.Allowed, await Readers().CheckAsync(Token("second@example.COM")));

    [Fact]
    public async Task Someone_signed_in_but_not_on_the_list_is_refused() =>
        Assert.Equal(Access.NotAReader, await Readers().CheckAsync(Token("someone@example.com")));

    [Fact]
    public async Task An_empty_list_lets_no_one_read() =>
        Assert.Equal(Access.NotAReader, await Readers("").CheckAsync(Token()));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Basic cmVhZGVyOg==")]
    [InlineData("Bearer not-a-token")]
    public async Task No_token_means_signing_in(string? authorization) =>
        Assert.Equal(Access.SignInNeeded, await Readers().CheckAsync(authorization));

    [Fact]
    public async Task A_token_not_signed_by_Entra_is_refused() =>
        Assert.Equal(Access.SignInNeeded, await Readers().CheckAsync(Token(key: OtherKey)));

    [Fact]
    public async Task A_token_for_another_app_is_refused() =>
        Assert.Equal(Access.SignInNeeded, await Readers().CheckAsync(Token(audience: "33333333-3333-3333-3333-333333333333")));

    [Fact]
    public async Task A_token_from_another_tenant_is_refused()
    {
        const string other = "44444444-4444-4444-4444-444444444444";
        Assert.Equal(Access.SignInNeeded, await Readers().CheckAsync(Token(issuer: $"https://login.microsoftonline.com/{other}/v2.0")));
        Assert.Equal(Access.SignInNeeded, await Readers().CheckAsync(Token(tenant: other)));
    }

    [Fact]
    public async Task An_expired_token_is_refused() =>
        Assert.Equal(Access.SignInNeeded, await Readers().CheckAsync(Token(expires: DateTime.UtcNow.AddMinutes(-10))));

    [Fact]
    public async Task A_token_with_no_signature_is_refused()
    {
        var parts = Token()["Bearer ".Length..].Split('.');
        var unsigned = Base64UrlEncoder.Encode("""{"alg":"none","typ":"JWT"}""") + "." + parts[1] + ".";
        Assert.Equal(Access.SignInNeeded, await Readers().CheckAsync("Bearer " + unsigned));
    }

    [Fact]
    public async Task Without_sign_in_set_up_only_a_local_run_reads()
    {
        Assert.Equal(Access.Allowed, await new SignIn(null, null, null, local: true).CheckAsync(null));
        Assert.Equal(Access.SignInNeeded, await new SignIn(null, null, "reader@example.com", local: false).CheckAsync(Token()));
        Assert.Equal(Access.SignInNeeded, await new SignIn(Tenant, " ", "reader@example.com", local: false).CheckAsync(Token()));
    }

    [Fact]
    public void The_page_gets_the_ids_only_when_set_up()
    {
        Assert.Equal((Tenant, Client), (Readers().TenantId, Readers().ClientId));
        var none = new SignIn(Tenant, null, null, local: true);
        Assert.Equal((null, null), (none.TenantId, none.ClientId));
    }
}
