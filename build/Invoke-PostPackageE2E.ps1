param(
    [Parameter(Mandatory)]
    [string]$InstallerPath,
    [Parameter(Mandatory)]
    [string]$ProductRoot,
    [string]$Configuration = 'Release',
    [string]$DependencyCache,
    [string]$ExpectedSignerThumbprint
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts')) + [IO.Path]::DirectorySeparatorChar
$testRoot = [IO.Path]::GetFullPath((Join-Path $artifactsRoot 'post-package-e2e'))
$programRoot = Join-Path $testRoot 'program'
$downloadRoot = Join-Path $testRoot 'downloads'
$exportRoot = Join-Path $testRoot 'exports'
$resolvedProductRoot = [IO.Path]::GetFullPath($ProductRoot)
$resolvedInstaller = [IO.Path]::GetFullPath($InstallerPath)
$desktopShortcut = Join-Path ([Environment]::GetFolderPath(
    [Environment+SpecialFolder]::DesktopDirectory)) 'Rooted Android Game VM.lnk'
$startMenuGroup = Join-Path ([Environment]::GetFolderPath(
    [Environment+SpecialFolder]::Programs)) 'Rooted Android Game VM'

function Normalize-CertificateThumbprint {
    param([Parameter(Mandatory)][string]$Thumbprint)
    $normalized = ($Thumbprint -replace '\s', '').ToUpperInvariant()
    if ($normalized -notmatch '^[0-9A-F]{40,128}$') {
        throw 'The expected Authenticode signer thumbprint is invalid.'
    }
    return $normalized
}

function Assert-AuthenticodeSignature {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ExpectedThumbprint
    )
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne 'Valid') {
        throw "Invalid Authenticode signature for ${Path}: $($signature.Status)"
    }
    $actualThumbprint = Normalize-CertificateThumbprint $signature.SignerCertificate.Thumbprint
    if ($actualThumbprint -ne $ExpectedThumbprint) {
        throw "Unexpected Authenticode signer for $Path."
    }
}

if (-not $testRoot.StartsWith($artifactsRoot, [StringComparison]::OrdinalIgnoreCase) -or
    (Split-Path $testRoot -Leaf) -ne 'post-package-e2e') {
    throw 'Unsafe post-package E2E root.'
}
if (Test-Path -LiteralPath $testRoot) {
    throw "Post-package E2E root must be absent: $testRoot"
}
if (-not (Test-Path -LiteralPath $resolvedInstaller)) {
    throw "Installer does not exist: $resolvedInstaller"
}
$normalizedExpectedSigner = if ([string]::IsNullOrWhiteSpace($ExpectedSignerThumbprint)) {
    $null
} else {
    Normalize-CertificateThumbprint $ExpectedSignerThumbprint
}
if ($normalizedExpectedSigner) {
    Assert-AuthenticodeSignature $resolvedInstaller $normalizedExpectedSigner
}
if ((Test-Path -LiteralPath $desktopShortcut) -or
    (Test-Path -LiteralPath $startMenuGroup)) {
    throw 'Post-package E2E refuses to overwrite existing product shortcuts.'
}

$uninstallKeyName = '{2B456CBE-77EC-4F4B-911A-32D78A42F287}_is1'
$uninstallRegistryPath = 'Software\Microsoft\Windows\CurrentVersion\Uninstall'
$originalLicenseGate = [Environment]::GetEnvironmentVariable(
    'RGVM_E2E_ACCEPT_SDK_LICENSE',
    [EnvironmentVariableTarget]::Process)
$originalUninstallMode = [Environment]::GetEnvironmentVariable(
    'RGVM_E2E_UNINSTALL_MODE',
    [EnvironmentVariableTarget]::Process)
$preflightRegistry = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey(
    "$uninstallRegistryPath\$uninstallKeyName")
if ($preflightRegistry) {
    $existingLocation = [string]$preflightRegistry.GetValue('InstallLocation')
    $preflightRegistry.Dispose()
    throw "Post-package E2E refuses to overwrite an existing installation at $existingLocation"
}

try {
    New-Item -ItemType Directory -Path $programRoot, $downloadRoot, $exportRoot -Force | Out-Null
    $install = Start-Process -FilePath $resolvedInstaller -ArgumentList @(
        '/VERYSILENT',
        '/SUPPRESSMSGBOXES',
        '/NORESTART',
        '/TASKS="desktopicon"',
        "/DIR=$programRoot"
    ) -Wait -PassThru -WindowStyle Hidden
    if ($install.ExitCode -ne 0) {
        throw "Final Inno installer exited with $($install.ExitCode)."
    }

    $installedSetup = Join-Path $programRoot 'RootedAndroidGameVM.Setup.exe'
    $installedLauncher = Join-Path $programRoot 'RootedAndroidGameVM.exe'
    $uninstaller = Join-Path $programRoot 'unins000.exe'
    foreach ($required in @($installedSetup, $installedLauncher, $uninstaller)) {
        if (-not (Test-Path -LiteralPath $required)) {
            throw "Installed GUI executable is missing: $required"
        }
    }
    if ($normalizedExpectedSigner) {
        foreach ($signedExecutable in @($installedLauncher, $installedSetup, $uninstaller)) {
            Assert-AuthenticodeSignature $signedExecutable $normalizedExpectedSigner
        }
    }
    $startMenuShortcut = Join-Path $startMenuGroup '配置 Rooted Android Game VM.lnk'
    $dailyStartMenuShortcut = Join-Path $startMenuGroup 'Rooted Android Game VM.lnk'
    if (-not (Test-Path -LiteralPath $desktopShortcut) -or
        -not (Test-Path -LiteralPath $startMenuShortcut)) {
        throw 'Final installer did not create the expected double-click shortcuts.'
    }
    if (Test-Path -LiteralPath $dailyStartMenuShortcut) {
        throw 'Daily Start Menu shortcut must be created only after Setup succeeds.'
    }

    [Environment]::SetEnvironmentVariable(
        'RGVM_E2E_ACCEPT_SDK_LICENSE',
        '1',
        [EnvironmentVariableTarget]::Process)
    $setupArguments = @(
        '--e2e',
        '--product-root', $resolvedProductRoot,
        '--avd-name', 'rgvm_clean_test_api35',
        '--port', '5564'
    )
    if ([Environment]::GetEnvironmentVariable('RGVM_E2E_HEADLESS') -eq '1') {
        $setupArguments += '--headless'
    }
    $setup = Start-Process -FilePath $installedSetup -ArgumentList $setupArguments `
        -PassThru -WindowStyle Hidden
    $setup.WaitForExit()
    $setup.Refresh()
    if ($setup.ExitCode -ne 0) {
        $resultPath = Join-Path $resolvedProductRoot 'setup-exe-e2e-result.json'
        $detail = if (Test-Path -LiteralPath $resultPath) {
            Get-Content -Raw -LiteralPath $resultPath
        } else {
            'No Setup E2E result file was created.'
        }
        throw "Installed Setup.exe E2E failed with $($setup.ExitCode): $detail"
    }
    $setupResult = Get-Content -Raw -LiteralPath (
        Join-Path $resolvedProductRoot 'setup-exe-e2e-result.json') | ConvertFrom-Json
    if ($setupResult.success -ne $true) {
        throw 'Installed Setup.exe did not record success.'
    }
    if (-not (Test-Path -LiteralPath $dailyStartMenuShortcut)) {
        throw 'Successful installed Setup.exe did not create the daily Start Menu shortcut.'
    }

    $releaseTool = Join-Path $projectRoot 'tools\RootedAndroidGameVM.ReleaseTool\RootedAndroidGameVM.ReleaseTool.csproj'
    $testApk = Join-Path $downloadRoot 'MaterialFiles-v1.7.4.apk'
    if ($DependencyCache) {
        $cachedApk = Join-Path ([IO.Path]::GetFullPath($DependencyCache)) 'MaterialFiles-v1.7.4.apk'
        if (Test-Path -LiteralPath $cachedApk) {
            Copy-Item -LiteralPath $cachedApk -Destination $testApk
        }
    }
    dotnet run --project $releaseTool -c $Configuration --no-restore -- download material-files-e2e $testApk
    if ($LASTEXITCODE -ne 0) { throw 'Pinned open-source E2E APK download failed.' }

    dotnet run --project $releaseTool -c $Configuration --no-restore -- verify-apk-export (Join-Path $resolvedProductRoot 'runtime\android-sdk') (Join-Path $resolvedProductRoot 'runtime\avd') 'rgvm_clean_test_api35' '5564' $testApk $exportRoot
    if ($LASTEXITCODE -ne 0) { throw 'APK install and private-data export E2E failed.' }

    $windowResults = @()
    foreach ($gui in @(
        @{ Path = $installedLauncher; Title = 'Rooted Android Game VM' },
        @{ Path = $installedSetup; Title = '安装 Rooted Android Game VM' }
    )) {
        $process = Start-Process -FilePath $gui.Path -PassThru
        Start-Sleep -Seconds 3
        $process.Refresh()
        $windowResults += [pscustomobject]@{
            Executable = [IO.Path]::GetFileName($gui.Path)
            ExpectedTitle = $gui.Title
            HasExited = $process.HasExited
            MainWindowTitle = $process.MainWindowTitle
            MainWindowHandle = $process.MainWindowHandle.ToInt64()
        }
        if (-not $process.HasExited) {
            Stop-Process -Id $process.Id -Force
            $process.WaitForExit()
        }
    }
    if ($windowResults.Where({
        $_.HasExited -or $_.MainWindowHandle -eq 0 -or $_.MainWindowTitle -ne $_.ExpectedTitle
    }).Count -ne 0) {
        throw "GUI smoke check failed: $($windowResults | ConvertTo-Json -Compress)"
    }
    Write-Output ($windowResults | ConvertTo-Json -Compress)

    [Environment]::SetEnvironmentVariable(
        'RGVM_E2E_UNINSTALL_MODE',
        'program',
        [EnvironmentVariableTarget]::Process)
    $uninstall = Start-Process -FilePath $uninstaller -ArgumentList @(
        '/VERYSILENT',
        '/SUPPRESSMSGBOXES',
        '/NORESTART'
    ) -Wait -PassThru -WindowStyle Hidden
    $uninstallDeadline = (Get-Date).AddSeconds(15)
    while (((Test-Path -LiteralPath $installedLauncher) -or
            (Test-Path -LiteralPath $installedSetup)) -and
           (Get-Date) -lt $uninstallDeadline) {
        Start-Sleep -Milliseconds 500
    }
    $registryCheck = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey(
        "$uninstallRegistryPath\$uninstallKeyName")
    $uninstallRegistrationRemains = $null -ne $registryCheck
    if ($registryCheck) { $registryCheck.Dispose() }
    $productAvd = Join-Path $resolvedProductRoot 'runtime\avd\rgvm_clean_test_api35.avd'
    if ($uninstall.ExitCode -ne 0 -or
        (Test-Path -LiteralPath $installedLauncher) -or
        (Test-Path -LiteralPath $installedSetup) -or
        $uninstallRegistrationRemains -or
        -not (Test-Path -LiteralPath $productAvd) -or
        (Test-Path -LiteralPath $desktopShortcut) -or
        (Test-Path -LiteralPath $startMenuGroup)) {
        throw "Program-only uninstall E2E failed with $($uninstall.ExitCode)."
    }
    Write-Output 'Post-package E2E passed.'
}
finally {
    [Environment]::SetEnvironmentVariable(
        'RGVM_E2E_ACCEPT_SDK_LICENSE',
        $originalLicenseGate,
        [EnvironmentVariableTarget]::Process)
    [Environment]::SetEnvironmentVariable(
        'RGVM_E2E_UNINSTALL_MODE',
        $originalUninstallMode,
        [EnvironmentVariableTarget]::Process)

    $registry = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey(
        $uninstallRegistryPath,
        $true)
    if ($registry) {
        $key = $registry.OpenSubKey($uninstallKeyName)
        if ($key) {
            $registered = ([string]$key.GetValue('InstallLocation')).TrimEnd('\')
            $key.Dispose()
            if ($registered -eq $programRoot.TrimEnd('\')) {
                $registry.DeleteSubKeyTree($uninstallKeyName, $false)
            }
        }
        $registry.Dispose()
    }

    if ([IO.Directory]::Exists($startMenuGroup)) {
        [IO.Directory]::Delete($startMenuGroup, $true)
    }
    if ([IO.File]::Exists($desktopShortcut)) {
        [IO.File]::Delete($desktopShortcut)
    }
    if ([IO.Directory]::Exists($testRoot)) {
        Get-ChildItem -LiteralPath $testRoot -Recurse -Force -File -ErrorAction SilentlyContinue |
            ForEach-Object { [IO.File]::SetAttributes($_.FullName, [IO.FileAttributes]::Normal) }
        [IO.Directory]::Delete($testRoot, $true)
    }
}
