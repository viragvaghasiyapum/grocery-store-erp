param(
    [Parameter(Mandatory = $true)][string] $InstallRoot,
    [Parameter(Mandatory = $true)][string] $Package,
    [Parameter(Mandatory = $true)][string] $ExpectedVersion
)

$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$install = [IO.Path]::GetFullPath($InstallRoot)
$release = [IO.Path]::GetFullPath($Package)
foreach ($path in @($install, $release)) {
    if (-not $path.StartsWith($repository + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "The test path must be inside the repository: $path"
    }
}
$updater = Join-Path $install 'Update.exe'
$versionFile = Join-Path $install 'current\sq.version'
$database = Join-Path $repository 'artifacts\data\phase4-combined-local-moved-moved\db\grocery.db'
if (-not (Test-Path -LiteralPath $updater) -or -not (Test-Path -LiteralPath $release) -or
    -not (Test-Path -LiteralPath $database)) { throw 'The isolated updater, release, or dummy database is missing.' }

function Read-Version([string] $file) {
    $matched = [regex]::Match([IO.File]::ReadAllText($file), '<version>([^<]+)</version>')
    if (-not $matched.Success) { throw "No package version in $file" }
    return $matched.Groups[1].Value
}

$previous = Read-Version $versionFile
if ($previous -eq $ExpectedVersion) { throw 'The target release is already installed.' }
$beforeHash = (Get-FileHash -LiteralPath $database -Algorithm SHA256).Hash
$log = Join-Path $repository ('artifacts\phase5-update-interrupted-' + [guid]::NewGuid().ToString('N') + '.log')
$staging = Join-Path $install 'packages\VelopackTemp'
$priorStages = @()
if (Test-Path -LiteralPath $staging) {
    $priorStages = @(Get-ChildItem -LiteralPath $staging -Directory | ForEach-Object FullName)
}
$arguments = 'apply --norestart --silent --package "{0}" --rootDir "{1}" --packageDir "{2}" --log "{3}"' -f
    $release, $install, (Join-Path $install 'packages'), $log
$process = Start-Process -FilePath $updater -ArgumentList $arguments -WorkingDirectory $install `
    -WindowStyle Hidden -PassThru
$observedExtraction = $false
$deadline = [Diagnostics.Stopwatch]::StartNew()
while ($deadline.Elapsed -lt [TimeSpan]::FromSeconds(15)) {
    $process.Refresh()
    if ($process.HasExited) { break }
    if (Test-Path -LiteralPath $staging) {
        $observedExtraction = @(Get-ChildItem -LiteralPath $staging -Directory |
            Where-Object { $_.FullName -notin $priorStages }).Count -gt 0
        if ($observedExtraction) { break }
    }
    Start-Sleep -Milliseconds 10
}
if (-not $observedExtraction -or $process.HasExited) {
    if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
    throw 'The updater left the extraction stage before it could be interrupted.'
}
Stop-Process -Id $process.Id -Force
$process.WaitForExit()
$interruptionObservedMilliseconds = $deadline.Elapsed.TotalMilliseconds
$interruptedVersion = Read-Version $versionFile
$interruptedHash = (Get-FileHash -LiteralPath $database -Algorithm SHA256).Hash
if ($interruptedVersion -ne $previous -or $interruptedHash -ne $beforeHash -or
    -not (Test-Path -LiteralPath (Join-Path $install 'current\GroceryErp.App.exe'))) {
    throw 'The interrupted apply did not preserve the prior installation and dummy database.'
}

$retry = Start-Process -FilePath $updater -ArgumentList $arguments -WorkingDirectory $install `
    -WindowStyle Hidden -Wait -PassThru
if ($retry.ExitCode -ne 0) { throw "Updater retry failed with exit code $($retry.ExitCode)." }
$afterVersion = Read-Version $versionFile
$afterHash = (Get-FileHash -LiteralPath $database -Algorithm SHA256).Hash
if ($afterVersion -ne $ExpectedVersion -or $afterHash -ne $beforeHash) {
    throw 'The retry did not install the requested release or changed the dummy database.'
}

$rollbackPackage = Join-Path $repository "artifacts\Releases\GroceryErp-$previous-full.nupkg"
if (-not (Test-Path -LiteralPath $rollbackPackage)) { throw 'The prior full package is missing.' }
$rollbackArguments = 'apply --norestart --silent --package "{0}" --rootDir "{1}" --packageDir "{2}"' -f
    $rollbackPackage, $install, (Join-Path $install 'packages')
$rollback = Start-Process -FilePath $updater -ArgumentList $rollbackArguments -WorkingDirectory $install `
    -WindowStyle Hidden -Wait -PassThru
if ($rollback.ExitCode -ne 0 -or (Read-Version $versionFile) -ne $previous -or
    (Get-FileHash -LiteralPath $database -Algorithm SHA256).Hash -ne $beforeHash) {
    throw 'The previous full package did not restore the prior app version with unchanged data.'
}
$restore = Start-Process -FilePath $updater -ArgumentList $arguments -WorkingDirectory $install `
    -WindowStyle Hidden -Wait -PassThru
if ($restore.ExitCode -ne 0 -or (Read-Version $versionFile) -ne $ExpectedVersion -or
    (Get-FileHash -LiteralPath $database -Algorithm SHA256).Hash -ne $beforeHash) {
    throw 'Reapplying the target release after rollback failed.'
}

$result = [ordered]@{
    from = $previous
    interruptedVersion = $interruptedVersion
    to = $afterVersion
    killStage = 'package extraction before current-directory swap'
    rollbackVersion = $previous
    updaterProcessId = $process.Id
    databaseSha256Unchanged = $true
    interruptionObservedMilliseconds = $interruptionObservedMilliseconds
    totalTestMilliseconds = $deadline.Elapsed.TotalMilliseconds
    log = [IO.Path]::GetFileName($log)
}
$result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $repository 'artifacts\phase5-update-interruption.json')
$result | ConvertTo-Json
