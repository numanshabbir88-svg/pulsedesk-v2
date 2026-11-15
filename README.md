# PulseDesk v.2

Consent-based employee monitoring (Net Monitor for Employees Pro ka free alternative).
Admin apne laptop par console chalata hai, employee ko email invite link jata hai, employee
link kholta hai -> setup download -> install -> bas. Koi manual setup, koi sign-in nahi.

---

## Kaise kaam karta hai

```
Employee PC (agent)  --outbound HTTPS/WSS-->  Admin laptop (server)  <--browser--  Admin console
        ^                                            ^
        |                                     Cloudflare Tunnel / Tailscale Funnel
        +-- kisi bhi network se connect (office, ghar, mobile hotspot)
```

- Agent **outbound** connect karta hai, is liye employee kisi bhi Wi-Fi par ho - chalega.
- Admin laptop par server + tunnel auto-start hota hai (Scheduled Task + watchdog).
- Data admin ke laptop par SQLite mein rehta hai.

---

## Requirements

| Cheez | Version |
|---|---|
| OS (admin laptop + employee PC) | Windows 10 / 11 (x64) |
| .NET SDK (sirf build ke liye) | 8.0+ |
| Admin laptop par | admin rights (watchdog task SYSTEM ke under chalta hai) |
| Public URL | Tailscale Funnel (recommended) ya Cloudflare quick tunnel |
| Employee PC par | admin rights (install ke waqt UAC), koi .NET install ki zaroorat nahi (self-contained) |
| Android phone (beta) | Android 8+ (APK), iPhone/iPad support nahi |

Repo mein sirf source + scripts hain — `bin/`, `obj/`, `dist/` (binaries, SQLite DB, screenshots, admin password) `.gitignore` se bahar rakhe gaye hain.

---

## Features

| Feature | Status |
|---|---|
| Remote mouse + keyboard control (Windows agent) | ✅ |
| Live view: full screen, minimize, quality selector | ✅ |
| Devices list, online/offline, last seen | ✅ |
| Live screen view (WebSocket JPEG stream) | ✅ |
| Screen recording / periodic screenshots | ✅ |
| App usage + window titles (activity) | ✅ |
| Idle / active time | ✅ |
| Pause / resume monitoring remotely | ✅ |
| Take screenshot now | ✅ |
| Email invite + install link | ✅ |
| Consent screen before install | ✅ |
| Visible app/uninstall entry + auto-start (user session) | ✅ |
| Raw keystroke logging | ❌ (privacy by design) |
| Android agent (beta): screen shots, live view, app activity, pause/resume | ✅ |

> Android par monitoring hamesha visible hoti hai (permanent notification), aur user notification se stop kar sakta hai — ye jaan boojh kar consent-based design hai.

> **Har network / reboot par dobara invite nahi:** agent ka enrollment token device par save hota hai, is liye internet badalne, Wi-Fi reconnect ya device restart par wahi device khud wapas connect ho jata hai. Sirf Android phone par reboot ke baad ek tap (screen-share allow) zaroori hai — Android ka rule hai.

---

## Folder layout

```
PulseDesk/
  src/
    PulseDesk.Server/     ASP.NET Core (minimal API) + dashboard (wwwroot/index.html)
    PulseDesk.Agent/      Windows agent (user session: screen + activity collector)
    PulseDesk.Mobile/     Android agent (Java, MediaProjection + foreground service)
    docs/ADMIN-GUIDE.md   admin setup + per-phone activation (invite link / adb / MDM)
  scripts/
    publish.ps1           build + publish into dist\ (live admin config preserve karta hai)
    build-mobile.ps1      Android APK build -> dist\server\agent\PulseDeskMobile.apk
    install-mobile.ps1    USB se ek phone par APK install + invite code (Play Protect prompt nahi)
    start-all.ps1         server + tunnel start (tunnel URL auto-detect)
    set-password.ps1      admin password set + server restart
    deploy-server-elevated.ps1  staged server update ko elevated deploy karta hai
    watchdog.ps1          keeps server/tunnel alive (auto-recover)
    install-autostart.ps1 registers scheduled task at boot
    install-tunnel.ps1    installs cloudflared / tailscale
    show-url.ps1          current public URL + console kholta hai
    selftest.ps1          poora API flow test (bina employee PC)
  dist/                   (generated) publish output
```

---

## Pehli baar setup (admin laptop)

> PowerShell hamesha `-ExecutionPolicy Bypass` ke sath chalayein.

### 1. Build + publish

```powershell
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1
```

Output:
- `dist\server\PulseDesk.Server.exe`  (admin console + API)
- `dist\agent\PulseDesk.Agent.exe`    (agent setup file)
- `dist\server\agent\PulseDesk.Agent.exe` (server isi ko download ke liye use karta hai)

### 2. Tunnel install (free public URL)

```powershell
# Tailscale Funnel (stable URL, recommended - is network par Cloudflare block hai)
powershell -ExecutionPolicy Bypass -File scripts\install-tunnel.ps1 -Mode tailscale

# ya Cloudflare quick tunnel (sab se asaan, free, koi account nahi)
powershell -ExecutionPolicy Bypass -File scripts\install-tunnel.ps1 -Mode cloudflared
```

### 3. Config set karein

`dist\server\appsettings.json`:

```json
{
  "Urls": "http://0.0.0.0:5080",
  "AdminPassword": "ChangeMe123!",
  "PublicBaseUrl": "http://localhost:5080",
  "DbPath": "data/pulsedesk.db",
  "AgentBundlePath": "agent/PulseDesk.Agent.exe",
  "ScreenshotDir": "data/shots",
  "RetentionDaysActivity": 30,
  "RetentionDaysShots": 7,
  "SmtpHost": "",
  "SmtpPort": 587,
  "SmtpUser": "",
  "SmtpPass": "",
  "SmtpFrom": ""
}
```

- `AdminPassword` **zaroor badlein** (default `ChangeMe123!`). Sab se asaan tareeqa:

  ```powershell
  powershell -ExecutionPolicy Bypass -File scripts\set-password.ps1
  ```

  Ye naya password `dist\ADMIN_PASSWORD.txt` mein save karta hai aur server restart karta hai.
  (`publish.ps1` aap ka password/URL/SMTP config overwrite nahi karta.)
- `PublicBaseUrl` wo URL hai jo tunnel deta hai. Cloudflare quick tunnel har restart par
  badal jata hai — is liye stable URL ke liye Tailscale Funnel ya Cloudflare named tunnel behtar hai.
- `SmtpHost` khali ho to invite email nahi jata, magar link console mein dikhta hai (copy kar ke
  WhatsApp/email se bhej dein). Gmail ke liye App Password use karein.

### 4. Auto-start install karein

```powershell
powershell -ExecutionPolicy Bypass -File scripts\install-autostart.ps1 -Mode tailscale
```

Ye ek Scheduled Task banata hai (`PulseDesk Watchdog`) jo:
- laptop boot par start hota hai,
- Wi-Fi reconnect / crash par server + tunnel dobara uthata hai,
- har 30 second health check karta hai.

### 5. Console kholen

```
http://localhost:5080
```

Admin password se login -> **Invites** tab -> employee email daalein -> **Send invite**.

Public URL: `scripts\show-url.ps1` (ya `dist\server\appsettings.json` ka `PublicBaseUrl`).

### 6. Update / republish (jab server chal raha ho)

```powershell
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1
# server files dist\server_new mein stage hote hain (port 5080 locked hota hai)
# phir elevated mein:
powershell -ExecutionPolicy Bypass -File scripts\deploy-server-elevated.ps1
```

Deploy script watchdog + server ko rok kar files swap karta hai, phir watchdog restart karta hai.
Live `appsettings.json` (password/URL/SMTP) aur `data\` folder safe rehte hain.

---

## Employee side (jo employee ko karna hai)

1. Email mein link kholta hai (`https://<tunnel>/join/<code>`).
2. **Download & Install** dabata hai — `PulseDeskSetup.exe` Downloads mein save hoti hai.
3. File run karta hai -> Windows "Yes" (admin) -> **consent screen** par OK.
4. Agent background mein chalu ho jata hai, device console mein online aa jata hai.

Employee ko **koi setting, koi login, koi agent config** nahi karni.

> **Zaroori note:** agent Windows service ke bajaye **user session** mein chalta hai.
> Windows service (session 0) se screen capture black/khali aata hai — is liye agent
> `HKCU\...\Run` se logon par start hota hai aur background process ki tarah chalta hai
> (koi window nahi). Uninstall entry Windows Settings -> Apps mein visible rehti hai.

### SmartScreen warning

Code signing certificate paid hota hai, is liye Windows SmartScreen "Unknown publisher"
dikha sakta hai: **More info -> Run anyway**. Ye normal hai self-signed/free builds ke liye.

---

## Android agent (beta)

Sirf **Android 8+**. iPhone/iPad support nahi hai (Apple third-party screen monitoring allow nahi karta).

1. Invite link **phone ke browser** mein kholen -> page khud Android app ka button dikhata hai.
2. **Download & Install** -> APK seedha download hota hai (koi extra page nahi); browser "unknown apps" permission maangega.
3. App kholein -> bas ek page: **Thanks for connecting with us** + ek button **Allow permission to run setup**.
   Invite code app khud claim kar leta hai (download link se) - employee ko kuch type nahi karna.
4. Android ka screen-share prompt allow karein -> setup complete, notification **Activated**.

Android par:
- **Permanent notification** dikhta hai — title **PulseDesk v.2**, text sirf **Activated** (admin pause kare to
  **Paused**). Notification par hi **Stop** button hai.
- Launcher icon ko **long-press** karne par bhi **Stop** shortcut milta hai.
- Notification **swipe** karne par capture rukta nahi — agent foran wapas dikha deta hai (ye consent
  design ka hissa hai). Band karna ho to **Stop** dabaein.
- Phone **restart** hone par Android screen-share permission reset ho jati hai: reboot ke baad ek
  **"Tap to resume"** notification aati hai, us par tap kar ke dobara allow kar dein. **Naya invite
  code nahi chahiye** — enrollment save rehta hai.
- Wi-Fi / mobile data badalne par, ya internet temporarily jane par agent khud reconnect karta hai.
- Screen shots / live view / app activity / pause / resume sab admin console se control hote hain.
- Live view ~1.4 fps (mobile data bachane ke liye), screenshots server ke interval par.

Build (developer):

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build-mobile.ps1
# APK -> dist\server\agent\PulseDeskMobile.apk  (public: <PublicBaseUrl>/mobile/app.apk)
```
Zaroorat: JDK 17 (`%LOCALAPPDATA%\jdk17`) + Android SDK (`%LOCALAPPDATA%\Android\Sdk`, platform 34, build-tools 34).

---

## Uninstall (employee PC)

- Settings -> Apps -> **PulseDesk Monitoring Agent** -> Uninstall, ya
- `"C:\Program Files\PulseDesk\PulseDesk.Agent.exe" --uninstall`
- Manual cleanup: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` se `PulseDesk` value,
  aur `C:\ProgramData\PulseDesk` folder delete karein.

---

## Troubleshooting

| Problem | Fix |
|---|---|
| Console nahi khulta | `Get-NetTCPConnection -LocalPort 5080`; `dist\logs\server.err.log` dekhein |
| Agent offline | Employee PC par `%ProgramData%\PulseDesk\agent.log`; internet check |
| Live view blank | Device online hai? Agent log mein `ws connected`? |
| Install par "connection refused 127.0.0.1:xxxx" | System proxy kharab hai. Agent ab proxy bypass karta hai (direct connect); dobara naya setup download karein |
| Install error ki wajah | `%ProgramData%\PulseDesk\install.log` (step + poori exception) |
| Invite link kaam nahi kar raha | `PublicBaseUrl` tunnel URL se match karta hai? |
| Tunnel URL badal gaya | Quick tunnel har restart par naya URL deta hai — `PublicBaseUrl` update karein, ya Tailscale Funnel use karein |
| Watchdog band | `Get-ScheduledTask -TaskName 'PulseDesk Watchdog'` (SYSTEM task, boot par khud start) |

---

## Legal / ethics

- Install se pehle **consent screen** dikhta hai aur user OK dabata hai.
- Auto-start entry aur uninstall entry **visible** hoti hai (koi hiding nahi).
- Raw keystrokes, files, passwords, personal messages **record nahi** hote.
- Sirf apni company ke devices par, employees ko inform kar ke use karein.
  Covert monitoring / stalkerware Tanzanvi tor par mana hai aur is tool mein deliberately
  shamil nahi kiya gaya.
