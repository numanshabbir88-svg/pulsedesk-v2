using System.Net;

public static class JoinPage
{
    public static string Html(string code, string baseUrl)
    {
        var safeCode = WebUtility.HtmlEncode(code);
        var dl = $"{baseUrl}/download/{code}";
        return $@"<!DOCTYPE html>
<html lang='en'><head>
<meta charset='utf-8'/><meta name='viewport' content='width=device-width,initial-scale=1'/>
<title>PulseDesk v.2 - Install</title>
<style>
 :root{{--teal:#0d3b39;--coral:#e0645c;--pale:#dfece8;--muted:#93b0aa}}
 *{{box-sizing:border-box}}
 body{{margin:0;min-height:100vh;display:grid;place-items:center;background:radial-gradient(circle at 50% 20%,#123f3c,#071a1c 70%);
      font-family:'Segoe UI',Arial,sans-serif;color:var(--pale);padding:24px}}
 .card{{max-width:520px;width:100%;background:rgba(10,32,32,.86);border:1px solid #1d4a47;border-radius:18px;
       padding:32px;box-shadow:0 30px 80px rgba(0,0,0,.5)}}
 .brand{{font-size:26px;font-weight:700;letter-spacing:.3px}}
 .brand span{{color:var(--coral)}}
 .sub{{color:var(--muted);font-size:13px;margin-top:4px;letter-spacing:.4px;text-transform:uppercase}}
 h1{{font-size:21px;margin:26px 0 8px;font-weight:600}}
 p{{color:var(--muted);font-size:14px;line-height:1.6;margin:8px 0}}
 .btn{{display:block;text-align:center;margin:24px 0 10px;background:var(--coral);color:#fff;text-decoration:none;
      padding:15px;border-radius:10px;font-weight:600;font-size:16px}}
 .btn:hover{{filter:brightness(1.08)}}
 .steps{{margin:22px 0;padding:0;list-style:none;font-size:13.5px;color:var(--pale)}}
 .steps li{{display:flex;gap:10px;padding:7px 0;align-items:flex-start}}
 .num{{flex:0 0 22px;height:22px;border-radius:50%;background:#164a46;color:#7fd8cc;display:grid;place-items:center;font-size:12px;font-weight:700}}
 .warn{{margin-top:20px;background:#2a1a18;border:1px solid #5a2f2a;border-radius:10px;padding:14px;font-size:12.5px;color:#f0c9c3}}
 code{{background:#0a2422;padding:2px 6px;border-radius:5px;color:#7fd8cc;font-size:12px}}
</style></head>
<body>
 <div class='card'>
   <div class='brand'>Pulse<span>Desk</span> <span style='font-size:15px;color:#e0645c;font-weight:700'>v.2</span></div>
   <div class='sub'>Company monitoring agent</div>

   <h1>Aap ke computer ka setup</h1>
   <p>Ye link sirf aap ke device ke liye hai. Install ke baad koi setting nahi karni - sab kuch admin
      control karta hai.</p>

   <ul class='steps'>
     <li><span class='num'>1</span><span>Neeche button dabaein - setup file download hogi</span></li>
     <li><span class='num'>2</span><span>Downloaded file ko run karein</span></li>
     <li><span class='num'>3</span><span>Windows ka <b>&quot;Yes&quot;</b> permission prompt - ek baar approve karein</span></li>
     <li><span class='num'>4</span><span>Consent screen par tick + Install - bas, ho gaya</span></li>
   </ul>

   <a class='btn' href='{dl}' download='PulseDeskSetup.exe'>Download &amp; Install</a>
   <p style='text-align:center;font-size:12px'>Invite code: <code>{safeCode}</code></p>

   <div class='warn'>
     <b>Transparency:</b> install ke baad aap ke PC par ek agent service chalegi jo screen, apps aur
     activity record karti hai - company policy ke tehat. System tray mein icon dikhega, aur aap kabhi
     bhi admin se monitoring pause karwa sakte hain.
   </div>
 </div>
</body></html>";
    }
}
