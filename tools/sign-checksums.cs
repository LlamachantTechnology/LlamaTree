// Writes a release's SHA256SUMS (sha256sum's format: "<hash>  <name>" for each file) and SHA256SUMS.p7s, a detached
// CMS signature of it by the company's code signing certificate (from the Windows certificate store, by subject; on the
// build agent the SafeNet token provides it), as tools/sign-notices.cs signs the notices.
// The app installs an update only when the download matches its line here and this signature is ours (see
// src/LlamaTree/Updates/Publisher.cs): a release changed on GitHub after it was built, by anyone who can publish there,
// isn't installed. Anyone can check by hand too: sha256sum -c SHA256SUMS --ignore-missing.
//
//   dotnet run tools/sign-checksums.cs -- <folder for SHA256SUMS> <file>... [--subject <subject>]
//       [--add-to <SHA256SUMS> <SHA256SUMS.p7s> [--may-replace <name>]...]
//
// Every file attached to the release goes in: copies look up the one they install by its name.
// --add-to: files being added to a release that's out already (the release pipeline building only some platforms).
// The new list is that release's, once its signature is shown to be ours as the app checks it (our certificate,
// through Sectigo's root), with these files added. A file it lists already stops it, unless it's named by
// --may-replace (the install scripts, which every run puts on the release afresh).
#:package System.Security.Cryptography.Pkcs@10.0.14

using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;

// As Publisher.cs in the app: the certificate must name the company, and its registration, as these.
const string Organization = "2.5.4.10";
const string SerialNumber = "2.5.4.5";
const string JurisdictionCountry = "1.3.6.1.4.1.311.60.2.1.3";
const string CodeSigning = "1.3.6.1.5.5.7.3.3";
const string RegistrationNumber = "2469513";
const string Jurisdiction = "CA";

// Sectigo Public Code Signing Root R46, which our certificate chains to: as Publisher.RootR46 in the app.
const string RootR46 =
    "MIIFeDCCA2CgAwIBAgIQSyw7AQGLrSq8jHtbPu2QVzANBgkqhkiG9w0BAQwFADBWMQswCQYDVQQGEwJHQjEYMBYGA1UEChMPU2VjdGlnbyBMaW1pdGVkMS0wKwYDVQQDEyRTZWN0aWdvIFB1YmxpYyBD" +
    "b2RlIFNpZ25pbmcgUm9vdCBSNDYwHhcNMjEwMzIyMDAwMDAwWhcNNDYwMzIxMjM1OTU5WjBWMQswCQYDVQQGEwJHQjEYMBYGA1UEChMPU2VjdGlnbyBMaW1pdGVkMS0wKwYDVQQDEyRTZWN0aWdvIFB1" +
    "YmxpYyBDb2RlIFNpZ25pbmcgUm9vdCBSNDYwggIiMA0GCSqGSIb3DQEBAQUAA4ICDwAwggIKAoICAQCN55QSIgQkdC7/FiMCkoq2rjaFrEfUI5ErPtx94jGgUW+shJHjUoq14pbe0IdjJImK/+8Skzt9" +
    "u7aKvb0Ffyeba2XTpQxpsbxJOZrxbW6q5KCDJ9qaDStQ6Utbs7hkNqR+Sj2pcaths3OzPAsM79szV+W+NDfjlxtd/R8SPYIDdub7P2bSlDFp+m2zNKzBenjcklDyZMeqLQSrw2rq4C+np9xu1+j/2iGr" +
    "QL+57g2extmeme/G3h+pDHazJyCh1rr9gOcB0u/rgimVcI3/uxXP/tEPNqIuTzKQdEZrRzUTdwUzT2MuuC3hv2WnBGsY2HH6zAjybYmZELGt2z4s5KoYsMYHAXVn3m3pY2MeNn9pib6qRT5uWl+PoVvL" +
    "nTCGMOgDs0DGDQ84zWeoU4j6uDBl+m/H5x2xg3RpPqzEaDux5mczmrYI4IAFSEDu9oJkRqj1c7AGlfJsZZ+/VVscnFcax3hGfHCqlBuCF6yH6bbJDoEcQNYWFyn8XJwYK+pF9e+91WdPKF4F7pBMeufG" +
    "9ND8+s0+MkYTIDaKBOq3qgdGnA2TOglmmVhcKaO5DKYwODzQRjY1fJy67sPV+Qp2+n4FG0DKkjXp1XrRtX8ArqmQqsV/AZwQsRb8zG4Y3G9i/qZQp7h7uJ0VP/4gDHXIIloTlRmQAOka1cKG8eOO7F/0" +
    "5QIDAQABo0IwQDAdBgNVHQ4EFgQUMuuSmv81lkgvKEBCcCA2kVwXheYwDgYDVR0PAQH/BAQDAgGGMA8GA1UdEwEB/wQFMAMBAf8wDQYJKoZIhvcNAQEMBQADggIBAHZlwuPXIkrXHYle/2lexhQCTXOm" +
    "zc0oyrA36r+nySGqql/av/aDbNCA0QpcAKTL88w5D55BcYjVPOiKe4wXI/fKNHSRbAauUD8AWbImPDwXg1cDPi3RGj3UzwdUskMLUnKoiPXEF/Jv0Vil0WjkPZgIGO429EhImvpUcPCI1HAWMEJJ0Nk/" +
    "dUtFcdiuorthDoiFUFe5uhErNikfjyBynlyeidGC2kWNapnahHFrM6UQu3nwl/Z0gaA/V8eGjDCMDjiVrgHGHqvcqB9vL9f/dh6uF3Nt5bl1s2EGqJUzwk5vsjfylb6FVBK5yL1iQnb3Kvz1NzEDJlf+" +
    "0ebb8BYCcoOMCLOErKnkB/ihiMQTWlBHVEKm7dBBNCyYsT6iNKEMXb2s9395p79tDFYyhRtLl7jhrOSkPHHxo+FOY9b0Rrr1CwjhYzztolkvCtQsayOinqFN7tESzRgzUO1Bbst/PUFgC2MLePV170MV" +
    "tzYLEK/cXBipmNk22R3YhLMGioLjexskp0LO7g8+VlwyfexL3lYrOzu6+XpY0FG2bNb2WKJSJHpEhqEcYD9J0/z6+YQcBcI0v+Lm8RkqmS9WVzWctfUHw0Yv3jg9GQ37o/HfE57nqXJYMa+96trX1m13" +
    "MzOO9Kz9wb9Jh9JwBWd0Bqb2eEAtFgSRDx/TFsS4ehcNJMmy";

var subject = "Llamachant Technology Ltd";   // as publish.ps1, and Publisher.Name
var rest = args.ToList();
if (rest.IndexOf("--subject") is var at and >= 0 && at + 1 < rest.Count) { subject = rest[at + 1]; rest.RemoveRange(at, 2); }
string? addTo = null, addToSignature = null;
var mayReplace = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
for (var i = 0; i < rest.Count;)
{
    if (rest[i] == "--add-to" && i + 2 < rest.Count) { (addTo, addToSignature) = (rest[i + 1], rest[i + 2]); rest.RemoveRange(i, 3); }
    else if (rest[i] == "--may-replace" && i + 1 < rest.Count) { mayReplace.Add(rest[i + 1]); rest.RemoveRange(i, 2); }
    else i++;
}
if (rest.Count < 2)
{
    Console.Error.WriteLine("Usage: dotnet run tools/sign-checksums.cs -- <folder for SHA256SUMS> <file>... [--subject <subject>] [--add-to <SHA256SUMS> <SHA256SUMS.p7s> [--may-replace <name>]...]");
    return 2;
}
var outDir = Path.GetFullPath(rest[0]);
var files = rest.Skip(1).Select(Path.GetFullPath).ToList();

string? Field(X509Certificate2 c, string oid) => c.SubjectName.EnumerateRelativeDistinguishedNames()
    .Where(n => !n.HasMultipleElements && n.GetSingleElementType().Value == oid).Select(n => n.GetSingleElementValue()).FirstOrDefault();
bool IsOurs(X509Certificate2 c) =>
    c.Extensions.OfType<X509EnhancedKeyUsageExtension>().Any(e => e.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value == CodeSigning))
    && Field(c, Organization) == subject && Field(c, SerialNumber) == RegistrationNumber && Field(c, JurisdictionCountry) == Jurisdiction;

// The release's list so far (name → hash, in its order), when adding to one: only once its signature is ours, as the app
// would judge it, so nothing on GitHub but what we signed gets signed again.
var entries = new List<(string Name, string Hash)>();
if (addTo != null)
{
    var oldContent = File.ReadAllBytes(addTo);
    var old = new SignedCms(new ContentInfo(oldContent), detached: true);
    old.Decode(File.ReadAllBytes(addToSignature!));
    if (old.SignerInfos.Count != 1 || old.SignerInfos[0] is not { Certificate: { } oldCertificate } oldSigner)
        throw new InvalidOperationException($"{addToSignature} must have exactly one signer.");
    oldSigner.CheckSignature(verifySignatureOnly: true);
    if (!IsOurs(oldCertificate)) throw new InvalidOperationException($"{addTo} isn't signed by our certificate ({oldCertificate.Subject}).");
    var signedAt = oldSigner.SignedAttributes.Cast<CryptographicAttributeObject>().SelectMany(a => a.Values.Cast<AsnEncodedData>())
        .OfType<Pkcs9SigningTime>().Select(t => (DateTime?)t.SigningTime.ToUniversalTime()).FirstOrDefault();
    using var root = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(RootR46));
    using var chain = new X509Chain();
    chain.ChainPolicy.ApplicationPolicy.Add(new Oid(CodeSigning));
    chain.ChainPolicy.ExtraStore.AddRange(old.Certificates);
    chain.ChainPolicy.VerificationTime = (signedAt ?? DateTime.UtcNow).ToLocalTime();
    chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
    chain.ChainPolicy.CustomTrustStore.Add(root);
    chain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
    chain.ChainPolicy.VerificationFlags = X509VerificationFlags.IgnoreEndRevocationUnknown | X509VerificationFlags.IgnoreCertificateAuthorityRevocationUnknown;
    if (!chain.Build(oldCertificate))
        throw new InvalidOperationException($"{addTo}'s signature doesn't chain to Sectigo Public Code Signing Root R46: " +
            string.Join("; ", chain.ChainStatus.Select(s => s.StatusInformation.Trim())));
    foreach (var line in Encoding.UTF8.GetString(oldContent).Split('\n', StringSplitOptions.RemoveEmptyEntries))
    {
        if (line.Length < 67 || line[64..66] != "  " || !line[..64].All(Uri.IsHexDigit))
            throw new InvalidOperationException($"{addTo} has a line that isn't \"<hash>  <name>\": {line}");
        entries.Add((line[66..].TrimEnd('\r'), line[..64]));
    }
    Console.WriteLine($"Adding to {addTo} ({entries.Count} files, signed {signedAt:yyyy-MM-dd} by {oldCertificate.Subject})");
}

var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
foreach (var file in files)
{
    var name = Path.GetFileName(file);
    if (!names.Add(name)) throw new InvalidOperationException($"Two files named {name}: copies couldn't tell which is which.");
    string hash;
    using (var stream = File.OpenRead(file)) hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    var listed = entries.FindIndex(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
    if (listed < 0) entries.Add((name, hash));
    else if (mayReplace.Contains(name)) entries[listed] = (name, hash);
    else throw new InvalidOperationException($"The release has {name} already: a file on a release isn't replaced. Build a new version instead.");
}
var sums = new StringBuilder();
foreach (var (name, hash) in entries) sums.Append(hash).Append("  ").Append(name).Append('\n');
var content = Encoding.UTF8.GetBytes(sums.ToString());

// The newest valid code signing certificate with its key, issued to the organisation and its registration, as the app requires.
var certificate = new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine }
    .SelectMany(location =>
    {
        using var store = new X509Store(StoreName.My, location);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        return store.Certificates.Find(X509FindType.FindBySubjectName, subject, validOnly: true).Cast<X509Certificate2>().ToList();
    })
    .Where(c => c.HasPrivateKey && IsOurs(c))
    .OrderByDescending(c => c.NotAfter)
    .FirstOrDefault()
    ?? throw new InvalidOperationException($"No valid code signing certificate with its private key for O={subject}, SERIALNUMBER={RegistrationNumber} in the certificate store.");
Console.WriteLine($"Signing SHA256SUMS ({entries.Count} files) as {certificate.Subject} (expires {certificate.NotAfter:yyyy-MM-dd})");

var cms = new SignedCms(new ContentInfo(content), detached: true);
var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, certificate)
{
    DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1"),   // SHA-256
    IncludeOption = X509IncludeOption.ExcludeRoot,          // the intermediates, so the app can build the chain
};
signer.SignedAttributes.Add(new Pkcs9SigningTime(DateTime.UtcNow));
cms.ComputeSignature(signer, silent: false);
var signature = cms.Encode();

// Read it back as the app will before writing it.
var check = new SignedCms(new ContentInfo(content), detached: true);
check.Decode(signature);
check.CheckSignature(verifySignatureOnly: false);
Directory.CreateDirectory(outDir);
File.WriteAllBytes(Path.Combine(outDir, "SHA256SUMS"), content);
File.WriteAllBytes(Path.Combine(outDir, "SHA256SUMS.p7s"), signature);
Console.Write(sums);
Console.WriteLine($"Wrote {Path.Combine(outDir, "SHA256SUMS")} and SHA256SUMS.p7s");
return 0;
