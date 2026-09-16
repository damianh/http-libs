# Runs pinned RFC 9111 fixtures using the shared cross-platform launcher.
# Example: .\run-conformance.ps1 -Framework net472 -FileSystem
param(
    [ValidateSet('net10.0', 'netstandard2.0', 'net472')]
    [string]$Framework = 'net10.0',
    [switch]$Update,
    [switch]$FileSystem,
    [string]$TestId,
    [int]$OriginPort = 0,
    [int]$ProxyPort = 0
)

$ErrorActionPreference = 'Stop'
$arguments = @((Join-Path $PSScriptRoot 'run-conformance.mjs'), '--framework', $Framework,
    '--origin-port', "$OriginPort", '--proxy-port', "$ProxyPort")
if ($Update) { $arguments += '--update' }
if ($FileSystem) { $arguments += '--file-system' }
if ($TestId) { $arguments += @('--test-id', $TestId) }
& node @arguments
exit $LASTEXITCODE
