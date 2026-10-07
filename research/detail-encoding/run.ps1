#!/usr/bin/env pwsh
# Generates a synthetic column chunk table in four encodings and times how long each takes to read.
param(
    [int[]] $Rows = @(1000000, 5000000)
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$data = Join-Path $PSScriptRoot 'data'
dotnet build -c Release -v q --nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

foreach ($count in $Rows) {
    dotnet bin/Release/net10.0/DetailEncoding.dll $count $data
    node parse.mjs $data $count
}
