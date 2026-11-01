# Packs build\Mystic into a zip and builds the web installer that downloads it.
#   .\installer\release.ps1 -BaseUrl https://github.com/<user>/<repo>/releases/download/v0.5.0
# Then upload build\release\Mystic-<version>.zip to that location (e.g. as a GitHub release asset) and share
# build\installer\MysticWebSetup-<version>.exe.
param(
    [Parameter(Mandatory = $true)][string]$BaseUrl,
    [string]$Version = "0.5.0"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$app = Join-Path $root "build\Mystic"
if (-not (Test-Path (Join-Path $app "Mystic.exe"))) { throw "Build the app first (Unity: Mystic > Build Windows App)." }
if (-not (Test-Path (Join-Path $app "Tracker\tracker.exe"))) { throw "build\Mystic\Tracker\tracker.exe is missing." }

# Unity's Burst debug symbols are for crash debugging only ("DoNotShip").
Get-ChildItem $app -Directory -Filter "*_DoNotShip" | Remove-Item -Recurse -Force

# Third-party licenses: everything shipped in the app and the packaged tracker.
$lic = Join-Path $app "Licenses"
if (Test-Path $lic) { Remove-Item $lic -Recurse -Force }
New-Item -ItemType Directory $lic | Out-Null
$pkgs = Join-Path $root "app\Library\PackageCache"
$sources = [ordered]@{
    "UniVRM-and-MToon (MIT).txt"     = Join-Path $root "app\Assets\Mystic\Shaders\MysticToon\LICENSE-UniVRM.txt"
    "KlakSpout (Unlicense).txt"      = Join-Path $pkgs "jp.keijiro.klak.spout\LICENSE"
    "Newtonsoft.Json (MIT).txt"      = Join-Path $pkgs "com.unity.nuget.newtonsoft-json\LICENSE.md"
    "Python (PSF).txt"               = Join-Path ((Get-Content (Join-Path $root "tracker\.venv\pyvenv.cfg") | Where-Object { $_ -match '^home\s*=' }) -replace '^home\s*=\s*', '') "LICENSE.txt"
}
foreach ($name in $sources.Keys) {
    if (-not (Test-Path $sources[$name])) { throw "License file missing: $($sources[$name])" }
    Copy-Item $sources[$name] (Join-Path $lic $name)
}
# Python packages in the tracker (build tools such as pip and PyInstaller itself are not shipped).
$buildOnly = "pip", "setuptools", "pyinstaller", "pyinstaller_hooks_contrib", "altgraph", "pefile", "pywin32_ctypes"
Get-ChildItem (Join-Path $root "tracker\.venv\Lib\site-packages") -Directory -Filter "*.dist-info" | ForEach-Object {
    $pkg = $_.Name -replace '-[^-]+\.dist-info$', ''
    if ($buildOnly -contains $pkg.ToLowerInvariant()) { return }
    Get-ChildItem $_.FullName -Recurse -File | Where-Object { $_.Name -match '^(LICEN[CS]E|COPYING|NOTICE)' } | ForEach-Object {
        Copy-Item $_.FullName (Join-Path $lic "$pkg - $($_.Name -replace '\.(txt|md)$', '').txt")
    }
}
@"
Mystic includes third-party software. Each component's license is in this folder:
Unity runtime (Unity Technologies), UniVRM/UniGLTF/MToon (VRM Consortium, MIT), KlakSpout (Keijiro Takahashi, Unlicense),
Newtonsoft.Json (James Newton-King, MIT), and in Tracker\: Python (PSF), MediaPipe (Google, Apache 2.0),
OpenCV (Apache 2.0; its bundled components are listed in its LICENSE-3RD-PARTY file), NumPy, matplotlib, Pillow and
their dependencies. The Python license also covers OpenSSL, libffi, Tcl/Tk and the other libraries bundled with Python.
"@ | Set-Content (Join-Path $lic "README.txt") -Encoding utf8
"licenses: $((Get-ChildItem $lic).Count) files"

$outDir = Join-Path $root "build\release"
New-Item -ItemType Directory -Force $outDir | Out-Null
$zip = Join-Path $outDir "Mystic-$Version.zip"
if (Test-Path $zip) { Remove-Item $zip }

# tar (bsdtar, built into Windows 10+) writes zip much faster than Compress-Archive.
Push-Location $app
try { & "$env:SystemRoot\System32\tar.exe" -a -c -f $zip * ; if ($LASTEXITCODE -ne 0) { throw "tar failed" } }
finally { Pop-Location }

$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
$url = "$($BaseUrl.TrimEnd('/'))/Mystic-$Version.zip"
"zip:    $zip ($([math]::Round((Get-Item $zip).Length / 1MB)) MB)"
"sha256: $hash"
"url:    $url"

$iscc = Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"
& $iscc "/DAppVersion=$Version" "/DPayloadUrl=$url" "/DPayloadSha256=$hash" (Join-Path $PSScriptRoot "MysticWeb.iss")
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed" }
"installer: $(Join-Path $root "build\installer\MysticWebSetup-$Version.exe")"
"Upload the zip to $url before sharing the installer."
