[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $HostReferenceDirectory,
    [Parameter(Mandatory)] [string] $UnrealRoot,
    [string] $Python = 'python.exe',
    [string] $OutputDirectory = (Join-Path $PSScriptRoot 'dist')
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'UAssetManager.ModelStudio.csproj'
$hostReferences = (Resolve-Path -LiteralPath $HostReferenceDirectory).Path
$projectXml = [xml](Get-Content -LiteralPath $project -Raw)
$version = [string]$projectXml.Project.PropertyGroup.Version
$assembly = 'UAssetManager.ModelStudio.dll'
$work = Join-Path $PSScriptRoot ('obj/release/' + [Guid]::NewGuid().ToString('N'))
$managed = Join-Path $work 'managed'
$native = Join-Path $work 'native'
$package = Join-Path $work 'package'
$fbx = Join-Path $package 'runtime/FbxBridge'
[void][IO.Directory]::CreateDirectory($fbx)

& dotnet build $project -c Release -o $managed "-p:HostReferenceDirectory=$hostReferences"
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

& $Python -X utf8 (Join-Path $PSScriptRoot 'FbxBridge/build.py') --ue-root $UnrealRoot --output $native
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Copy-Item -LiteralPath (Join-Path $managed $assembly) -Destination $package
Copy-Item -LiteralPath (Join-Path $native 'BnsOutfitFbx.exe') -Destination $fbx
$manifest = [ordered]@{
    schema = 1
    id = 'xyliaup.modelstudio'
    name = 'Model Studio'
    version = $version
    entryAssembly = $assembly
    minHostVersion = '1.0.2'
}
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $package 'plugin.json') -Encoding utf8

$output = [IO.Path]::GetFullPath($OutputDirectory)
[void][IO.Directory]::CreateDirectory($output)
$zip = Join-Path $output 'UAssetManager_ModelStudio-win-x64.zip'
$stagedZip = Join-Path $work ([IO.Path]::GetFileName($zip))
[IO.Compression.ZipFile]::CreateFromDirectory($package, $stagedZip)
[IO.File]::Move($stagedZip, $zip, $true)
Write-Output $zip
