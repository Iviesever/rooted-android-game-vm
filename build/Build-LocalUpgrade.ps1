param([string]$Configuration='Release')
$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $projectRoot
$stamp=Get-Date -Format 'yyyyMMdd-HHmmss'
$candidate=Join-Path $projectRoot "artifacts/local-upgrade-$stamp"
New-Item -ItemType Directory -Path $candidate | Out-Null
$manifest=Get-Content profiles/dependencies.json -Raw | ConvertFrom-Json
$version=[regex]::Match((Get-Content installer/RootedAndroidGameVM.iss -Raw),'#define AppVersion "([^"]+)"').Groups[1].Value
if ((& dotnet --version).Trim() -ne ($manifest.components | Where-Object id -eq 'dotnet-sdk').version) { throw 'Pinned SDK mismatch.' }
dotnet test tests/RootedAndroidGameVM.Core.Tests -c $Configuration --filter 'Category!=LocalIntegration&Category!=CleanE2E' --logger "trx;LogFileName=unit.trx" --results-directory $candidate
if ($LASTEXITCODE -ne 0) { throw 'Local automated test gate failed.' }
foreach($component in @('Launcher','Setup','Cli')) {
 dotnet publish "src/RootedAndroidGameVM.$component/RootedAndroidGameVM.$component.csproj" -c $Configuration -r win-x64 --self-contained true -o "artifacts/publish/$component"
 if($LASTEXITCODE -ne 0){throw "$component publish failed."}
}
$iscc=Join-Path $env:LOCALAPPDATA 'Programs/Inno Setup 6/ISCC.exe'
& $iscc "/O$candidate" "/FRootedAndroidGameVM-Setup-$version-x64-UNSIGNED" installer/RootedAndroidGameVM.iss
if($LASTEXITCODE -ne 0){throw 'Installer compilation failed.'}
$installer=Join-Path $candidate "RootedAndroidGameVM-Setup-$version-x64-UNSIGNED.exe"
if((Get-AuthenticodeSignature -LiteralPath $installer).Status -ne 'NotSigned'){throw 'Local package signature status unexpected.'}
if((Get-Item -LiteralPath $installer).Length -gt 120MB){throw 'Installer exceeds existing size gate.'}
$sandbox=Join-Path $candidate 'sandbox'
New-Item -ItemType Directory -Path $sandbox | Out-Null
& $iscc /DRgvmSandbox "/O$sandbox" /FSandbox-Installer installer/RootedAndroidGameVM.iss
if($LASTEXITCODE -ne 0){throw 'Isolated installer compilation failed.'}
$testProgram=Join-Path $sandbox 'program'
$beforeKey=Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{2B456CBE-77EC-4F4B-911A-32D78A42F287}_is1' -ErrorAction SilentlyContinue
$beforeLocation=[string]$beforeKey.InstallLocation
$beforeVersion=[string]$beforeKey.DisplayVersion
foreach($pass in 1..2){
 $run=Start-Process -FilePath (Join-Path $sandbox 'Sandbox-Installer.exe') -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/TASKS=""',"/DIR=`"$testProgram`"","/LOG=`"$sandbox/install-$pass.log`"") -WindowStyle Hidden -Wait -PassThru
 if($run.ExitCode -ne 0){throw "Isolated install pass $pass failed."}
 if($pass -eq 1){Set-Content (Join-Path $testProgram 'retained-user-data.txt') 'upgrade-retention-probe'}
 if((Get-Content (Join-Path $testProgram 'retained-user-data.txt')).Trim() -ne 'upgrade-retention-probe'){throw 'Upgrade removed pre-existing data.'}
 foreach($name in @('RootedAndroidGameVM.exe','RootedAndroidGameVM.Setup.exe','RootedAndroidGameVM.Cli.exe')){
  if(-not(Test-Path (Join-Path $testProgram $name))){throw "Missing installed file: $name"}
 }
 $help=& (Join-Path $testProgram 'RootedAndroidGameVM.Cli.exe') help | ConvertFrom-Json
 if($LASTEXITCODE -ne 0 -or $help.schemaVersion -ne 1 -or $help.version -ne $version){throw 'Installed CLI smoke failed.'}
}
$afterKey=Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{2B456CBE-77EC-4F4B-911A-32D78A42F287}_is1' -ErrorAction SilentlyContinue
if([string]$afterKey.InstallLocation -ne $beforeLocation -or [string]$afterKey.DisplayVersion -ne $beforeVersion){throw 'Isolated test affected production installation.'}
@{freshInstall=$true;overlayUpgrade=$true;retainedUserData=$true;productionRegistrationUntouched=$true;testAppId='C218718D-510A-42A0-BFCB-16F546DAB90B';publicReleaseGatesRun=$false} | ConvertTo-Json | Set-Content (Join-Path $candidate 'installer-test.json')
$launcher=Join-Path $projectRoot 'artifacts/publish/Launcher/RootedAndroidGameVM.exe'
$setup=Join-Path $projectRoot 'artifacts/publish/Setup/RootedAndroidGameVM.Setup.exe'
$cli=Join-Path $projectRoot 'artifacts/publish/Cli/RootedAndroidGameVM.Cli.exe'
foreach($pe in @(@($launcher,2),@($setup,2),@($cli,3),@($installer,2))){
 $bytes=[IO.File]::ReadAllBytes($pe[0]);$offset=[BitConverter]::ToInt32($bytes,0x3c)
 if([BitConverter]::ToUInt16($bytes,$offset+24+68) -ne $pe[1]){throw "PE subsystem mismatch: $($pe[0])"}
}
$sbom=Join-Path $candidate 'SBOM.spdx.json'
dotnet run --project tools/RootedAndroidGameVM.ReleaseTool -c $Configuration -- generate-sbom $version $launcher $setup $installer $sbom $cli
if($LASTEXITCODE -ne 0){throw 'SBOM generation failed.'}
& build/Prepare-SpdxValidator.ps1 -Offline
if($LASTEXITCODE -ne 0){throw 'Pinned SPDX validator unavailable.'}
& build/Validate-Spdx.ps1 -SbomPath $sbom
if($LASTEXITCODE -ne 0){throw 'Official SPDX validation failed.'}
Copy-Item release/THIRD_PARTY_NOTICES.md,release/CHANGELOG.md -Destination $candidate
$digest=(Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
"$digest  $([IO.Path]::GetFileName($installer))" | Set-Content "$installer.sha256" -Encoding ascii
@{version=$version;installer=$installer;sha256=$digest;unsigned=$true;localOnly=$true;publicReleaseGatesRun=$false} | ConvertTo-Json | Set-Content (Join-Path $candidate 'local-audit.json')
Write-Output "Local candidate passed: $candidate"
Write-Output 'This does not replace the unchanged clean-runner/publication gates in Build-Release.ps1.'
