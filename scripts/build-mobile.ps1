# Build the Android agent APK and publish it into dist\server\agent\
# Run:  powershell -ExecutionPolicy Bypass -File scripts\build-mobile.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root 'src\PulseDesk.Mobile'

# ---- JDK 17 (Gradle 8.7 cannot run on the JDK bundled with Android Studio 25) ----
$javaHome = $null
foreach ($candidate in @('msjdk17', 'jdk17ok', 'jdk17')) {
    $jdkRoot = Join-Path $env:LOCALAPPDATA $candidate
    if (-not (Test-Path $jdkRoot)) { continue }
    $javaHome = (Get-ChildItem $jdkRoot -Directory |
                 Where-Object { Test-Path (Join-Path $_.FullName 'bin\java.exe') } |
                 Select-Object -First 1).FullName
    if ($javaHome) { break }
}
if (-not $javaHome) { throw "JDK 17 not found in %LOCALAPPDATA% - download Microsoft OpenJDK 17 first" }
$env:JAVA_HOME = $javaHome
$env:PATH = "$javaHome\bin;$env:PATH"

# ---- Android SDK ----
$sdk = Join-Path $env:LOCALAPPDATA 'Android\Sdk'
if (-not (Test-Path $sdk)) { throw "Android SDK not found at $sdk" }
$env:ANDROID_HOME = $sdk
$env:ANDROID_SDK_ROOT = $sdk
Set-Content (Join-Path $proj 'local.properties') ("sdk.dir=" + $sdk.Replace('\', '\\')) -Encoding ASCII

# ---- Gradle 8.7 (already unpacked in the wrapper cache) ----
$gradle = Get-ChildItem (Join-Path $env:USERPROFILE '.gradle\wrapper\dists') -Recurse -Filter 'gradle.bat' -ErrorAction SilentlyContinue |
          Select-Object -First 1
if (-not $gradle) { throw 'Gradle 8.7 not found in the wrapper cache' }

Write-Host "JDK    : $javaHome" -ForegroundColor DarkGray
Write-Host "SDK    : $sdk" -ForegroundColor DarkGray
Write-Host "Gradle : $($gradle.FullName)" -ForegroundColor DarkGray

# ---- release signing: pass the PULSEDESK_* keys explicitly ----
# gradle.properties is usually picked up automatically, but passing them on the command line
# guarantees the APK is signed with the release key even if property resolution misbehaves.
$propsFile = Join-Path $proj 'gradle.properties'
$signArgs = @()
if (Test-Path $propsFile) {
    foreach ($line in Get-Content $propsFile) {
        if ($line -match '^\s*(PULSEDESK_[A-Z_]+)\s*=\s*(.+?)\s*$') {
            $signArgs += "-P$($Matches[1])=$($Matches[2])"
        }
    }
}
$keystore = Join-Path $proj 'keystore\pulsedesk-release.jks'
if ($signArgs.Count -gt 0 -and (Test-Path $keystore)) {
    Write-Host "Signing: RELEASE key (keystore\pulsedesk-release.jks)" -ForegroundColor Green
} else {
    Write-Host "Signing: DEBUG key (release keystore / gradle.properties missing)" -ForegroundColor Yellow
}

& $gradle.FullName -p $proj assembleRelease --no-daemon @signArgs
if ($LASTEXITCODE -ne 0) { throw "gradle build failed ($LASTEXITCODE)" }

$apk = Get-ChildItem (Join-Path $proj 'app\build\outputs\apk\release') -Filter '*.apk' | Select-Object -First 1
if (-not $apk) { throw 'APK not produced' }

$agentDir = Join-Path $root 'dist\server\agent'
New-Item -ItemType Directory -Force -Path $agentDir | Out-Null
Copy-Item $apk.FullName (Join-Path $agentDir 'PulseDeskMobile.apk') -Force
Copy-Item $apk.FullName (Join-Path $root 'dist\PulseDeskMobile.apk') -Force

$size = [math]::Round((Get-Item $apk.FullName).Length / 1MB, 2)
Write-Host "`nAPK: dist\server\agent\PulseDeskMobile.apk ($size MB)" -ForegroundColor Green
Write-Host "URL: <PublicBaseUrl>/mobile/app.apk" -ForegroundColor Green
