param(
    [string]$SdkRoot = $env:ANDROID_HOME,
    [string]$JavaHome = $env:JAVA_HOME_21_X64
)
$ErrorActionPreference = 'Stop'
if (-not $SdkRoot) { $SdkRoot = Join-Path $env:LOCALAPPDATA 'Android/Sdk' }
if (-not $JavaHome) { $JavaHome = $env:JAVA_HOME }
if (-not $JavaHome -or -not (Test-Path -LiteralPath (Join-Path $JavaHome 'bin/javac.exe'))) {
    throw 'Provide -JavaHome pointing to a JDK 21 installation to rebuild the embedded Android catalog.'
}
$catalogInputs = @((Join-Path $SdkRoot 'platforms/android-36/android.jar'), (Join-Path $SdkRoot 'build-tools/35.0.0/lib/d8.jar'))
if ($catalogInputs.Where({ -not (Test-Path -LiteralPath $_) }).Count -gt 0) {
    $catalogManager = Join-Path $SdkRoot 'cmdline-tools/latest/bin/sdkmanager.bat'
    if (-not (Test-Path -LiteralPath $catalogManager)) { throw 'Android SDK command-line tools are required on the build machine.' }
    & $catalogManager "--sdk_root=$SdkRoot" 'platforms;android-36' 'build-tools;35.0.0'
    if ($LASTEXITCODE -ne 0) { throw 'Unable to provision Android catalog compilation dependencies.' }
}
& (Join-Path $PSScriptRoot 'Build-AndroidCatalog.ps1') -SdkRoot $SdkRoot -JavaHome $JavaHome -Verify
