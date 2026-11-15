PulseDesk - Android release key
================================

File      : keystore/pulsedesk-release.jks   (src\PulseDesk.Mobile\keystore\)
Alias     : pulsedesk
Password  : gradle.properties me (PULSEDESK_STORE_PASSWORD / PULSEDESK_KEY_PASSWORD)
Valid     : 2026-11-14  ->  2054-04-01  (10,000 din)
Owner     : CN=PulseDesk, OU=IT, O=PulseDesk, L=Karachi, ST=Sindh, C=PK

SHA-256 fingerprint (store / MDM / Play / Galaxy Store me yahi dikhega):
51:FC:71:03:F5:06:35:2A:10:07:BF:BF:A2:E3:26:2B:F0:EC:B2:95:29:CF:6F:39:AE:92:83:C9:D3:C2:9D:19

ZAROORI
-------
1. Is .jks file ka backup rakho (USB + cloud). Ye kho gayi to dobara isi signature
   ke saath APK nahi ban sakti - install ho chuki app ko update nahi kar paoge,
   sirf uninstall kar ke nayi app install karni padegi.
2. Password kisi ke saath share na karo. gradle.properties ko git me commit mat karo.
3. Purani debug-signed build wale phones par ye APK install nahi hogi
   (signature mismatch) - wahan se pehle uninstall karna hoga.
