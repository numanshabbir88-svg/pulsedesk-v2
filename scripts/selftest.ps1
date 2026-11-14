# PulseDesk local end-to-end self test (no employee PC needed).
# Starts the published server on a temp port, exercises the full API flow, then stops it.
# Run: powershell -ExecutionPolicy Bypass -File scripts\selftest.ps1
param(
    [int]$Port = 5099,
    [string]$Password = 'ChangeMe123!'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$serverExe = Join-Path $root 'dist\server\PulseDesk.Server.exe'
if (-not (Test-Path $serverExe)) { throw "Server not published. Run scripts\publish.ps1 first." }

$base = "http://localhost:$Port"
$logDir = Join-Path $root 'dist\logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

# The published server reads appsettings.json from its own folder; override with env vars.
$env:Urls = "http://localhost:$Port"
$env:PublicBaseUrl = $base
$env:AdminPassword = $Password

function Step($n, $msg) { Write-Host "[$n] $msg" -ForegroundColor Cyan }
function Ok($msg) { Write-Host "    OK  $msg" -ForegroundColor Green }
function Fail($msg) { Write-Host "    FAIL $msg" -ForegroundColor Red; throw $msg }

Step 1 "Starting server on $Port ..."
$proc = Start-Process -FilePath $serverExe -WorkingDirectory (Split-Path $serverExe) `
    -WindowStyle Hidden -PassThru `
    -RedirectStandardOutput "$logDir\test.out.log" -RedirectStandardError "$logDir\test.err.log"

try {
    $up = $false
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 500
        try { $r = Invoke-WebRequest "$base/index.html" -UseBasicParsing -TimeoutSec 3; if ($r.StatusCode -eq 200) { $up = $true; break } } catch { }
    }
    if (-not $up) { Fail "server did not start (see dist\logs\test.err.log)" }
    Ok "dashboard served"

    Step 2 "Admin login"
    $login = Invoke-RestMethod "$base/api/admin/login" -Method Post -ContentType 'application/json' `
        -Body (@{ password = $Password } | ConvertTo-Json)
    if (-not $login.ok) { Fail "login failed" }
    $h = @{ 'X-Admin-Token' = $login.token }
    Ok "token received"

    Step 3 "Create invite"
    $inv = Invoke-RestMethod "$base/api/admin/invite" -Method Post -Headers $h -ContentType 'application/json' `
        -Body (@{ email = 'test@company.com'; note = 'selftest' } | ConvertTo-Json)
    if (-not $inv.code) { Fail "no invite code" }
    Ok "invite $($inv.code) -> $($inv.link)"

    Step 4 "Join page"
    $join = Invoke-WebRequest "$base/join/$($inv.code)" -UseBasicParsing
    if ($join.Content -notmatch 'Download') { Fail "join page missing download button" }
    Ok "join page renders ($([math]::Round($join.RawContentLength/1kb,1)) KB)"

    Step 5 "Download filtered agent exe"
    $dlPath = Join-Path $env:TEMP "PulseDeskSetup_test.exe"
    Invoke-WebRequest "$base/download/$($inv.code)" -OutFile $dlPath -UseBasicParsing
    $len = (Get-Item $dlPath).Length
    $tail = [System.IO.File]::ReadAllBytes($dlPath)[-4096..-1]
    $tailText = [System.Text.Encoding]::UTF8.GetString($tail)
    if ($tailText -notmatch 'PLSDCONF:') { Fail "embedded config marker missing from download" }
    Ok "download $([math]::Round($len/1MB,1)) MB with PLSDCONF marker"

    Step 6 "Enroll device"
    $enr = Invoke-RestMethod "$base/api/enroll" -Method Post -ContentType 'application/json' -Body (@{
            code = $inv.code; machine = 'SELFTEST-PC'; user = 'tester'; os = 'Windows 11'
            agentVersion = '1.0.0'; consentAt = (Get-Date).ToUniversalTime().ToString('o')
        } | ConvertTo-Json)
    if (-not $enr.ok) { Fail "enroll failed" }
    $devH = @{ 'X-Device-Token' = $enr.deviceToken }
    Ok "device $($enr.deviceId.Substring(0,8)) enrolled"

    Step 7 "Activity + heartbeat"
    $samples = @(
        @{ ts = (Get-Date).ToUniversalTime().ToString('o'); process = 'Code.exe'; title = 'Program.cs - Visual Studio Code'; idle = 3; active = $true }
        @{ ts = (Get-Date).ToUniversalTime().ToString('o'); process = 'chrome.exe'; title = 'Dashboard'; idle = 12; active = $true }
    )
    Invoke-RestMethod "$base/api/activity" -Method Post -Headers $devH -ContentType 'application/json' `
        -Body (@{ samples = $samples } | ConvertTo-Json -Depth 5) | Out-Null
    $hb = Invoke-RestMethod "$base/api/hb" -Method Post -Headers $devH -ContentType 'application/json' -Body (@{ paused = $false } | ConvertTo-Json)
    if (-not $hb.ok) { Fail "heartbeat failed" }
    Ok "activity posted, heartbeat ok (interval=$($hb.screenshotIntervalSec)s)"

    Step 8 "Screenshot upload"
    Add-Type -AssemblyName System.Drawing
    $bmp = New-Object System.Drawing.Bitmap 320, 180
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::FromArgb(13, 59, 57))
    $g.Dispose()
    $shotPath = Join-Path $env:TEMP 'pds_shot_test.jpg'
    $bmp.Save($shotPath, [System.Drawing.Imaging.ImageFormat]::Jpeg); $bmp.Dispose()

    # HttpClient multipart (works on Windows PowerShell 5.1, which has no -Form)
    Add-Type -AssemblyName System.Net.Http
    $cl = New-Object System.Net.Http.HttpClient
    $cl.DefaultRequestHeaders.Add('X-Device-Token', $enr.deviceToken)
    $mp = New-Object System.Net.Http.MultipartFormDataContent
    $fb = [System.IO.File]::ReadAllBytes($shotPath)
    $fc = New-Object System.Net.Http.ByteArrayContent (, $fb)
    $fc.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse('image/jpeg')
    $mp.Add($fc, 'file', 'shot.jpg')
    $sr = $cl.PostAsync("$base/api/shot", $mp).GetAwaiter().GetResult()
    $srBody = $sr.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    $cl.Dispose(); $mp.Dispose()
    if (-not $sr.IsSuccessStatusCode) { Fail "screenshot upload failed: $($sr.StatusCode) $srBody" }
    Ok "screenshot uploaded"

    Step 9 "Admin reads data back"
    $devices = Invoke-RestMethod "$base/api/admin/devices" -Headers $h
    if (-not ($devices | Where-Object { $_.id -eq $enr.deviceId })) { Fail "device not in admin list" }
    $act = Invoke-RestMethod "$base/api/admin/activity?deviceId=$($enr.deviceId)&minutes=60" -Headers $h
    if ($act.Count -lt 2) { Fail "activity rows missing" }
    $sum = Invoke-RestMethod "$base/api/admin/summary?deviceId=$($enr.deviceId)&minutes=60" -Headers $h
    $shots = Invoke-RestMethod "$base/api/admin/shots?deviceId=$($enr.deviceId)&limit=10" -Headers $h
    if ($shots.Count -lt 1) { Fail "screenshot not listed" }
    $img = Invoke-WebRequest "$base$($shots[0].url)" -Headers $h -UseBasicParsing
    if ($img.RawContentLength -lt 100) { Fail "screenshot not downloadable" }
    $alerts = Invoke-RestMethod "$base/api/admin/alerts?limit=20" -Headers $h
    Ok "devices=$($devices.Count) activity=$($act.Count) apps=$($sum.Count) shots=$($shots.Count) alerts=$($alerts.Count)"

    Step 10 "Remote command (pause)"
    Invoke-RestMethod "$base/api/admin/command" -Method Post -Headers $h -ContentType 'application/json' `
        -Body (@{ deviceId = $enr.deviceId; command = 'pause' } | ConvertTo-Json) | Out-Null
    Start-Sleep -Milliseconds 400
    $devices2 = Invoke-RestMethod "$base/api/admin/devices" -Headers $h
    $d2 = $devices2 | Where-Object { $_.id -eq $enr.deviceId }
    if (-not $d2.paused) { Fail "pause did not apply" }
    Ok "device paused by admin"

    Step 11 "Unauthorized access blocked"
    try { Invoke-WebRequest "$base/api/admin/devices" -UseBasicParsing -ErrorAction Stop | Out-Null; Fail "unauthorized call succeeded" }
    catch { if ($_.Exception.Response.StatusCode.value__ -ne 401) { Fail "expected 401" } }
    Ok "401 without admin token"

    Write-Host ""
    Write-Host "ALL CHECKS PASSED" -ForegroundColor Green
}
finally {
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
    Remove-Item $env:Urls, $env:PublicBaseUrl, $env:AdminPassword -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $env:TEMP 'PulseDeskSetup_test.exe'), (Join-Path $env:TEMP 'pds_shot_test.jpg') -ErrorAction SilentlyContinue
    Write-Host "server stopped" -ForegroundColor DarkGray
}
