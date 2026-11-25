# User count service

Copies of the app whose users said yes to *May we count you?* send one request an hour while they are open:

```
GET https://api.llamatree.dev/count/ping?id=<32 hex>&v=<app version>&os=windows&osv=<system version>&projects=<n>&sessions=<n>
```

This Azure Function app ([`Count`](Count)) records it, and adds the numbers up for us.

## What it keeps

One row per copy per UTC hour in the `pings` table: the hour, the id, the app's version, the operating system's name
and version, and how many projects and sessions were open. A copy that pings twice in an hour is still one row.
Projects and sessions are **only counts**: the app never sends their names, folders or anything in them.

**Nothing else is kept**: not the IP address, the user agent or any other header, and there's no Application Insights
or request log. Anything that isn't exactly an id, two version numbers, a system name and two counts (up to 10,000)
gets a `400` and isn't stored. Rows are deleted after 400 days (the `count-forget` function, daily at 03:30 UTC).

The id is random, made when the user said yes and forgotten when they say no, so nothing links a row to a person.

## Reading the numbers

`GET https://api.llamatree.dev/count/stats?days=30`, signed in (see [Who can read them](#who-can-read-them)), returns JSON:

- `users`: different copies in the period, each counted once as last seen.
- `days`: per day, `users`; `userHours` (a copy open three hours is three); `averageProjects` and `averageSessions`
  (the most each user had open at once that day, averaged); and users by version. A day with nobody is `0`.
- `hours` (for 7 days or fewer): per hour, the users and the projects and sessions open between them.
- `versions`, `systems`, `systemVersions`: the period's users by app version, by system (`windows`) and by system
  and version (`windows 10.0.26300`).

`days` goes up to 400.

### The dashboard

[`https://api.llamatree.dev/count/dashboard`](https://api.llamatree.dev/count/dashboard) charts the same numbers:
users per day (per hour for 7 days or fewer), averages, versions and systems, with a table under the chart. The
page holds no numbers, so it loads for anyone; it signs you in with your Microsoft work account and shows them only
if you're a reader. `?days=7` opens a given period. The sign-in lasts as long as the tab; a later visit signs in again
by itself while Microsoft still knows you. **Sign out** signs you out of Microsoft too.

### Who can read them

There are no keys. The dashboard signs in with Microsoft (Entra ID) through an app registration of ours, as a
single-page app with PKCE and no secret, and sends the stats the ID token it gets as `Authorization: Bearer`. The
function checks the token itself (`SignIn.cs`): signed by Entra, for that app registration, issued by our tenant and
not expired. That doesn't depend on App Service authentication (Easy Auth), which the Flex Consumption plan doesn't
promise. Then the signed-in name must be on the readers list: a valid sign-in by anyone else is a `403`.

Three app settings, set by `count.bicep` from the pipeline's variables:

| Setting | Pipeline variable | |
| --- | --- | --- |
| `COUNT_TENANT_ID` | (the deployment's tenant) | Whose people sign in. |
| `COUNT_CLIENT_ID` | `CountClientId` | The app registration's *Application (client) ID*. |
| `COUNT_READERS` | `CountReaders` | Who may read: Microsoft sign-in names, separated by commas. |

Without `COUNT_CLIENT_ID`, no one can read the stats (except in a local run). To let someone in or shut them out,
change `COUNT_READERS`, in the pipeline variable so the next deployment keeps it, and in the function app's
**Settings › Environment variables** to take effect now. The names are matched against the token's
`preferred_username` (the sign-in name), which only our tenant's admins can change, since only our tenant's tokens
are accepted.

The app registration, once (Entra admin centre › **App registrations › New registration**, or with the Azure CLI):

- **Supported account types:** this organizational directory only (single tenant).
- **Redirect URI:** platform *Single-page application*, `https://api.llamatree.dev/count/dashboard` (add
  `https://<app>.azurewebsites.net/count/dashboard` too to use the default address). The page signs in and out to its
  own address, so a page served elsewhere needs its address here.
- Nothing else: no secret, certificate, API permission beyond the default `User.Read`, or exposed API.

```powershell
$app = az ad app create --display-name "LlamaTree count" --sign-in-audience AzureADMyOrg `
  --enable-id-token-issuance false --query appId -o tsv
az rest --method patch --url "https://graph.microsoft.com/v1.0/applications(appId='$app')" `
  --body '{"spa":{"redirectUris":["https://api.llamatree.dev/count/dashboard"]}}'
$app   # the CountClientId
```

To also require that readers are assigned in Entra, turn on **Enterprise applications › LlamaTree count ›
Properties › Assignment required** and assign them under **Users and groups**; the list in `COUNT_READERS` still
applies.

## Deploying

[`azure-pipelines-count.yml`](../azure-pipelines-count.yml) builds, tests and deploys it with
[`count.bicep`](count.bicep): a storage account for it and a Flex Consumption function app (.NET 10), at most 40
instances. The pipeline's header lists the one-time setup.

Then give it an address of ours. In the function app, **Settings › Custom domains › Add custom domain**, for example
`api.llamatree.dev`, with the CNAME and `asuid` TXT records it asks for and a free App Service managed certificate.
The app's `TelemetryService.PingUrl` is that address plus `/count/ping`. The function app (`func-llamatree`) is
meant for more than the count: each part has its own path, the count's being `/count/`.

## Running it locally

```powershell
npx azurite --silent --location $env:TEMP\azurite     # the storage emulator, in another window
cd server\Count
func start                                            # Azure Functions Core Tools
# http://localhost:7071/count/ping?id=1868c4d72ca345de80adb48806e14052&v=1.2.0&os=windows&osv=10.0.26300&projects=1&sessions=2
# http://localhost:7071/count/stats?days=7   (no sign-in locally)
# http://localhost:7071/count/dashboard      (doesn't sign in either)
```

Locally means `func start`, which sets `AZURE_FUNCTIONS_ENVIRONMENT=Development`: with no `COUNT_CLIENT_ID` it lets
everyone read. Set the three settings in `local.settings.json` to try the sign-in (and add
`http://localhost:7071/count/dashboard` as a redirect URI).

`dotnet test server\Count.Tests` runs the tests; the storage ones run only while Azurite is.
