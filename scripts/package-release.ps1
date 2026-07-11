param(
    [string]$Configuration = "Release",
    [string]$Version = "dev"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$releaseDir = Join-Path $repoRoot "release"
$normalPublish = Join-Path $repoRoot "PowerRecover.App\bin\$Configuration\net8.0-windows\win-x64\publish"
$bootPublish = Join-Path $repoRoot "PowerRecover.App\bin\$Configuration\net8.0-windows\win-x64\bootable-usb"

New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null

dotnet clean (Join-Path $repoRoot "PowerRecover.sln") -c $Configuration | Out-Host
dotnet restore (Join-Path $repoRoot "PowerRecover.sln") | Out-Host
dotnet build (Join-Path $repoRoot "PowerRecover.sln") -c $Configuration --no-restore | Out-Host
dotnet test (Join-Path $repoRoot "PowerRecover.Tests\PowerRecover.Tests.csproj") -c $Configuration --no-build | Out-Host

dotnet publish (Join-Path $repoRoot "PowerRecover.App\PowerRecover.App.csproj") `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o $normalPublish | Out-Host

dotnet publish (Join-Path $repoRoot "PowerRecover.App\PowerRecover.App.csproj") `
    -c $Configuration `
    -r win-x64 `
    -p:PublishProfile=BootableUsb | Out-Host

$normalZip = Join-Path $releaseDir "PowerRecover-win-x64-$Version.zip"
$bootZip = Join-Path $releaseDir "PowerRecover-bootable-usb-package-$Version.zip"
$checksums = Join-Path $releaseDir "SHA256SUMS.txt"

Remove-Item $normalZip -Force -ErrorAction SilentlyContinue
Remove-Item $bootZip -Force -ErrorAction SilentlyContinue
Remove-Item $checksums -Force -ErrorAction SilentlyContinue

Compress-Archive -Path (Join-Path $normalPublish "*") -DestinationPath $normalZip
Compress-Archive -Path (Join-Path $bootPublish "*"), (Join-Path $repoRoot "BootableUsb\*") -DestinationPath $bootZip

Get-FileHash $normalZip -Algorithm SHA256 |
    ForEach-Object { "$($_.Hash)  $(Split-Path $_.Path -Leaf)" } |
    Set-Content $checksums

Get-FileHash $bootZip -Algorithm SHA256 |
    ForEach-Object { "$($_.Hash)  $(Split-Path $_.Path -Leaf)" } |
    Add-Content $checksums

Write-Host "Release files created in $releaseDir"
