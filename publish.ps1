<#
.SYNOPSIS
    Builds a single-file, self-contained CinemaWatch.exe (no .NET runtime needed on the target PC)
    plus a SHA-256 checksum file.
.EXAMPLE
    .\publish.ps1
    .\publish.ps1 -Version 1.0.1
    Output: .\artifacts\CinemaWatch.exe and .\artifacts\CinemaWatch.exe.sha256
#>
param(
    [string] $Version,
    [string] $Runtime = 'win-x64',
    [string] $Output  = (Join-Path $PSScriptRoot 'artifacts')
)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src\CinemaWatch.App\CinemaWatch.App.csproj'

$arguments = @(
    'publish', $project,
    '-c', 'Release',
    '-r', $Runtime,
    '--self-contained', 'true',
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true',
    '-p:DebugType=None',
    '-o', $Output
)
if ($Version) { $arguments += "-p:Version=$Version" }

& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

$exe = Join-Path $Output 'CinemaWatch.exe'
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path "$exe.sha256" -Value "$hash  CinemaWatch.exe" -Encoding ascii

Get-Item $exe | Select-Object FullName, @{ Name = 'SizeMB'; Expression = { [math]::Round($_.Length / 1MB, 1) } }
"SHA-256: $hash"
