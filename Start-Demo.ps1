param([switch]$PrepareOnly)

$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
# The demo store, and every temporary file DataGen, ScaleAudit and the app make, stay on the
# repository's drive under artifacts\temp (gitignored), not in the system temp folder on C:.
# The app accepts a test data root only inside its temp folder, so TMP and TEMP point there
# while this script runs and are put back afterwards.
$tempRoot = Join-Path $repo 'artifacts\temp'
New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null
$savedTmp = $env:TMP
$savedTemp = $env:TEMP
$env:TMP = $tempRoot
$env:TEMP = $tempRoot
$demoRoot = Join-Path $tempRoot 'grocery-erp-packaged-ui-demo-hare-krushna'
$database = Join-Path $demoRoot 'db\grocery.db'
$dotnet = Join-Path $repo '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = 'dotnet' }

try {
    if (-not (Test-Path -LiteralPath $database)) {
        & $dotnet run --project (Join-Path $repo 'tools\GroceryErp.DataGen\GroceryErp.DataGen.csproj') -c Release -- "--scale=0.002" "--output=$database"
        if ($LASTEXITCODE -ne 0) { throw 'Demo data generation failed.' }
    }

    & $dotnet run --project (Join-Path $repo 'tools\GroceryErp.ScaleAudit\GroceryErp.ScaleAudit.csproj') -c Release -- $demoRoot ui-setup
    if ($LASTEXITCODE -ne 0) { throw 'Demo login setup failed.' }

    & $dotnet build (Join-Path $repo 'src\GroceryErp.App\GroceryErp.App.csproj') -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Demo app build failed.' }

    Write-Host 'Demo login: owner / scale-audit-2026'
    Write-Host "Demo data: $demoRoot"
    if ($PrepareOnly) { return }

    $env:GROCERY_ERP_TEST_DATA_ROOT = $demoRoot
    & (Join-Path $repo 'src\GroceryErp.App\bin\Release\net10.0-windows\GroceryErp.App.exe')
}
finally {
    Remove-Item Env:GROCERY_ERP_TEST_DATA_ROOT -ErrorAction SilentlyContinue
    $env:TMP = $savedTmp
    $env:TEMP = $savedTemp
}
