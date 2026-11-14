# Installs a free public tunnel so agents can reach the admin server from any network.
# Run as Administrator: powershell -ExecutionPolicy Bypass -File scripts\install-tunnel.ps1 -Mode cloudflared
param(
    [ValidateSet('cloudflared','tailscale')]
    [string]$Mode = 'cloudflared'
)

$ErrorActionPreference = 'Stop'

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Host "Admin rights chahiye - UAC prompt aa raha hai..." -ForegroundColor Yellow
    Start-Process powershell -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Mode $Mode"
    exit
}

function Get-Winget { Get-Command winget -ErrorAction SilentlyContinue }

if ($Mode -eq 'cloudflared') {
    $dest = "$env:ProgramFiles\cloudflared\cloudflared.exe"
    if (Test-Path $dest) {
        Write-Host "cloudflared already installed: $dest" -ForegroundColor Green
    }
    else {
        Write-Host "Installing cloudflared..." -ForegroundColor Cyan
        $wg = Get-Winget
        if ($wg) {
            & winget install --id Cloudflare.cloudflared --accept-source-agreements --accept-package-agreements -e
        }
        if (-not (Test-Path $dest)) {
            # manual download fallback
            $url = 'https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe'
            New-Item -ItemType Directory -Force -Path (Split-Path $dest) | Out-Null
            Write-Host "Downloading cloudflared from GitHub..." -ForegroundColor Cyan
            Invoke-WebRequest -Uri $url -OutFile $dest -UseBasicParsing
        }
    }

    if (Test-Path $dest) {
        Write-Host ""
        Write-Host "cloudflared ready: $dest" -ForegroundColor Green
        Write-Host ""
        Write-Host "Quick tunnel URL nikalne ke liye (server chalu hone ke baad):" -ForegroundColor DarkGray
        Write-Host "  & '$dest' tunnel --url http://localhost:5080" -ForegroundColor DarkGray
        Write-Host ""
        Write-Host "Ya bas: powershell -ExecutionPolicy Bypass -File scripts\start-all.ps1 -Mode cloudflared" -ForegroundColor DarkGray
        Write-Host "URL dist\logs\tunnel.err.log mein likha hoga (https://....trycloudflare.com)" -ForegroundColor DarkGray
    }
    else {
        throw "cloudflared install nahi hua. Manually download karein: $url"
    }
}
elseif ($Mode -eq 'tailscale') {
    $ts = Get-Command tailscale -ErrorAction SilentlyContinue
    if (-not $ts) {
        $wg = Get-Winget
        if ($wg) {
            & winget install --id Tailscale.Tailscale --accept-source-agreements --accept-package-agreements -e
        }
        Start-Sleep -Seconds 5
        $ts = Get-Command tailscale -ErrorAction SilentlyContinue
    }
    if (-not $ts) { throw "Tailscale install nahi mila. https://tailscale.com/download se install karein." }

    Write-Host "Enabling Tailscale Funnel on port 5080..." -ForegroundColor Cyan
    & $ts.Source funnel --bg 5080
    Write-Host ""
    Write-Host "Public URL:" -ForegroundColor Green
    & $ts.Source funnel status
    Write-Host ""
    Write-Host "Note: Funnel ke liye Tailscale admin console mein HTTPS/Funnel enable karna parti hai." -ForegroundColor Yellow
}
