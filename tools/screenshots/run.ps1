# Regenerates the README screenshots from a fictional demo project; no real sessions, repos or
# conversations are involved. Requires Node.js and a build of the app.
#   .\tools\screenshots\run.ps1                 # demo in <drive>:\Demos\acme, images in docs\images
#   .\tools\screenshots\run.ps1 -KeepDemo       # leave the demo repos for poking around
#   .\tools\screenshots\run.ps1 -SiteDir <dir>  # also the llamatree.dev home page's shots, as PNGs in <dir>
#   .\tools\screenshots\run.ps1 -WebDir <dir>   # a worktree: build with another checkout's web folder (its node_modules)
#   .\tools\screenshots\run.ps1 -Version 1.4.0  # the version the app shows, for shots of a release not yet bumped
param(
    # One level below the drive root: some tools refuse to delete a folder directly under it.
    [string]$DemoRoot = (Join-Path ([IO.Path]::GetPathRoot($PSScriptRoot)) 'Demos\acme'),
    [int]$Port = 9333,
    [switch]$KeepDemo,
    [string]$SiteDir,
    [string]$WebDir,
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$exe = Join-Path $repo 'src\LlamaTree\bin\Debug\net10.0-windows\LlamaTree.exe'

$webArg = @()
if ($WebDir) { $webArg += "-p:WebDir=$WebDir" }
if ($Version) { $webArg += "-p:Version=$Version" }
dotnet build (Join-Path $repo 'src\LlamaTree') -v q -nologo @webArg | Out-Null
if ($LASTEXITCODE) { throw 'Build failed' }

$demo = node (Join-Path $PSScriptRoot 'setup-demo.mjs') $DemoRoot | ConvertFrom-Json
if ($LASTEXITCODE) { throw 'Demo setup failed' }

# Isolated profile + fake conversation history; nothing from the real user profile is loaded.
$saved = @{}
$vars = @{
    LLAMATREE_DATA = $demo.profile
    CLAUDE_CONFIG_DIR = $demo.claudeConfig
    WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = "--remote-debugging-port=$Port"
    # For the sessions (see SessionEnvironment): the demo's claude.cmd, the fake, before any real claude, and the
    # demo's Claude config. Without them the sessions would run the real claude with the real config.
    LLAMATREE_SESSION_PATH = "$($demo.bin);$env:PATH"
    LLAMATREE_SESSION_CLAUDE_CONFIG_DIR = $demo.claudeConfig
    # Codex's and Grok's conversations are read from these, for the app and its sessions alike: the demo's, empty. agy's
    # folder can't be moved, so capture.mjs leaves out any conversation that isn't in the demo.
    CODEX_HOME = $demo.codexHome
    GROK_HOME = $demo.grokHome
    LLAMATREE_SESSION_CODEX_HOME = $demo.codexHome
    LLAMATREE_SESSION_GROK_HOME = $demo.grokHome
}
foreach ($k in $vars.Keys) { $saved[$k] = [Environment]::GetEnvironmentVariable($k); [Environment]::SetEnvironmentVariable($k, $vars[$k]) }

$app = Start-Process $exe -PassThru
try {
    node (Join-Path $PSScriptRoot 'capture.mjs') $Port (Join-Path $repo 'docs\images') $demo.root $SiteDir 2>&1 | ForEach-Object { "$_" }   # its errors too
    if ($LASTEXITCODE) { throw 'Capture failed' }
}
finally {
    foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) }
    # The sessions outlive the window in a session host the app started, so that host (and the fakes it runs) goes too.
    $hosts = Get-CimInstance Win32_Process -Filter "ParentProcessId = $($app.Id)" | Where-Object { $_.ExecutablePath -eq $exe }
    if (-not $app.HasExited) { Stop-Process -Id $app.Id -Force }
    foreach ($h in $hosts) { taskkill /T /F /PID $h.ProcessId 2>&1 | Out-Null }
    if (-not $KeepDemo) {
        Start-Sleep 2   # let the session processes release the worktrees
        if (Test-Path (Join-Path $demo.root '.llamatree-demo')) { Remove-Item $demo.root -Recurse -Force }
    }
}
