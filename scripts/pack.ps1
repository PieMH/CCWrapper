# Builds the Velopack release files for a version. Run from anywhere; output goes to artifacts/ (publish/ and releases/).
#   publish: self-contained win-x64 app + the native AOT status relay (needs the MSVC build tools; windows-latest has them)
#   pack:    vpk pack over artifacts/publish -> Setup.exe, full .nupkg, releases.win.json, Portable.zip
# CI runs the two stages apart so SignPath can sign the publish folder in between (and Setup.exe afterwards).
param(
    [Parameter(Mandatory)][string]$Version,
    [ValidateSet('publish', 'pack', 'all')][string]$Stage = 'all'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$publish = Join-Path $root 'artifacts/publish'
$releases = Join-Path $root 'artifacts/releases'

function Run([string]$exe) {
    & $exe @args
    if ($LASTEXITCODE) { throw "$exe $($args -join ' ') failed with exit code $LASTEXITCODE" }
}

if ($Stage -in 'publish', 'all') {
    Remove-Item $publish -Recurse -Force -ErrorAction Ignore
    Run dotnet publish "$root/src/CCWrapper" -c Release -r win-x64 --self-contained "-p:Version=$Version" -o $publish
    $relay = Join-Path $root 'artifacts/relay'
    Remove-Item $relay -Recurse -Force -ErrorAction Ignore
    Run dotnet publish "$root/src/CCWrapper.StatusRelay" -c Release -r win-x64 "-p:Version=$Version" -o $relay
    # The app publish carries the framework-dependent relay (dll + runtimeconfig); only the native exe may ship.
    Remove-Item "$publish/CCWrapper.StatusRelay.*" -Force
    Copy-Item "$relay/CCWrapper.StatusRelay.exe" $publish
    Get-ChildItem $publish -Filter *.pdb -Recurse | Remove-Item -Force
}

if ($Stage -in 'pack', 'all') {
    Run dotnet tool restore
    $vpk = @(
        'pack', '--packId', 'PieMH.CCWrapper', '--packTitle', 'CCWrapper', '--packAuthors', 'PieMH',
        '--packVersion', $Version, '--packDir', $publish, '--mainExe', 'CCWrapper.exe', '--runtime', 'win-x64',
        '--icon', "$root/src/CCWrapper/CCWrapper.ico", '--framework', 'webview2', '--outputDir', $releases
    )
    Run dotnet vpk @vpk
}
