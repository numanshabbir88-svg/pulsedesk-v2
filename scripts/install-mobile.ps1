# PulseDesk Mobile - one-cable install (admin PC se, phone par koi prompt/settings nahi)
# Run: powershell -ExecutionPolicy Bypass -File scripts\install-mobile.ps1 -Code ABC12345
#
# Kyun ye tareeqa: adb install Play Protect aur Samsung Auto Blocker dono ko bypass karta hai.
# Phone par sirf EK dafa (admin karta hai, employee nahi):
#   Settings > About phone > Build number (7 taps) > Developer options > USB debugging ON
param(
    [string]$Code = "",                 # invite code (dashboard > Invites). Khali ho to app
                                        # apna pichla download-invite claim kar lega.
    [string]$Server = ""                # optional: https://xxxx.ts.net/pulsedesk
)
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$apk  = Join-Path $root 'dist\server\agent\PulseDeskMobile.apk'
if (-not (Test-Path $apk)) { throw "APK not found: $apk  (pehle scripts\build-mobile.ps1 chalao)" }

$adb = @(
    (Join-Path $env:LOCALAPPDATA 'Android\Sdk\platform-tools\adb.exe'),
    'C:\platform-tools\adb.exe'
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $adb) {
    $cmd = Get-Command adb.exe -ErrorAction SilentlyContinue
    if ($cmd) { $adb = $cmd.Source }
}
if (-not $adb) { throw 'adb not found - Android platform-tools install karo ya PATH me daalo' }

$pkg = 'com.pulsedesk.mobile'
Write-Host "ADB : $adb" -ForegroundColor DarkGray
Write-Host "APK : $apk" -ForegroundColor DarkGray

& $adb start-server | Out-Null
$serial = (& $adb devices |
           Select-String -Pattern '^\S+\s+device$' |
           ForEach-Object { ($_.ToString() -split '\s+')[0] } |
           Select-Object -First 1)
if (-not $serial) { throw 'Koi phone nahi mila. USB lagao aur phone par "Allow USB debugging" tap karo.' }
Write-Host "Phone: $serial" -ForegroundColor Cyan

# -r : reinstall/update   -g : saari runtime permissions khud grant (notification prompt nahi)
# -d : downgrade allowed (purani build wapas karni ho to)
& $adb -s $serial install -r -g -d $apk
if ($LASTEXITCODE -ne 0) { throw "Install failed (adb exit $LASTEXITCODE)" }

# Samsung "sleeping apps" / Doze monitoring ko band na kare
& $adb -s $serial shell dumpsys deviceidle whitelist "+$pkg" | Out-Null
& $adb -s $serial shell cmd appops set $pkg RUN_IN_BACKGROUND allow | Out-Null
& $adb -s $serial shell cmd appops set $pkg RUN_ANY_IN_BACKGROUND allow | Out-Null

# App khol do: auto-enroll + screen-share prompt khud aa jayega
if ($Code -and $Code.Trim().Length -ge 4) {
    $c = $Code.Trim().ToUpperInvariant()
    Write-Host "Invite: $c" -ForegroundColor Cyan
    $startArgs = @('-n', "$pkg/.MainActivity", '-e', 'code', $c)
    if ($Server) { $startArgs += @('-e', 'server', $Server) }
    & $adb -s $serial shell am start @startArgs | Out-Null
} else {
    & $adb -s $serial shell monkey -p $pkg -c android.intent.category.LAUNCHER 1 | Out-Null
}

Write-Host "`nHo gaya. Phone par sirf 'Allow' (screen share) tap karna hai - baqi sab auto." -ForegroundColor Green
Write-Host "Agla phone: cable lagao aur yehi command dobara chalao." -ForegroundColor DarkGray
