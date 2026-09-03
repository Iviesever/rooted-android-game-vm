param(
    [Parameter(Mandatory)]
    [string]$ProductRoot,
    [Parameter(Mandatory)]
    [string]$AllowedParent,
    [string]$RequiredLeafPrefix = 'rgvm-clean-'
)

$ErrorActionPreference = 'Stop'
$resolvedRoot = [IO.Path]::GetFullPath($ProductRoot)
$resolvedParent = [IO.Path]::GetFullPath($AllowedParent).TrimEnd('\') + '\'
$leaf = [IO.Path]::GetFileName($resolvedRoot)
if (-not $resolvedRoot.StartsWith($resolvedParent, [StringComparison]::OrdinalIgnoreCase) -or
    -not $leaf.StartsWith($RequiredLeafPrefix, [StringComparison]::Ordinal)) {
    throw "Unsafe isolated product cleanup target: $resolvedRoot"
}
if (-not (Test-Path -LiteralPath $resolvedRoot)) { exit 0 }

$adb = Join-Path $resolvedRoot 'runtime\android-sdk\platform-tools\adb.exe'
if (Test-Path -LiteralPath $adb) {
    try { & $adb -s emulator-5564 emu kill | Out-Null } catch { }
    try { & $adb kill-server | Out-Null } catch { }
}
Start-Sleep -Seconds 2

$processRoot = $resolvedRoot.TrimEnd('\') + '\'
Get-Process -ErrorAction SilentlyContinue | ForEach-Object {
    try { $executable = $_.Path } catch { $executable = $null }
    if ($executable -and
        [IO.Path]::GetFullPath($executable).StartsWith(
            $processRoot,
            [StringComparison]::OrdinalIgnoreCase)) {
        Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
    }
}
Start-Sleep -Seconds 1

Get-ChildItem -LiteralPath $resolvedRoot -Recurse -Force -File -ErrorAction SilentlyContinue |
    ForEach-Object { [IO.File]::SetAttributes($_.FullName, [IO.FileAttributes]::Normal) }
for ($attempt = 1; $attempt -le 5; $attempt++) {
    try {
        [IO.Directory]::Delete($resolvedRoot, $true)
        exit 0
    }
    catch {
        if ($attempt -ge 5) { throw }
        Start-Sleep -Seconds $attempt
    }
}
