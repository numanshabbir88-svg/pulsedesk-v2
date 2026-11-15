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
- [ ] **Quality: Sharp / Balanced / Fast** — Sharp = 1920px @ 80%, Balanced = 1600px @ 68% (~3 fps),
      Fast = 1280px @ 52% (slow internet / mobile data).
- [ ] **Remote control** dabao → "Stop control" ho jata hai. Iske baad:
      * mouse move, left/right/middle click, scroll — sab device par chalta hai
      * keyboard: normal typing, Enter/Tab/Esc/Backspace, arrows, F1–F12, Ctrl/Alt/Shift shortcuts
      * device par ek chhota banner dikhta hai: *"PulseDesk - remote control active (admin)"*
- [ ] **Stop control** dabao (ya full screen se bahar aao) → input band, banner gayab, dashboard
      **Alerts** me "remote control START/STOP" record ho jata hai.
- [ ] Remote control sirf **Windows agent** par hai (Android nahi). Live screen sirf **primary**
      monitor capture karti hai — second display nahi aata (isliye pehle image "crop" lagti thi).

---

## 6. Verification checklist (after each enrolment)

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

## 7. Troubleshooting

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

---

## 8. Command reference

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

## 9. Legal / policy notes (do not skip)

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
