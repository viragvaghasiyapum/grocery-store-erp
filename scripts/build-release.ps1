param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $Version,
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$publish = Join-Path $repository 'artifacts\publish'
$releases = Join-Path $repository 'artifacts\Releases'
$dotnet = Join-Path $repository '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = 'dotnet' }

# The package shows these notes once after sign-in, so they must describe this version.
foreach ($language in 'en', 'gu') {
    $notes = Join-Path $repository "src\GroceryErp.App\CHANGELOG.$language.md"
    $title = Get-Content -LiteralPath $notes -Encoding UTF8 |
        Where-Object { $_.Trim() -and -not $_.TrimStart().StartsWith('<!--') } | Select-Object -First 1
    if ($title -cne "Grocery ERP $Version") {
        throw "CHANGELOG.$language.md begins '$title', not 'Grocery ERP $Version'. Write the release notes for $Version first."
    }
}

& $dotnet tool restore
if ($LASTEXITCODE -ne 0) { throw 'Tool restore failed.' }
& $dotnet publish (Join-Path $repository 'src\GroceryErp.App\GroceryErp.App.csproj') `
    -c $Configuration -r win-x64 --self-contained true -p:PublishReadyToRun=true `
    -p:Version=$Version -o $publish
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

& $dotnet tool run vpk pack --packId GroceryErp --packVersion $Version --packDir $publish `
    --mainExe GroceryErp.App.exe --runtime win-x64 --outputDir $releases `
    --icon (Join-Path $repository 'logo\logo.ico')
if ($LASTEXITCODE -ne 0) { throw 'Velopack packaging failed.' }
