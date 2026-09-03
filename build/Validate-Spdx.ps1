param(
    [Parameter(Mandatory)]
    [string]$SbomPath
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$resolvedSbom = [IO.Path]::GetFullPath($SbomPath)
if (-not (Test-Path -LiteralPath $resolvedSbom)) {
    throw "SPDX document does not exist: $resolvedSbom"
}

$python = Get-Command python.exe -ErrorAction Stop
$validatorRoot = Join-Path $projectRoot 'artifacts\tools\spdx-tools-python-0.8.5'
$sentinel = Join-Path $validatorRoot '.rgvm-version'
$requirements = Join-Path $PSScriptRoot 'spdx-tools-requirements.txt'
$requirementsHash = (Get-FileHash -LiteralPath $requirements -Algorithm SHA256).Hash.ToLowerInvariant()
$receipt = "0.8.5|python3.13-x64|$requirementsHash"
if (-not (Test-Path -LiteralPath $sentinel) -or
    (Get-Content -Raw -LiteralPath $sentinel).Trim() -ne $receipt -or
    -not (Test-Path -LiteralPath (Join-Path $validatorRoot 'spdx_tools\spdx\__init__.py'))) {
    throw 'SPDX validator is not prepared from the repository hash lock.'
}

$validationProgram = @'
import sys
sys.path.insert(0, sys.argv[1])
from spdx_tools.spdx.parser.parse_anything import parse_file
from spdx_tools.spdx.validation.document_validator import validate_full_spdx_document

document = parse_file(sys.argv[2])
messages = validate_full_spdx_document(document)
if messages:
    for message in messages:
        print(message.validation_message, file=sys.stderr)
    raise SystemExit(1)
print("Official SPDX Tools validation passed.")
'@

& $python.Source -c $validationProgram $validatorRoot $resolvedSbom
if ($LASTEXITCODE -ne 0) { throw 'SPDX Tools rejected the generated document.' }
