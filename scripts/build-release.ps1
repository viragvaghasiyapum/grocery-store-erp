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

& $dotnet tool restore
if ($LASTEXITCODE -ne 0) { throw 'Tool restore failed.' }
& $dotnet publish (Join-Path $repository 'src\GroceryErp.App\GroceryErp.App.csproj') `
    -c $Configuration -r win-x64 --self-contained true -p:PublishReadyToRun=true `
    -p:Version=$Version -o $publish
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

& $dotnet tool run vpk pack --packId GroceryErp --packVersion $Version --packDir $publish `
    --mainExe GroceryErp.App.exe --runtime win-x64 --outputDir $releases
if ($LASTEXITCODE -ne 0) { throw 'Velopack packaging failed.' }
