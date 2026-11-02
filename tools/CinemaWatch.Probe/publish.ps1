<#
.SYNOPSIS
    Builds CinemaWatch.Probe for the server: one self-contained Linux file (no .NET needed on the server) plus a
    SHA-256 checksum file to verify the copy on the server with 'sha256sum -c'.
.EXAMPLE
    .\tools\CinemaWatch.Probe\publish.ps1
    Output: .\artifacts\probe\CinemaWatch.Probe and .\artifacts\probe\CinemaWatch.Probe.sha256
#>
param(
    [string] $Runtime = 'linux-x64',
    [string] $Output  = (Join-Path $PSScriptRoot '..\..\artifacts\probe')
)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'CinemaWatch.Probe.csproj'
$Output = [System.IO.Path]::GetFullPath($Output)

$arguments = @(
    'publish', $project,
    '-c', 'Release',
    '-r', $Runtime,
    '--self-contained', 'true',
    '-p:PublishSingleFile=true',
    '-p:EnableCompressionInSingleFile=true',
    '-p:DebugType=embedded',
    '-o', $Output
)
& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

$file = Join-Path $Output 'CinemaWatch.Probe'
$hash = (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant()
# Unix line ending, so that 'sha256sum -c' on the server can read the file.
[System.IO.File]::WriteAllText("$file.sha256", "$hash  CinemaWatch.Probe`n")

Get-Item $file | Select-Object FullName, @{ Name = 'SizeMB'; Expression = { [math]::Round($_.Length / 1MB, 1) } }
"SHA-256: $hash"
