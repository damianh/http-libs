# Runs the forward-proxy HTTP Garden fixtures using the shared cross-platform launcher.
# Example: .\run-conformance.ps1 [-Update] [-TestId <fixture>] [-Serve]
param(
    [switch]$Update,
    [string]$TestId,
    [switch]$Serve,
    [string]$Python,
    [int]$OriginPort = 0,
    [int]$ProxyPort = 0
)

$ErrorActionPreference = 'Stop'
$arguments = @((Join-Path $PSScriptRoot 'run-conformance.mjs'),
    '--origin-port', "$OriginPort", '--proxy-port', "$ProxyPort")
if ($Update) { $arguments += '--update' }
if ($TestId) { $arguments += @('--test-id', $TestId) }
if ($Serve) { $arguments += '--serve' }
if ($Python) { $arguments += @('--python', $Python) }
& node @arguments
exit $LASTEXITCODE
