<#
.SYNOPSIS
    Builds a self-contained Sessionwright release and packs it with Velopack (Setup.exe, Portable.zip, full/delta packages,
    releases.<channel>.json).
.EXAMPLE
    ./build/publish.ps1 -Version 1.0.1 -UpdateFeed https://github.com/Patri0s/Sessionwright -Upload
    Publishes the release on GitHub (token: $env:GITHUB_TOKEN, else the GitHub CLI's login).
.EXAMPLE
    ./build/publish.ps1 -Version 1.0.1 -UpdateFeed \\fileserver\apps\Sessionwright -Upload
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Version,
    [string] $Channel = 'win',
    [string] $OutputDir = 'releases',
    # Default feed baked into the exe (Settings can override it); the previous release is fetched from it for deltas.
    [string] $UpdateFeed,
    # Publish the release: a GitHub release for a github.com feed, a copy into a UNC/local feed folder (for any other
    # http feed copy OutputDir to the server).
    [switch] $Upload,
    # signtool.exe parameters, e.g. '/fd sha256 /tr http://timestamp.digicert.com /td sha256 /sha1 <thumbprint>'.
    [string] $SignParams,
    # Azure Trusted Signing metadata.json; credentials come from the Azure CLI / environment.
    [string] $AzureTrustedSignFile
)

$ErrorActionPreference = 'Stop'

function Invoke-Native([scriptblock] $Command) {
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "Exit code $LASTEXITCODE from: $Command" }
}

$root = Split-Path $PSScriptRoot -Parent
$out = if ([IO.Path]::IsPathRooted($OutputDir)) { $OutputDir } else { Join-Path $root $OutputDir }
$publishDir = Join-Path ([IO.Path]::GetTempPath()) "Sessionwright-publish-$Version"
$feed = if ($UpdateFeed) { $UpdateFeed.TrimEnd('\', '/') } else { $null }
$gitHubFeed = $feed -match '^https://github\.com/[^/]+/[^/]+$'
$httpFeed = -not $gitHubFeed -and $feed -match '^https?://'

if ($Upload -and (-not $feed -or $httpFeed)) { throw '-Upload needs -UpdateFeed pointing to a GitHub repository or a UNC/local folder.' }

# Never stored anywhere: the CI's GITHUB_TOKEN, else the token of the GitHub CLI's login (gh auth login).
$token = $null
if ($gitHubFeed) {
    $token = $env:GITHUB_TOKEN
    if (-not $token -and (Get-Command gh -ErrorAction SilentlyContinue)) { $token = (gh auth token 2>$null) }
    if (-not $token) { throw 'A GitHub token is needed: set GITHUB_TOKEN or sign in with "gh auth login".' }
}

Push-Location $root
try {
    try {
        Invoke-Native { dotnet tool restore }
    }
    catch { throw "Restoring the vpk tool failed: $_" }

    # The previous full package lets vpk generate a small delta update.
    try {
        if ($gitHubFeed) {
            Invoke-Native { dotnet vpk download github --repoUrl $feed --token $token --channel $Channel --outputDir $out } # no release yet: a warning, exit 0
        }
        elseif ($httpFeed) {
            Invoke-Native { dotnet vpk download http --url $feed --channel $Channel --outputDir $out --allowEmptyChannel }
        }
        elseif ($feed -and (Test-Path (Join-Path $feed "releases.$Channel.json"))) {
            Invoke-Native { dotnet vpk download local --path $feed --channel $Channel --outputDir $out }
        }
    }
    catch { throw "Downloading the previous release from '$feed' failed: $_" }

    try {
        if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
        # No trimming / single file: mstscax is driven through late-bound COM and Velopack deltas work per file.
        $props = @("-p:Version=$Version")
        if ($feed) { $props += "-p:UpdateFeed=$feed" }
        Invoke-Native { dotnet publish src/Sessionwright -c Release -r win-x64 --self-contained @props -o $publishDir }
    }
    catch { throw "dotnet publish failed: $_" }

    try {
        $pack = @(
            '--packId', 'Sessionwright', '--packVersion', $Version, '--packTitle', 'Sessionwright',
            '--packDir', $publishDir, '--mainExe', 'Sessionwright.exe', '--icon', 'src/Sessionwright/Assets/app.ico',
            '--runtime', 'win-x64', '--channel', $Channel, '--outputDir', $out)
        if ($SignParams) { $pack += '--signParams', $SignParams }
        if ($AzureTrustedSignFile) { $pack += '--azureTrustedSignFile', $AzureTrustedSignFile }
        Invoke-Native { dotnet vpk pack @pack }
    }
    catch { throw "vpk pack failed: $_" }

    if ($Upload) {
        try {
            if ($gitHubFeed) {
                Invoke-Native { dotnet vpk upload github --repoUrl $feed --token $token --channel $Channel --outputDir $out --publish --releaseName "Sessionwright $Version" --tag "v$Version" }
            }
            else {
                New-Item -ItemType Directory -Force $feed | Out-Null
                Invoke-Native { dotnet vpk upload local --path $feed --channel $Channel --outputDir $out }
            }
        }
        catch { throw "Uploading to '$feed' failed: $_" }
    }

    Write-Host "Release $Version ($Channel): $out" -ForegroundColor Green
}
finally {
    Pop-Location
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force -ErrorAction SilentlyContinue }
}
