# PulseDesk v.2 — Admin Guide

Employee monitoring for company-owned Android phones. This guide covers:

1. One-time admin setup
2. Adding a phone with the invite link (3 taps, no cable)
3. Adding a phone over USB / adb (zero prompts — best for bulk rollout)
4. Optional: managing the fleet with a free MDM (no factory reset)
5. Verification checklist
6. Troubleshooting
7. Command reference

---

## 0. What you have

| Item | Value |
|---|---|
| Admin dashboard (this PC) | `http://localhost:5080` |
| Admin dashboard (internet, Tailscale) | `https://desktop-l21qf2i.taila5e0e7.ts.net/pulsedesk` |
| Invite link format | `<public-url>/join/<CODE>` |
| APK served by the server | `<public-url>/mobile/app.apk` |
| Admin password | `dist\server\appsettings.json` → `AdminPassword` |
| Signed Android build | `dist\server\agent\PulseDeskMobile.apk` (v2.2, versionCode 5) |
| Release key + fingerprint | `src\PulseDesk.Mobile\keystore\` (see `README.txt`) |

**Important:** the APK is signed with the PulseDesk release key. A phone that already has an
older **debug-signed** build must uninstall it first (Android refuses to update across
different signatures). Fresh phones are not affected.

---

## 1. One-time admin setup

- [ ] Start (or update) the server:
  ```
  powershell -ExecutionPolicy Bypass -File scripts\publish.ps1
  powershell -ExecutionPolicy Bypass -File scripts\deploy-server-elevated.ps1   (as Administrator)
  ```
- [ ] Open `http://localhost:5080` and log in with the admin password.
- [ ] Confirm **PublicBaseUrl** in `dist\server\appsettings.json` matches the address employees
      will reach (Tailscale URL or LAN IP). Invite links are built from it.
- [ ] If you want invite e-mails sent automatically, fill `SmtpHost`, `SmtpPort`, `SmtpUser`,
      `SmtpPass`, `SmtpFrom`. Without SMTP, copy the link from the dashboard and send it yourself
      (WhatsApp/SMS/e-mail).
- [ ] Decide how phones will be enrolled:
      * **Invite link** → Section 2 (works on every brand, 3 taps per phone)
      * **USB / adb** → Section 3 (no prompts at all, needs USB debugging once per phone)
      * **MDM work profile** → Section 4 (one-time accept per phone, then fully silent updates)

---

## 2. Add a phone with the invite link (no cable)

### 2a. Admin side (30 seconds per phone)

- [ ] Dashboard → **Invites**.
- [ ] Type the employee's e-mail (or a name) in **Email**, optional **Note** (e.g. "Ali — Sales").
- [ ] Press **Send invite**.
- [ ] The new row shows a **code** and a **link** like
      `https://<public-url>/join/AB12CD34EF`.
- [ ] Copy that link and send it to the employee (WhatsApp / SMS / e-mail).

### 2b. Phone side (employee or you, 3 taps)

- [ ] Tap the invite link in the message.
      → On Android the landing page is **skipped**; `PulseDeskMobile.apk` downloads directly.
- [ ] Open the downloaded file (notification bar → **PulseDeskMobile.apk**).
- [ ] If Android asks, allow **Install unknown apps** for the browser/WhatsApp that opened it
      (Settings → Apps → Special access → Install unknown apps → allow the browser).
- [ ] If Play Protect / Auto Blocker shows a warning:
      * Play Protect: **More details → Install anyway**
      * Samsung Auto Blocker: Settings → **Security and privacy → Auto Blocker → OFF**
- [ ] Tap **Install** → **Open**.
- [ ] PulseDesk connects to the server automatically, claims the invite, and shows
      *PulseDesk v.2 — Activated* in the notification bar.
- [ ] Tap **Allow** on the screen-sharing prompt (Android requires this tap; no app or MDM can
      skip it).
- [ ] Back in the dashboard → **Devices**: the phone appears as **online** within ~30 seconds.

> The invite code is claimed automatically — the employee never types a code, and a reinstall
> keeps the same device identity.

---

## 3. Add a phone over USB / adb (zero prompts, best for 20 phones)

This is the fast path when phones are physically in your hands. `adb install` is a trusted
install route, so **Play Protect / Auto Blocker / "Install anyway" never appear**.

### 3a. Once per phone (admin does it, ~1 minute)

- [ ] Settings → **About phone** → tap **Build number** 7 times → "You are now a developer".
- [ ] Settings → **Developer options** → **USB debugging → ON**.
- [ ] Connect the phone to the admin PC with a USB cable.
- [ ] On the phone, tap **Allow** on the "Allow USB debugging?" dialog (tick "Always allow").

> Brand notes: Samsung → *About phone → Software information → Build number*;
> Realme/Oppo → *About device → Version → Build number*;
> Tecno → *About phone → Build number*; Huawei → *About phone → Build number*.

### 3b. Per phone, from the admin PC (~30 seconds each)

- [ ] Dashboard → **Invites** → **Send invite** → copy the code.
- [ ] Run (one phone connected at a time):
      ```
      powershell -ExecutionPolicy Bypass -File scripts\install-mobile.ps1 -Code <CODE>
      ```
      Optional: add `-Server https://<public-url>` if the phone must use a non-default server.
- [ ] The script installs the APK, grants runtime permissions, excludes the app from battery
      optimisation / Samsung sleeping apps, launches it, and passes the invite code.
- [ ] Phone: tap **Allow** on the screen-sharing prompt. Nothing else.
- [ ] Dashboard → **Devices**: phone shows **online**. Unplug, next phone.

### 3c. Updating already-installed phones

- [ ] Build/publish the new APK (Section 7), then rerun the same script.
      `-r` keeps app data, so the device stays enrolled — no new invite needed.
- [ ] Re-signing old debug builds: uninstall first (`adb uninstall com.pulsedesk.mobile`).

---

## 4. Optional: free MDM for silent install / updates (no factory reset)

Use this when you want to push installs and updates **without touching the phones again**.
Work-profile enrolment keeps personal data and does **not** require a factory reset.

- [ ] Pick one: **Headwind MDM** (open-source, self-hosted, free) or
      **ManageEngine MDM Plus** (cloud, free tier ~25 devices).
- [ ] Create the MDM account, add the PulseDesk APK as a managed app, create an enrolment
      QR/link.
- [ ] On each phone: install the MDM agent → **Scan the QR / open the link** →
      **Set up work profile** → **Accept** (2–3 taps, data stays intact).
- [ ] From then on: MDM console → push app → installs/updates **silently**. No Play Protect,
      no Auto Blocker, no user taps.
- [ ] Huawei phones without Google Mobile Services cannot use Managed Google Play —
      enrol them with the invite link (Section 2) or USB (Section 3).

> MDM does **not** remove the screen-share "Allow" tap. That consent is enforced by Android
> per session and is part of PulseDesk's transparent, consent-based design.

---

## 5. Live screen + remote control (Windows agent)

- [ ] Dashboard → **Live screen** → device select → **Connect**.
- [ ] Image par click = **full screen**; **Exit full screen** se wapas. **Minimize** = full screen band.
- [ ] **Quality: Full HD 1080p / Ultra / Balanced / Fast** — Full HD = 1920px @ 84% (default, sharp
      text), Ultra = 2560px @ 88% (2K/4K screens), Balanced = 1600px @ 72%, Fast = 1280px @ 55%
      (slow internet / mobile data).
- [ ] **Zoom: Fit / 100% / 150% / 200%** — chhota text parhne ke liye. 100% = stream ke asli pixels,
      image ko scroll karke pan karein. Remote control ON hone par mouse coordinates zoom ke saath
      khud adjust hote hain.
- [ ] Agent ab **DPI-aware** hai (PerMonitorV2): 125%/150% display scaling wale laptop par bhi
      capture asli pixels me hota hai, is liye text blurry nahi aata.
- [ ] **Remote control** dabao → "Stop control" ho jata hai. Iske baad:
      * employee ka **keyboard + mouse block** ho jata hai (local input swallow hota hai)
      * admin ka mouse move, left/right/middle click, scroll — sab device par chalta hai
      * keyboard: normal typing, Enter/Tab/Esc/Backspace, arrows, F1–F12, Ctrl/Alt/Shift shortcuts
      * device par ek chhota banner dikhta hai: *"PulseDesk - admin control active (keyboard + mouse locked)"*
- [ ] **Stop control** dabao → employee ka input foran wapas, banner gayab, dashboard
      **Alerts** me "remote control START/STOP" record ho jata hai.
- [ ] Safety: admin ka tab band / network cut ho jaye to **45 second** baad control khud release ho jata
      hai (agent heartbeat dekhta hai) — employee permanently locked nahi rehta.
- [ ] Ctrl+Alt+Del hamesha kaam karta hai (Windows security screen hook nahi hoti) — employee ke paas
      emergency exit rehta hai.
- [ ] **Locked laptop**: dashboard par **LOCKED** pill dikhti hai. Windows lock screen (secure
      desktop) par koi bhi app — PulseDesk, AnyDesk, TeamViewer — input inject nahi kar sakta;
      unlock Windows ka security boundary hai. Employee ko unlock karna hoga.
- [ ] Control ke doran agent display/session ko **jagta** rakhta hai (auto-sleep/auto-lock nahi hota),
      is liye session ke beech me laptop lock nahi hoga.
- [ ] Agar company policy allow kare to idle lock **GPO** se band kar sakte hain (neeche section 10).
- [ ] Remote control sirf **Windows agent** par hai (Android nahi). Live screen sirf **primary**
      monitor capture karti hai — second display nahi aata (isliye pehle image "crop" lagti thi).

---

## 6. Phone / tablet se admin console (PWA)

Dashboard ko phone par app ki tarah install kiya ja sakta hai — usi console se laptops monitor
aur control hote hain.

- [ ] Phone browser (Chrome / Edge / Safari) me public URL kholo:
      `https://desktop-l21qf2i.taila5e0e7.ts.net/pulsedesk/index.html`
      (LAN par: `http://<PC-IP>:5080`).
- [ ] Agar Tailscale use kar rahe hain to phone par **Tailscale app** installed + same account
      me login hona chahiye. Warna LAN IP use karein.
- [ ] Admin password se **Sign in** (token phone me save ho jata hai, dobara nahi poochta).
- [ ] Browser menu → **Add to home screen / Install app**. Ab icon se full-screen console khulta hai.
- [ ] Mobile layout: navigation upar ek horizontal bar me aa jati hai; tables side me scroll hoti hain.
- [ ] **Live screen** → device → **Connect** → tap se full screen.
- [ ] **Remote control** ON karo, phir:
      * **Tap** = left click
      * **Long press (~0.5s)** = right click
      * **Drag** = mouse move
      * neeche touch bar: text box + **Send text**, **Enter / Esc / Tab / Backspace**, **▲ ▼ Scroll**
- [ ] Kaam khatam → **Stop control** (laptop par banner bhi gayab ho jayega).

Mahdoodiyat (mobile par):

- Live view aur screenshots har phone par chalta hai; remote control sirf **Windows** agents par.
- Phone keyboard shortcuts (Ctrl+C etc.) mobile browsers nahi bhejte — text box se normal typing karein.
- Bahut chhoti screen par live image ki tafseel kam dikhti hai; detail ke liye `Zoom: 100%` ya
  `Quality: Ultra` chunein.
- **Text blurry / words clear nahi** — (1) laptop par naya agent install karo (DPI-aware build),
  (2) dashboard par **Ctrl+F5**, (3) `Quality: Full HD 1080p`, (4) zaroorat par `Zoom: 100%`.
  Purana agent 125%/150% scaling par Windows ke stretch ki wajah se blurry capture bhejta tha.
- Elevation wali windows (Task Manager / UAC) par click nahi jata — Windows ka integrity level rok.

---

## 7. Verification checklist (after each enrolment)

| Check | Expected |
|---|---|
| Install | no Play Protect / Auto Blocker block (adb route: no prompt at all) |
| Notification | "PulseDesk v.2 — Activated" appears immediately |
| Dashboard → Devices | device **online** within 30 s, correct name/model |
| Live view | screen stream starts after "Allow" |
| Invite list | code marked as used / device linked, no duplicate |
| Reboot phone | app comes back online by itself |
| Reinstall / update | no new invite code required |

---

## 8. Troubleshooting

- **"App not installed" / signature mismatch** — an older debug-signed PulseDesk is installed.
  Run `adb uninstall com.pulsedesk.mobile` (or uninstall from Settings), then reinstall.
- **Play Protect keeps removing the app** — only happens on the browser/APK route with a
  debug-signed or unknown build; use the release-signed APK and, on Samsung, turn Auto Blocker off.
- **Device stays offline** — check the phone is on the same reachable server URL
  (`PublicBaseUrl`), and that battery optimisation is off for PulseDesk
  (the adb script does this automatically). Logs: `adb logcat -s PulseDesk`.
- **Invite link opens a web page instead of downloading** — that is the desktop/iPhone page;
  it appears only when the request is not from an Android browser. Copy the link into the
  phone's browser.
- **Wrong invite claimed** — invite codes are claimed "last download wins"; issue one invite per
  phone and enrol one phone at a time.
- **Server not reachable from outside** — Tailscale must be running on the server PC, or use the
  LAN IP in `PublicBaseUrl` for phones on the same Wi-Fi.
- **Remote control: mouse/keyboard nahi chalta** — check list:
  1. Laptop par **naya agent** install hua ho (purana build `control-on` ignore karta hai).
     Naya version: dashboard → Invites → us laptop ke liye setup dobara chalao.
  2. Dashboard → Live screen → **Connect** pehle, phir **Remote control**.
  3. Laptop par banner dikhna chahiye: *"PulseDesk - admin control active (keyboard + mouse locked)"*.
     Banner na aaye to agent chal hi nahi raha.
  4. `%ProgramData%\PulseDesk\agent.log` me ye lines honi chahiye:
     `input lock: ON (employee keyboard/mouse blocked)` aur `remote input received from admin`.
  5. Laptop sleep / lock screen par ho to pehle unlock karo — lock screen par input nahi jata.
- **Employee ka input band nahi ho raha** — agent purana hai (upar point 1). Naya agent low-level
  hooks se local input swallow karta hai; koi setting enable karne ki zaroorat nahi.
- **Remote control khud band ho gaya** — safety feature: admin tab band / network drop par
  45 second me auto-release. Dobara **Remote control** dabao.

---

## 9. Command reference

```powershell
# build the Android APK into dist\server\agent\PulseDeskMobile.apk
powershell -ExecutionPolicy Bypass -File scripts\build-mobile.ps1

# publish server + agent + APK into dist\ (server_new if the server is running)
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1

# swap the staged build in and restart (must be elevated)
powershell -ExecutionPolicy Bypass -File scripts\deploy-server-elevated.ps1

# install / update one phone over USB with an invite code
powershell -ExecutionPolicy Bypass -File scripts\install-mobile.ps1 -Code AB12CD34EF
powershell -ExecutionPolicy Bypass -File scripts\install-mobile.ps1 -Code AB12CD34EF -Server https://desktop-l21qf2i.taila5e0e7.ts.net/pulsedesk

# check which key signed the APK
$env:JAVA_HOME = "$env:LOCALAPPDATA\msjdk17\jdk-17.0.21.0.2+1"
& "$env:LOCALAPPDATA\Android\Sdk\build-tools\34.0.0\apksigner.bat" verify --print-certs dist\server\agent\PulseDeskMobile.apk

# confirm release signing will be used for the next build
# gradle -p src\PulseDesk.Mobile pdSigningInfo
```

---

## 10. Legal / policy notes (do not skip)

- PulseDesk is **consent-based**: permanent notification, visible status, and a **Stop** action.
  Keep it that way — a hidden build gets auto-removed by Play Protect and creates legal exposure.
- Remote control (mouse/keyboard) is opt-in per session: the device shows an always-on-top
  banner while it is active, and every START/STOP is written to **Alerts**. Tell employees
  in advance that admin can take control of company machines.
- Tell employees in writing that company phones are monitored, and keep a signed acknowledgement.
- Never bypass Play Protect / Auto Blocker with code inside the app. Use the supported routes:
  signed APK, USB/adb, or MDM.
- Keep `keystore\pulsedesk-release.jks`, its password, and `appsettings.json` out of public
  repositories.
- **Never store employee Windows passwords** in PulseDesk and never install a credential-provider
  "unlock" component. Windows deliberately blocks input into the lock screen; getting around it is
  a backdoor, not a feature.

### Locked laptop — what is actually possible

Windows lock screen (Winlogon "secure desktop") is a separate desktop. No normal app — PulseDesk,
AnyDesk, TeamViewer — can type into it. Only a credential provider / SYSTEM-level hook can do that,
and that is exactly how password-stealing malware works. So:

| Option | Effect | Risk |
| --- | --- | --- |
| Employee unlocks | Always works, zero risk | Needs the employee |
| Keep-awake while controlling (built in) | Laptop does not auto-lock mid-session | none |
| Machine policy: no idle lock / no sleep | Laptop stays unlocked during work hours | weaker physical security |
| Autologon | Boot goes straight to desktop | password in plaintext registry — **not recommended** |
| RDP with an admin account | Admin logs on over RDP instead | employee session gets disconnected |

Policy route (needs local admin, apply to company-owned machines only):

```powershell
# no auto-lock after idle (Interactive logon: Machine inactivity limit = 0)
New-Item -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -Force | Out-Null
Set-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -Name InactivityTimeoutSecs -Value 0 -Type DWord

# screensaver must not lock the screen
Set-ItemProperty 'HKCU:\Control Panel\Desktop' -Name ScreenSaveActive -Value '0'
Set-ItemProperty 'HKCU:\Control Panel\Desktop' -Name ScreenSaverIsSecure -Value '0'

# never sleep / never blank the display while plugged in
powercfg /change standby-timeout-ac 0
powercfg /change monitor-timeout-ac 0
powercfg /change hibernate-timeout-ac 0
```

Reverse the policy when the protection matters more than remote control (e.g. set
`InactivityTimeoutSecs` back to `900` for 15 minutes).

