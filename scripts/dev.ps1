param(
    [ValidateSet('Restore','Build','Test','Run','Publish')]
    [string]$Action = 'Test',
    [switch]$Locked
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskDotnet = Join-Path $taskRoot '.tools/dotnet/dotnet.exe'
if (-not (Test-Path $taskDotnet)) {
    $taskCommand = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $taskCommand) { throw 'Install the pinned SDK or run scripts/bootstrap-sdk.ps1 first.' }
    $taskDotnet = $taskCommand.Source
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_CLI_HOME = Join-Path $taskRoot '.tools/dotnet-home'
$env:NUGET_PACKAGES = Join-Path $taskRoot '.tools/nuget/packages'
Push-Location $taskRoot
try {
    $taskRestoreArguments = @('restore','LocalDocSearch.slnx')
    if ($Locked) { $taskRestoreArguments += '--locked-mode' }
    & $taskDotnet @taskRestoreArguments
    if ($LASTEXITCODE -ne 0) { throw 'NuGet restore failed.' }
    switch ($Action) {
        'Restore' { return }
        'Build' { & $taskDotnet build LocalDocSearch.slnx -c Release --no-restore }
        'Test' { & $taskDotnet test LocalDocSearch.slnx -c Release --no-restore --logger 'trx' --results-directory (Join-Path $taskRoot 'artifacts/history/builds/test-results') }
        'Run' { & $taskDotnet run --project src/LocalDocSearch.Desktop -c Release --no-restore }
        'Publish' { & $taskDotnet publish src/LocalDocSearch.Desktop -c Release --no-restore -o (Join-Path $taskRoot 'artifacts/publish') }
    }
    if ($LASTEXITCODE -ne 0) { throw "$Action failed (exit $LASTEXITCODE)." }
} finally {
    Pop-Location
}
