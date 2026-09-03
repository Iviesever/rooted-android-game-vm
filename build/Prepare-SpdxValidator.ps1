param(
    [switch]$Offline
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$python = Get-Command python.exe -ErrorAction Stop
$pythonVersion = (& $python.Source -c 'import platform,sys; print(f"{sys.version_info.major}.{sys.version_info.minor}:{platform.machine().lower()}")').Trim()
if ($pythonVersion -notmatch '^3\.13:(amd64|x86_64)$') {
    throw "SPDX validator requires pinned Python 3.13 x64 wheels; found $pythonVersion."
}

$requirements = Join-Path $PSScriptRoot 'spdx-tools-requirements.txt'
$requirementsHash = (Get-FileHash -LiteralPath $requirements -Algorithm SHA256).Hash.ToLowerInvariant()
$validatorRoot = Join-Path $projectRoot 'artifacts\tools\spdx-tools-python-0.8.5'
$wheelhouse = Join-Path $projectRoot 'artifacts\tools\spdx-wheelhouse-0.8.5'
$sentinel = Join-Path $validatorRoot '.rgvm-version'
$receipt = "0.8.5|python3.13-x64|$requirementsHash"
if ((Test-Path -LiteralPath $sentinel) -and
    (Get-Content -Raw -LiteralPath $sentinel).Trim() -eq $receipt -and
    (Test-Path -LiteralPath (Join-Path $validatorRoot 'spdx_tools\spdx\__init__.py'))) {
    Write-Output 'Pinned SPDX validator is prepared.'
    exit 0
}

if (-not $Offline) {
    if (Test-Path -LiteralPath $wheelhouse) {
        [IO.Directory]::Delete($wheelhouse, $true)
    }
    [IO.Directory]::CreateDirectory($wheelhouse) | Out-Null
    & $python.Source -m pip download --disable-pip-version-check --no-input `
        --only-binary=:all: --require-hashes --index-url 'https://pypi.org/simple' `
        --dest $wheelhouse --requirement $requirements
    if ($LASTEXITCODE -ne 0) { throw 'Unable to download hash-locked SPDX validator wheels.' }
}
elseif (-not (Test-Path -LiteralPath $wheelhouse)) {
    throw 'Signed builds require a pre-provisioned hash-locked SPDX wheelhouse.'
}

if (Test-Path -LiteralPath $validatorRoot) {
    [IO.Directory]::Delete($validatorRoot, $true)
}
[IO.Directory]::CreateDirectory($validatorRoot) | Out-Null
& $python.Source -m pip install --disable-pip-version-check --no-input `
    --only-binary=:all: --require-hashes --no-index --find-links $wheelhouse `
    --target $validatorRoot --requirement $requirements
if ($LASTEXITCODE -ne 0) { throw 'Unable to install the hash-locked SPDX validator wheel set.' }
[IO.File]::WriteAllText($sentinel, $receipt)
Write-Output 'Pinned SPDX validator prepared from hash-locked wheels.'
