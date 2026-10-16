param([switch]$PrepareOnly)

$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
$demoRoot = Join-Path ([System.IO.Path]::GetTempPath()) 'grocery-erp-packaged-ui-demo-hare-krushna'
$database = Join-Path $demoRoot 'db\grocery.db'
$dotnet = Join-Path $repo '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = 'dotnet' }

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
try {
    & (Join-Path $repo 'src\GroceryErp.App\bin\Release\net10.0-windows\GroceryErp.App.exe')
}
finally {
    Remove-Item Env:GROCERY_ERP_TEST_DATA_ROOT -ErrorAction SilentlyContinue
}
