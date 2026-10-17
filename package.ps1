# Assembles a clean, shippable PPUpscaler folder and zip in .\dist (BepInEx\plugins\PPUpscaler\...), extractable into the game folder.
# Includes NVIDIA's DLSS runtime (nvngx_dlss.dll) and PPUpscalerNative.dll (which contains NVIDIA NGX code), with NVIDIA's license text; the script refuses to
# build without that text. It also fails when a build fails, when the native plugin is older than its source, and when the output contains a string that must
# not be published (the build machine's user name or paths, plus anything in the git-ignored release-forbidden.txt, one string per line).
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$version = "0.9.0"
$name = "PPEnhancer"
$dist = Join-Path $root "dist"
$stage = Join-Path $dist "stage"
$out = Join-Path $stage "BepInEx\plugins\$name"
$zip = Join-Path $dist "$name-$version.zip"
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Force $out | Out-Null

# 1. Managed core and loader, Release (no debug symbols, so no build paths inside).
foreach ($p in "managed\PPUpscaler.csproj", "loader\PPUpscalerLoader.csproj") {
    dotnet build (Join-Path $root $p) -c Release | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed: $p" }
}
$core = Join-Path $root "managed\bin\Release\net472\PPUpscaler.Core.dll"
$loader = Join-Path $root "loader\bin\Release\net472\PPUpscaler.dll"
foreach ($f in $core, $loader) { if (-not (Test-Path $f)) { throw "$f was not built." } }
if ((Select-String -Path (Join-Path $root "loader\Loader.cs") -Pattern "`"$version`"" -Quiet) -eq $false) { throw "Loader.cs version does not match $version." }

# 2. Native plugin must exist and be newer than its source.
$native = Join-Path $root "native\bin\PPUpscalerNative.dll"
if (-not (Test-Path $native)) { throw "Native plugin not built. Run native\build.bat first." }
if ((Get-Item $native).LastWriteTime -lt (Get-Item (Join-Path $root "native\PPUpscaler.cpp")).LastWriteTime) { throw "PPUpscalerNative.dll is older than PPUpscaler.cpp. Run native\build.bat." }

Copy-Item $core, $loader, $native $out
foreach ($f in "README.md", "CHANGELOG.md", "LICENSE", "THIRD-PARTY-NOTICES.md") { Copy-Item (Join-Path $root $f) $out }

# 3. NVIDIA files: the runtime and the license text that must travel with them.
$sdk = Join-Path $root "..\ThirdParty\DLSS"
$nvidiaLicense = Join-Path $sdk "LICENSE.txt"
$runtime = Join-Path $sdk "lib\Windows_x86_64\rel\nvngx_dlss.dll"
if (-not (Test-Path $nvidiaLicense)) { throw "NVIDIA SDK license not found at $nvidiaLicense." }
if (-not (Test-Path $runtime)) { throw "DLSS runtime not found at $runtime." }
New-Item -ItemType Directory -Force (Join-Path $out "licenses") | Out-Null
Copy-Item $nvidiaLicense (Join-Path $out "licenses\NVIDIA-RTX-SDKs-LICENSE.txt")
Copy-Item $runtime $out

# 4. Nothing that must not be published may be inside any file (text or binary, ASCII or UTF-16).
$forbidden = @($env:USERNAME, $env:USERPROFILE, "gmail.com")
$extra = Join-Path $root "release-forbidden.txt"
if (Test-Path $extra) { $forbidden += Get-Content $extra | Where-Object { $_.Trim().Length -gt 0 } }
$forbidden = $forbidden | Where-Object { $_ -and $_.Length -ge 4 } | Select-Object -Unique
$bad = @()
foreach ($file in Get-ChildItem $stage -Recurse -File) {
    $bytes = [System.IO.File]::ReadAllBytes($file.FullName)
    $ascii = [System.Text.Encoding]::GetEncoding(28591).GetString($bytes)
    $utf16 = [System.Text.Encoding]::Unicode.GetString($bytes)
    $utf16b = if ($bytes.Length -gt 1) { [System.Text.Encoding]::Unicode.GetString($bytes, 1, $bytes.Length - 1) } else { "" }
    foreach ($s in $forbidden) {
        if ($ascii.IndexOf($s, [StringComparison]::OrdinalIgnoreCase) -ge 0 -or $utf16.IndexOf($s, [StringComparison]::OrdinalIgnoreCase) -ge 0 -or $utf16b.IndexOf($s, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $bad += "$($file.Name) contains a forbidden string (entry $([array]::IndexOf($forbidden, $s) + 1) of the list)"
        }
    }
}
if ($bad.Count -gt 0) { $bad | ForEach-Object { Write-Host $_ -ForegroundColor Red }; throw "Release scan failed: see above." }

# 5. Zip (paths start at BepInEx\ so it extracts into the game folder).
Compress-Archive -Path (Join-Path $stage "BepInEx") -DestinationPath $zip -CompressionLevel Optimal
Get-ChildItem $out -Recurse -File | Select-Object @{n="Path";e={$_.FullName.Substring($out.Length + 1)}}, Length | Format-Table -AutoSize
Write-Host "Package: $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB). It contains NVIDIA files (nvngx_dlss.dll, PPUpscalerNative.dll); NVIDIA's license text is in licenses\."
