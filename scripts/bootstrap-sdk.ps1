$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskVersion = (Get-Content (Join-Path $taskRoot 'global.json') -Raw | ConvertFrom-Json).sdk.version
$taskDirectory = Join-Path $taskRoot '.tools/dotnet'
$taskExecutable = Join-Path $taskDirectory 'dotnet.exe'
if (Test-Path $taskExecutable) {
    & $taskExecutable --version
    if ($LASTEXITCODE -ne 0) { throw 'Existing local SDK failed validation.' }
    return
}
$taskMetadata = Invoke-RestMethod 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json'
$taskSdk = @($taskMetadata.releases | ForEach-Object { $_.sdks } | Where-Object { $_.version -eq $taskVersion })[0]
if (-not $taskSdk) { $taskSdk = @($taskMetadata.releases | ForEach-Object { $_.sdk } | Where-Object { $_.version -eq $taskVersion })[0] }
$taskAsset = @($taskSdk.files | Where-Object { $_.rid -eq 'win-x64' -and $_.url.EndsWith('.zip') })[0]
if (-not $taskAsset) { throw "Official win-x64 archive for SDK $taskVersion was not found." }
$taskDownloadDirectory = Join-Path $taskRoot '.tools/downloads'
New-Item -ItemType Directory -Force -Path $taskDirectory,$taskDownloadDirectory | Out-Null
$taskArchive = Join-Path $taskDownloadDirectory "dotnet-sdk-$taskVersion-win-x64.zip"
$ProgressPreference = 'SilentlyContinue'
Invoke-WebRequest -Uri $taskAsset.url -OutFile $taskArchive
if ((Get-FileHash $taskArchive -Algorithm SHA512).Hash -ne $taskAsset.hash) { throw 'SDK SHA512 verification failed.' }
Expand-Archive -LiteralPath $taskArchive -DestinationPath $taskDirectory -Force
& $taskExecutable --info
if ($LASTEXITCODE -ne 0) { throw 'Installed local SDK failed validation.' }
