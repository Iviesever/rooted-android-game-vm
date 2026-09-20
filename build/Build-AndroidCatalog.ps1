param(
    [Parameter(Mandatory = $true)][string]$SdkRoot,
    [Parameter(Mandatory = $true)][string]$JavaHome,
    [switch]$Verify
)
$ErrorActionPreference = 'Stop'
$catalogRepo = Split-Path -Parent $PSScriptRoot
$catalogSourceRoot = Join-Path $catalogRepo 'tools/android-catalog/src'
$catalogSources = @(Get-ChildItem -LiteralPath $catalogSourceRoot -Filter '*.java' -Recurse | Sort-Object FullName | ForEach-Object FullName)
$catalogResources = Join-Path $catalogRepo 'src/RootedAndroidGameVM.Core/Debugging/Resources'
$catalogWork = Join-Path $catalogRepo ('artifacts/catalog-build-' + [Guid]::NewGuid().ToString('N'))
$catalogAndroidJar = Join-Path $SdkRoot 'platforms/android-36/android.jar'
$catalogD8 = Join-Path $SdkRoot 'build-tools/35.0.0/lib/d8.jar'
foreach ($required in @($catalogAndroidJar, $catalogD8, (Join-Path $JavaHome 'bin/javac.exe'))) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Missing catalog build input: $required" }
}
New-Item -ItemType Directory -Force -Path "$catalogWork/classes", "$catalogWork/dex", $catalogResources | Out-Null
$catalogCompileArgs = @('-J-Duser.language=en', '-J-Duser.country=US', '-Xlint:-options', '-encoding', 'UTF-8', '-g:none',
    '-source', '8', '-target', '8', '-bootclasspath', $catalogAndroidJar, '-d', "$catalogWork/classes") + $catalogSources
& (Join-Path $JavaHome 'bin/javac.exe') @catalogCompileArgs
if ($LASTEXITCODE -ne 0) { throw 'Android catalog javac failed.' }
$catalogClasses = @(Get-ChildItem -LiteralPath "$catalogWork/classes" -Filter '*.class' -Recurse | Sort-Object FullName | ForEach-Object FullName)
& (Join-Path $JavaHome 'bin/java.exe') -cp $catalogD8 com.android.tools.r8.D8 --release --min-api 28 --lib $catalogAndroidJar --output "$catalogWork/dex" @catalogClasses
if ($LASTEXITCODE -ne 0) { throw 'Android catalog D8 failed.' }
function Get-CatalogHash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
# Source text is normalized so Git's Windows line-ending conversion does not change its identity.
$catalogSourceIndex = foreach ($source in $catalogSources) {
    $sourceBytes = [Text.Encoding]::UTF8.GetBytes([IO.File]::ReadAllText($source).Replace("`r`n", "`n"))
    $sourceHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($sourceBytes)).ToLowerInvariant()
    [IO.Path]::GetRelativePath($catalogSourceRoot, $source).Replace('\', '/') + '=' + $sourceHash + "`n"
}
$catalogSourceBytes = [Text.Encoding]::UTF8.GetBytes(($catalogSourceIndex -join ''))
$catalogManifest = [ordered]@{
    schemaVersion = 1
    sourceFormat = 'sorted-java-path=sha256-lf'
    entryPoint = 'dev.rgvm.catalog.Main'
    sourceSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($catalogSourceBytes)).ToLowerInvariant()
    dexSha256 = Get-CatalogHash "$catalogWork/dex/classes.dex"
    androidJarSha256 = Get-CatalogHash $catalogAndroidJar
    d8JarSha256 = Get-CatalogHash $catalogD8
    compileApi = 36
    minApi = 28
    buildTools = '35.0.0'
}
$catalogManifestPath = Join-Path $catalogResources 'catalog-manifest.json'
if ($Verify) {
    $recorded = Get-Content -LiteralPath $catalogManifestPath -Raw | ConvertFrom-Json
    foreach ($key in $catalogManifest.Keys) {
        if ($recorded.$key -ne $catalogManifest[$key]) { throw "Catalog build differs: $key" }
    }
    if ((Get-CatalogHash (Join-Path $catalogResources 'catalog.dex')) -ne $catalogManifest.dexSha256) { throw 'Embedded catalog DEX differs.' }
    Write-Output "Catalog source rebuild verified: $($catalogManifest.dexSha256)"
} else {
    Copy-Item -LiteralPath "$catalogWork/dex/classes.dex" -Destination (Join-Path $catalogResources 'catalog.dex')
    $catalogManifest | ConvertTo-Json | Set-Content -LiteralPath $catalogManifestPath -Encoding utf8NoBOM
    Write-Output "Catalog artifact updated: $($catalogManifest.dexSha256)"
}
