param(
    [string]$Configuration = 'Release',
    [Parameter(Mandatory)]
    [string]$CleanInstallRoot,
    [string]$SigningCertificateThumbprint,
    [switch]$AllowUnsignedLocalCandidate,
    [switch]$ReuseE2EState,
    [string]$E2EDependencyCache
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $projectRoot 'artifacts'
$launcherOutput = Join-Path $artifacts 'publish\Launcher'
$setupOutput = Join-Path $artifacts 'publish\Setup'
$innoCompiler = Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'
$releaseDirectory = Join-Path $artifacts 'release'
$manifestPath = Join-Path $projectRoot 'profiles\dependencies.json'
$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
$innoScript = Join-Path $projectRoot 'installer\RootedAndroidGameVM.iss'
$versionMatch = [regex]::Match(
    (Get-Content -Raw -LiteralPath $innoScript),
    '(?m)^#define AppVersion "([^"]+)"$')
if (-not $versionMatch.Success) { throw 'Unable to read the product version from the Inno script.' }
$productVersion = $versionMatch.Groups[1].Value

if (-not (Test-Path -LiteralPath $innoCompiler)) {
    throw "Inno Setup compiler was not found at $innoCompiler"
}
$expectedSdk = $manifest.components.Where({ $_.id -eq 'dotnet-sdk' }).version
$actualSdk = (& dotnet --version).Trim()
if ($actualSdk -ne $expectedSdk) {
    throw "The Release requires .NET SDK $expectedSdk; found $actualSdk."
}
$expectedInno = $manifest.components.Where({ $_.id -eq 'inno-setup' }).version
$innoInstallRoot = (Split-Path -Parent $innoCompiler).TrimEnd('\')
$innoRegistration = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*' `
    -ErrorAction SilentlyContinue | Where-Object {
        $_.DisplayName -like 'Inno Setup version *' -and
        ([string]$_.InstallLocation).TrimEnd('\') -eq $innoInstallRoot
    } | Select-Object -First 1
if (-not $innoRegistration -or $innoRegistration.DisplayVersion -ne $expectedInno) {
    throw "The Release requires Inno Setup $expectedInno at $innoInstallRoot."
}
$expectedRuntime = $manifest.components.Where({ $_.id -eq 'dotnet-runtime' }).version
foreach ($project in @(
    'src\RootedAndroidGameVM.Launcher\RootedAndroidGameVM.Launcher.csproj',
    'src\RootedAndroidGameVM.Setup\RootedAndroidGameVM.Setup.csproj'
)) {
    [xml]$projectXml = Get-Content -Raw -LiteralPath (Join-Path $projectRoot $project)
    if ([string]$projectXml.Project.PropertyGroup.Version -ne $productVersion -or
        [string]$projectXml.Project.PropertyGroup.RuntimeFrameworkVersion -ne $expectedRuntime) {
        throw "Version mismatch in $project."
    }
}

if ($SigningCertificateThumbprint -and $ReuseE2EState) {
    throw 'A signed public candidate must pass E2E from an absent or empty product root.'
}
if ($SigningCertificateThumbprint -and $AllowUnsignedLocalCandidate) {
    throw 'AllowUnsignedLocalCandidate cannot be combined with a signing certificate.'
}

$prepareValidator = Join-Path $projectRoot 'build\Prepare-SpdxValidator.ps1'
if ($SigningCertificateThumbprint) {
    & $prepareValidator -Offline
} else {
    & $prepareValidator
}

if (Test-Path -LiteralPath $releaseDirectory) {
    Get-ChildItem -LiteralPath $releaseDirectory -File |
        Where-Object { $_.Name -like 'RootedAndroidGameVM-Setup-*.exe*' } |
        ForEach-Object { [IO.File]::Delete($_.FullName) }
}

dotnet test (Join-Path $projectRoot 'RootedAndroidGameVM.sln') -c $Configuration --filter 'Category!=LocalIntegration&Category!=CleanE2E'
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }

$resolvedCleanRoot = [IO.Path]::GetFullPath($CleanInstallRoot)
if ($resolvedCleanRoot -eq [IO.Path]::GetPathRoot($resolvedCleanRoot)) {
    throw 'CleanInstallRoot cannot be a drive root.'
}
if (-not $ReuseE2EState -and
    (Test-Path -LiteralPath $resolvedCleanRoot) -and
    (Get-ChildItem -LiteralPath $resolvedCleanRoot -Force | Select-Object -First 1)) {
    throw 'CleanInstallRoot must be absent or empty. ReuseE2EState is allowed only for non-public local iteration.'
}
$env:RGVM_CLEAN_INSTALL_ROOT = $resolvedCleanRoot
$env:RGVM_E2E_REUSE_STATE = if ($ReuseE2EState) { '1' } else { '0' }
dotnet test (Join-Path $projectRoot 'tests\RootedAndroidGameVM.Core.Tests\RootedAndroidGameVM.Core.Tests.csproj') -c $Configuration --filter 'Category=CleanE2E'
if ($LASTEXITCODE -ne 0) { throw 'Clean Windows AVD/Root E2E gate failed.' }

dotnet publish (Join-Path $projectRoot 'src\RootedAndroidGameVM.Launcher\RootedAndroidGameVM.Launcher.csproj') -c $Configuration -r win-x64 --self-contained true -p:PublishSingleFile=true -o $launcherOutput
if ($LASTEXITCODE -ne 0) { throw 'Launcher publish failed.' }

dotnet publish (Join-Path $projectRoot 'src\RootedAndroidGameVM.Setup\RootedAndroidGameVM.Setup.csproj') -c $Configuration -r win-x64 --self-contained true -p:PublishSingleFile=true -o $setupOutput
if ($LASTEXITCODE -ne 0) { throw 'Setup publish failed.' }

function Invoke-AuthenticodeSign {
    param([Parameter(Mandatory)][string]$Path)
    $signTool = Get-Command signtool.exe -ErrorAction Stop
    & $signTool.Source sign /sha1 $SigningCertificateThumbprint /fd SHA256 /tr 'http://timestamp.digicert.com' /td SHA256 $Path
    if ($LASTEXITCODE -ne 0) { throw "Authenticode signing failed for $Path" }
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne 'Valid') { throw "Authenticode verification failed for ${Path}: $($signature.Status)" }
}

if ($SigningCertificateThumbprint) {
    Invoke-AuthenticodeSign (Join-Path $launcherOutput 'RootedAndroidGameVM.exe')
    Invoke-AuthenticodeSign (Join-Path $setupOutput 'RootedAndroidGameVM.Setup.exe')
} elseif (-not $AllowUnsignedLocalCandidate) {
    throw 'A trusted code-signing certificate thumbprint is required. Use -AllowUnsignedLocalCandidate only for a non-public local candidate.'
}

& $innoCompiler $innoScript
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed.' }

$installer = Get-ChildItem -LiteralPath $releaseDirectory -Filter 'RootedAndroidGameVM-Setup-*-x64.exe' -File |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
if (-not $installer) { throw 'Release installer was not produced.' }
if (-not $SigningCertificateThumbprint) {
    $unsignedPath = Join-Path $releaseDirectory (
        [IO.Path]::GetFileNameWithoutExtension($installer.Name) + '-UNSIGNED.exe')
    [IO.File]::Move($installer.FullName, $unsignedPath, $true)
    $installer = Get-Item -LiteralPath $unsignedPath
}
if ($SigningCertificateThumbprint) {
    Invoke-AuthenticodeSign $installer.FullName
}

& (Join-Path $projectRoot 'build\Invoke-PostPackageE2E.ps1') -InstallerPath $installer.FullName -ProductRoot $resolvedCleanRoot -Configuration $Configuration -DependencyCache $E2EDependencyCache
if ($LASTEXITCODE -ne 0) { throw 'Post-package final installer E2E failed.' }

Copy-Item -LiteralPath (Join-Path $projectRoot 'release\THIRD_PARTY_NOTICES.md') -Destination $releaseDirectory -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'release\CHANGELOG.md') -Destination $releaseDirectory -Force

$sbomPath = Join-Path $releaseDirectory 'SBOM.spdx.json'
dotnet run --project (Join-Path $projectRoot 'tools\RootedAndroidGameVM.ReleaseTool\RootedAndroidGameVM.ReleaseTool.csproj') -c $Configuration --no-restore -- generate-sbom $productVersion (Join-Path $launcherOutput 'RootedAndroidGameVM.exe') (Join-Path $setupOutput 'RootedAndroidGameVM.Setup.exe') $installer.FullName $sbomPath
if ($LASTEXITCODE -ne 0) { throw 'SPDX SBOM generation failed.' }

$sbom = Get-Content -Raw -LiteralPath $sbomPath | ConvertFrom-Json
if ($sbom.spdxVersion -ne 'SPDX-2.3' -or
    $sbom.packages.Count -ne ($manifest.components.Count + 1) -or
    $sbom.files.Count -ne 3 -or
    $sbom.files.Where({
        $sha256 = @($_.checksums.Where({ $_.algorithm -eq 'SHA256' }).checksumValue)
        $sha1 = @($_.checksums.Where({ $_.algorithm -eq 'SHA1' }).checksumValue)
        $sha256.Count -ne 1 -or $sha256[0].Length -ne 64 -or
        $sha1.Count -ne 1 -or $sha1[0].Length -ne 40
    }).Count -ne 0) {
    throw 'Generated SPDX SBOM is incomplete.'
}
& (Join-Path $projectRoot 'build\Validate-Spdx.ps1') -SbomPath $sbomPath
if ($LASTEXITCODE -ne 0) { throw 'Official SPDX semantic validation failed.' }

$forbiddenExtensions = @('.apk', '.apks', '.xapk', '.aff', '.img', '.vhd', '.vhdx', '.qcow2', '.keystore', '.jks', '.pfx', '.key')
$forbidden = Get-ChildItem -LiteralPath $releaseDirectory -Recurse -File |
    Where-Object { $forbiddenExtensions -contains $_.Extension.ToLowerInvariant() -or $_.Name -in @('secrets.json', 'appsettings.Local.json') }
if ($forbidden) {
    throw "Forbidden Release content detected: $($forbidden.FullName -join ', ')"
}

$expectedAssets = @(
    'CHANGELOG.md',
    $installer.Name,
    ($installer.Name + '.sha256'),
    'SBOM.spdx.json',
    'THIRD_PARTY_NOTICES.md'
)
$unexpectedAssets = Get-ChildItem -LiteralPath $releaseDirectory -Recurse -File |
    Where-Object {
        $_.DirectoryName -ne $releaseDirectory -or
        $_.Name -notin $expectedAssets
    }
if ($unexpectedAssets) {
    throw "Unexpected Release assets: $($unexpectedAssets.Name -join ', ')"
}
if ($installer.Length -gt 120MB) {
    throw "Installer exceeds the 120 MB Release size limit: $($installer.Length) bytes."
}

$secretPatterns = @(
    '-----BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY-----',
    'AKIA[0-9A-Z]{16}',
    'gh[pousr]_[A-Za-z0-9]{20,}',
    'github_pat_[A-Za-z0-9_]{20,}'
)
$textFiles = Get-ChildItem -LiteralPath $projectRoot -Recurse -File |
    Where-Object {
        $_.FullName -notmatch '\\(artifacts|bin|obj|\.git)\\' -and
        $_.Extension -in @('.cs', '.xaml', '.ps1', '.iss', '.md', '.json', '.csproj')
    }
foreach ($pattern in $secretPatterns) {
    $matches = $textFiles | Select-String -Pattern $pattern
    if ($matches) { throw "Potential secret material detected for pattern '$pattern'." }
}

function Assert-WindowsGuiExecutable {
    param([Parameter(Mandatory)][string]$Path)
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $peOffset = [BitConverter]::ToInt32($bytes, 0x3C)
    $optionalHeaderOffset = $peOffset + 24
    $subsystem = [BitConverter]::ToUInt16($bytes, $optionalHeaderOffset + 68)
    if ($subsystem -ne 2) {
        throw "$Path is not a Windows GUI subsystem executable."
    }
}

Assert-WindowsGuiExecutable (Join-Path $launcherOutput 'RootedAndroidGameVM.exe')
Assert-WindowsGuiExecutable (Join-Path $setupOutput 'RootedAndroidGameVM.Setup.exe')
Assert-WindowsGuiExecutable $installer.FullName

$digest = (Get-FileHash -LiteralPath $installer.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
$checksumPath = Join-Path $releaseDirectory ($installer.Name + '.sha256')
Set-Content -LiteralPath $checksumPath -Value "$digest  $($installer.Name)" -Encoding ascii

$missingAssets = $expectedAssets | Where-Object {
    -not (Test-Path -LiteralPath (Join-Path $releaseDirectory $_))
}
if ($missingAssets) {
    throw "Missing Release assets: $($missingAssets -join ', ')"
}

Write-Output "Release audit passed: $($installer.FullName)"
Write-Output "SHA-256: $digest"
