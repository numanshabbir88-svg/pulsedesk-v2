using System.Net;

public static class JoinPage
{
    public static bool IsMobile(string? userAgent)
    {
        if (string.IsNullOrEmpty(userAgent)) return false;
        var ua = userAgent.ToLowerInvariant();
        return ua.Contains("android") || ua.Contains("iphone") || ua.Contains("ipad") ||
               ua.Contains("ipod") || ua.Contains("mobile") || ua.Contains("windows phone");
    }

    public static string Html(string code, string baseUrl, bool mobile = false, long expectedBytes = 0)
    {
        var safeCode = WebUtility.HtmlEncode(code);
        var dl = $"{baseUrl}/download/{code}";

        var steps = mobile
            ? @"<ol class='steps'>
     <li><span class='num'>1</span><span>Download the Android app (APK) using the button below.</span></li>
     <li><span class='num'>2</span><span>Open the file and allow <b>&quot;Install unknown apps&quot;</b> when Android asks.</span></li>
     <li><span class='num'>3</span><span>Open PulseDesk &mdash; it connects automatically.</span></li>
     <li><span class='num'>4</span><span>Tap <b>Allow permission to run setup</b> and confirm the Android prompt.</span></li>
   </ol>"
            : @"<ol class='steps'>
     <li><span class='num'>1</span><span>Click the button below to download the setup file.</span></li>
     <li><span class='num'>2</span><span>Run the downloaded file.</span></li>
     <li><span class='num'>3</span><span>Approve the Windows prompt once (<b>Yes</b>).</span></li>
     <li><span class='num'>4</span><span>The PulseDesk window opens &mdash; press the red <b>Allow</b> button.</span></li>
   </ol>";

        // Desktop download is chunked in the page itself. A quick tunnel can drop one long 22 MB
        // transfer near the end; 2 MB Range requests with retry leave a complete, openable .exe.
        var desktopScript = @"<script>
(function () {
  var url = '__DL__';
  var total = __TOTAL__;
  var btn = document.getElementById('dlBtn');
  var status = document.getElementById('dlStatus');
  if (!btn || !total) return;
  var parts = [], offset = 0, busy = false;
  var CHUNK = 2 * 1024 * 1024;

  function human(n) { return (n / 1048576).toFixed(1) + ' MB'; }
  function say(s) { if (status) status.textContent = s; }

  function save() {
    var blob = new Blob(parts, { type: 'application/octet-stream' });
    if (blob.size !== total) {
      say('File incomplete (' + human(blob.size) + ' / ' + human(total) + '). Click again to resume.');
      busy = false; btn.disabled = false; btn.textContent = 'Resume download';
      return;
    }
    var a = document.createElement('a');
    var u = URL.createObjectURL(blob);
    a.href = u; a.download = 'PulseDeskSetup.exe';
    document.body.appendChild(a); a.click(); a.remove();
    setTimeout(function () { URL.revokeObjectURL(u); }, 600000);
    say('Download complete (' + human(total) + '). Ab downloaded file ko double-click karein.');
    btn.textContent = 'Download again';
    parts = []; offset = 0; busy = false; btn.disabled = false;
  }

  function next(attempt) {
    if (offset >= total) { save(); return; }
    var end = Math.min(offset + CHUNK - 1, total - 1);
    say('Downloading ' + human(offset) + ' / ' + human(total) + '...');
    fetch(url, { headers: { Range: 'bytes=' + offset + '-' + end }, cache: 'no-store' })
      .then(function (r) {
        if (r.status !== 206 && r.status !== 200) throw new Error('HTTP ' + r.status);
        return r.arrayBuffer().then(function (b) { return { r: r, b: b }; });
      })
      .then(function (x) {
        var b = x.b;
        if (x.r.status === 200 && offset === 0) {
          parts = [b]; offset = b.byteLength;
        } else if (x.r.status === 206) {
          parts.push(b); offset += b.byteLength;
        } else {
          throw new Error('Range not supported');
        }
        next(0);
      })
      .catch(function (e) {
        attempt = attempt || 0;
        if (attempt < 6) {
          say('Connection slow - retry ' + (attempt + 1) + '/6 at ' + human(offset) + '...');
          setTimeout(function () { next(attempt + 1); }, 600 * (attempt + 1));
        } else {
          busy = false; btn.disabled = false; btn.textContent = 'Resume download';
          say('Download ruk gaya. ""Resume download"" dabaein - yahin se continue hoga.');
        }
      });
  }

  btn.addEventListener('click', function () {
    if (busy) return;
    busy = true; btn.disabled = true; btn.textContent = 'Downloading...';
    next(0);
  });
})();
</script>".Replace("__DL__", dl).Replace("__TOTAL__", expectedBytes.ToString());

        var action = mobile
            ? $@"<a class='btn' href='{baseUrl}/mobile/app.apk?c={safeCode}' download='PulseDeskMobile.apk'>Download &amp; Install</a>
   <div class='note'>Android 8 or newer. iPhone and iPad are not supported.</div>"
            : $@"<button class='btn' id='dlBtn' type='button'>Download &amp; Install</button>
    <div id='dlStatus' style='text-align:center;font-size:12px;color:#7fd8cc;margin-top:10px;min-height:18px'>Setup ~{Math.Round(expectedBytes / 1048576.0, 1)} MB. Slow connection par bhi automatically resume hoga.</div>
    <noscript><a class='btn' href='{dl}' download='PulseDeskSetup.exe'>Download &amp; Install</a></noscript>
    <div style='text-align:center;margin-top:8px'><a href='{dl}' download='PulseDeskSetup.exe' style='color:#7fd8cc;font-size:11.5px'>Direct download link (agar upar wala button kaam na kare)</a></div>
    {desktopScript}";

        var transparency = mobile
            ? "PulseDesk runs only with your consent. While it is active, screen images and app activity are visible to your company administrator, and an ongoing notification stays visible on this phone. You can stop it at any time from that notification or from the app's Stop shortcut. After a phone restart, open the app once to resume."
            : "PulseDesk runs only with your consent. While it is active, screen images, running apps and activity are visible to your company administrator, and the agent stays in the background. You can ask your administrator to pause it at any time, and you can uninstall it from Windows Settings &rarr; Apps.";

        return $@"<!DOCTYPE html>
<html lang='en'><head>
<meta charset='utf-8'/><meta name='viewport' content='width=device-width,initial-scale=1'/>
<meta name='theme-color' content='#0d3b39'/>
<title>PulseDesk v.2</title>
<style>
 :root{{--teal:#0d3b39;--coral:#e0645c;--pale:#dfece8;--muted:#8fa5a0;--line:#1d4a47}}
 *{{box-sizing:border-box}}
 body{{margin:0;min-height:100vh;display:grid;place-items:center;background:radial-gradient(circle at 50% 15%,#123f3c,#071a1c 72%);
      font-family:'Segoe UI',Arial,sans-serif;color:var(--pale);padding:24px;-webkit-font-smoothing:antialiased}}
 .card{{max-width:540px;width:100%;background:rgba(9,29,29,.9);border:1px solid var(--line);border-radius:20px;
       padding:34px 32px 28px;box-shadow:0 30px 80px rgba(0,0,0,.55)}}
 .top{{display:flex;align-items:center;gap:11px}}
 .mark{{width:34px;height:34px;border-radius:10px;background:linear-gradient(140deg,#17514c,#0a2526);
        border:1px solid #245e58;display:grid;place-items:center;flex:0 0 34px}}
 .mark svg{{width:19px;height:19px;stroke:#e0645c}}
 .brand{{font-size:23px;font-weight:700;letter-spacing:.4px;line-height:1}}
 .brand b{{color:var(--coral);font-weight:700}}
 .ver{{font-size:11px;font-weight:700;color:#7fd8cc;border:1px solid #2f6b66;border-radius:999px;
      padding:2px 7px;margin-left:2px;letter-spacing:.4px}}
 .sub{{color:#6f8a86;font-size:11px;margin:14px 0 0;letter-spacing:2.2px;text-transform:uppercase;font-weight:600}}
 h1{{font-size:16px;margin:26px 0 4px;font-weight:600;letter-spacing:.2px;color:#c8dfda}}
 .steps{{margin:12px 0 4px;padding:0;list-style:none;font-size:13.5px;color:#cfe4df;counter-reset:s}}
 .steps li{{display:flex;gap:11px;padding:8px 0;align-items:flex-start;line-height:1.5}}
 .num{{flex:0 0 21px;height:21px;border-radius:50%;background:#123f3c;border:1px solid #2c6a64;color:#7fd8cc;
      display:grid;place-items:center;font-size:11px;font-weight:700;margin-top:1px}}
 .btn{{display:block;width:100%;text-align:center;margin:26px 0 10px;background:var(--coral);color:#fff;
      text-decoration:none;border:0;padding:15px;border-radius:11px;font-weight:600;font-size:15.5px;
      font-family:inherit;cursor:pointer;transition:filter .15s}}
 .btn:hover{{filter:brightness(1.08)}}
 .note{{text-align:center;font-size:11.5px;color:#69807c;margin-top:12px}}
 details{{margin-top:26px;border-top:1px solid #163c39;padding-top:16px}}
 summary{{display:inline-flex;align-items:center;gap:8px;list-style:none;cursor:pointer;
         background:rgba(255,255,255,.035);border:1px solid #21403d;border-radius:8px;
         padding:8px 12px;font-size:11.5px;color:#8fa5a0;letter-spacing:.5px}}
 summary::-webkit-details-marker{{display:none}}
 summary:hover{{border-color:#2c5a56;color:#a8c2bd}}
 summary svg{{width:13px;height:13px;stroke:#7b9490;flex:0 0 13px}}
 details p{{margin:12px 2px 0;font-size:12px;line-height:1.65;color:#7d9590}}
</style></head>
<body>
 <div class='card'>
   <div class='top'>
     <div class='mark'>
       <svg viewBox='0 0 24 24' fill='none' stroke-width='1.9' stroke-linecap='round' stroke-linejoin='round'>
         <rect x='2.5' y='4.5' width='19' height='13' rx='1.6'/>
         <path d='M5.5 11h2.4l1.2-2.4 1.8 5.2 1.5-3.1h4.9'/>
         <path d='M12 17.5v2.5M8.5 21h7'/>
       </svg>
     </div>
     <div class='brand'>Pulse<b>Desk</b><span class='ver'>v.2</span></div>
   </div>

   <div class='sub'>Connect with us</div>

   <h1>Instructions</h1>
   {steps}

   {action}

   <details>
     <summary>
       <svg viewBox='0 0 24 24' fill='none' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'>
         <circle cx='12' cy='12' r='9.2'/><path d='M12 10.6v6'/><circle cx='12' cy='7.4' r='.6' fill='#7b9490'/>
       </svg>
       Transparency
     </summary>
     <p>{transparency}</p>
   </details>
 </div>
</body></html>";
    }
}
