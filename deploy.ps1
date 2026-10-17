# Builds and copies the mod into the game's BepInEx plugin folder.
#   .\deploy.ps1          the core only (PPUpscaler.Core.dll): works while the game runs, HotReload picks it up
#   .\deploy.ps1 -All     also the loader and the native plugin (the game must be closed)
param([switch]$All)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
[xml]$props = Get-Content (Join-Path $root 'managed\local.props')
$game = $props.Project.PropertyGroup.PPDir
$dest = Join-Path $game 'BepInEx\plugins\PPEnhancer'

dotnet build (Join-Path $root 'managed\PPUpscaler.csproj') -c Release --nologo -v q | Select-String -Pattern ' error |Error\(s\)'
if ($LASTEXITCODE -ne 0) { throw 'core build failed' }
if ($All) {
    if (Get-Process PhoenixPointWin64 -ErrorAction SilentlyContinue) { throw 'close the game first (the loader and native plugin are locked while it runs)' }
    dotnet build (Join-Path $root 'loader\PPUpscalerLoader.csproj') -c Release --nologo -v q | Select-String -Pattern ' error |Error\(s\)'
    if ($LASTEXITCODE -ne 0) { throw 'loader build failed' }
    cmd /c (Join-Path $root 'native\build.bat') | Select-String -Pattern 'BUILD|error'
    if ($LASTEXITCODE -ne 0) { throw 'native build failed' }
    Copy-Item (Join-Path $root 'loader\bin\Release\net472\PPUpscaler.dll') $dest -Force
    Copy-Item (Join-Path $root 'native\bin\PPUpscalerNative.dll') $dest -Force
}
# write to a temporary name first, so the loader never sees a half-copied file
$tmp = Join-Path $dest 'PPUpscaler.Core.dll.tmp'
Copy-Item (Join-Path $root 'managed\bin\Release\net472\PPUpscaler.Core.dll') $tmp -Force
Move-Item $tmp (Join-Path $dest 'PPUpscaler.Core.dll') -Force
"deployed to $dest"
