param(
    [string]$DataRoot = (Join-Path $PSScriptRoot '..\artifacts\phase2-crash-check')
)

$ErrorActionPreference = 'Stop'
$artifacts = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\artifacts'))
$root = [IO.Path]::GetFullPath($DataRoot)
if ([IO.Path]::GetDirectoryName($root) -ne $artifacts -or
    -not [IO.Path]::GetFileName($root).StartsWith('phase2-crash-', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The crash audit only runs on a disposable artifacts/phase2-crash-* data root.'
}

$audit = Join-Path $PSScriptRoot 'GroceryErp.ScaleAudit\bin\Debug\net10.0-windows\GroceryErp.ScaleAudit.dll'
if (-not (Test-Path -LiteralPath $audit)) {
    throw 'Build tools/GroceryErp.ScaleAudit before running this audit.'
}

$boundaries = @(
    'marker-preparing', 'original-moved', 'marker-original-moved',
    'candidate-installed', 'marker-applied', 'marker-undoing',
    'candidate-discarded', 'original-restored'
)
foreach ($boundary in $boundaries) {
    $crashOutput = (& dotnet $audit $root restore-crash (Join-Path $root 'audit.json') $boundary 2>&1 | Out-String)
    $crashExit = $LASTEXITCODE
    if ($crashExit -eq 0 -or -not $crashOutput.Contains("KILL_AT $boundary")) {
        throw "Expected a process kill at $boundary; exit=$crashExit; output=$crashOutput"
    }
    $recoveryOutput = (& dotnet $audit $root restore-recover 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0 -or -not $recoveryOutput.Contains('OK restore-recover')) {
        throw "Cold recovery failed after $boundary`n$recoveryOutput"
    }
    Write-Output "PASS $boundary"
}

& dotnet $audit $root verify (Join-Path $root 'verify.json')
if ($LASTEXITCODE -ne 0) { throw 'Post-crash database verification failed.' }
