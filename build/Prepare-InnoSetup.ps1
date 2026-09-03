param(
    [switch]$Offline
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$manifest = Get-Content -Raw -LiteralPath (Join-Path $projectRoot 'profiles\dependencies.json') |
    ConvertFrom-Json
$component = $manifest.components.Where({ $_.id -eq 'inno-setup' })
$expectedVersion = $component.version
$releaseTagMatch = [regex]::Match($component.url, '/releases/download/([^/]+)/')
if (-not $releaseTagMatch.Success) {
    throw 'Inno Setup manifest URL does not contain an immutable Release tag.'
}
$releaseTag = $releaseTagMatch.Groups[1].Value
$innoRoot = Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6'
$compiler = Join-Path $innoRoot 'ISCC.exe'

function Test-ExpectedInnoSetup {
    if (-not (Test-Path -LiteralPath $compiler)) { return $false }
    $registration = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*' `
        -ErrorAction SilentlyContinue | Where-Object {
            $_.DisplayName -like 'Inno Setup version *' -and
            $_.DisplayVersion -eq $expectedVersion -and
            ([string]$_.InstallLocation).TrimEnd('\') -eq $innoRoot.TrimEnd('\')
        } | Select-Object -First 1
    return $null -ne $registration
}

if (Test-ExpectedInnoSetup) {
    Write-Output "Pinned Inno Setup $expectedVersion is prepared."
    exit 0
}
if ($Offline) {
    throw "Pinned Inno Setup $expectedVersion is not installed."
}

$downloadRoot = Join-Path $projectRoot 'artifacts\tools\inno-setup'
[IO.Directory]::CreateDirectory($downloadRoot) | Out-Null
$installer = Join-Path $downloadRoot $component.archiveFileName
dotnet run --project (Join-Path $projectRoot 'tools\RootedAndroidGameVM.ReleaseTool\RootedAndroidGameVM.ReleaseTool.csproj') `
    -c Release -- download inno-setup $installer
if ($LASTEXITCODE -ne 0) { throw 'Pinned Inno Setup download failed.' }
if ((Get-Item -LiteralPath $installer).Length -ne $component.size) {
    throw 'Pinned Inno Setup installer size mismatch.'
}
$signature = Get-AuthenticodeSignature -LiteralPath $installer
if ($signature.Status -ne 'Valid' -or
    $signature.SignerCertificate.Subject -notlike '*CN=Pyrsys B.V.*') {
    throw 'Pinned Inno Setup installer has an unexpected Authenticode signer.'
}
gh release verify-asset $releaseTag $installer --repo jrsoftware/issrc
if ($LASTEXITCODE -ne 0) { throw 'GitHub rejected the Inno Setup Release asset attestation.' }

$process = Start-Process -FilePath $installer -ArgumentList @(
    '/VERYSILENT',
    '/SUPPRESSMSGBOXES',
    '/NORESTART',
    '/CURRENTUSER'
) -Wait -PassThru -WindowStyle Hidden
if ($process.ExitCode -ne 0) {
    throw "Inno Setup installation failed with exit code $($process.ExitCode)."
}
if (-not (Test-ExpectedInnoSetup)) {
    throw "Inno Setup $expectedVersion registration verification failed."
}
Write-Output "Pinned Inno Setup $expectedVersion was installed from its verified Release asset."
