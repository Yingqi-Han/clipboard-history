param([switch]$LockedMode)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dotnet = $env:YINGQI_DOTNET
if (-not $dotnet) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$solution = Join-Path $root 'ClipboardHistory.slnx'
$restore = @('restore', $solution)
if ($LockedMode) { $restore += '--locked-mode' }
& $dotnet @restore
if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
& $dotnet build $solution -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
& $dotnet test (Join-Path $root 'tests\ClipboardHistoryComponent.Tests\ClipboardHistoryComponent.Tests.csproj') -c Release --no-build --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
