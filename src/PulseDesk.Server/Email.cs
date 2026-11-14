using System.Net;
using System.Net.Mail;
using System.Text;

public static class Email
{
    /// <summary>Sends the invite. Returns false when SMTP is not configured.</summary>
    public static async Task<bool> Send(IConfiguration cfg, string to, string link, string note)
    {
        var host = cfg["SmtpHost"] ?? "";
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(to)) return false;

        int port = int.TryParse(cfg["SmtpPort"], out var p) ? p : 587;
        var user = cfg["SmtpUser"] ?? "";
        var pass = cfg["SmtpPass"] ?? "";
        var from = cfg["SmtpFrom"] ?? user;

        using var msg = new MailMessage(from, to)
        {
            Subject = "PulseDesk v.2 - device setup",
            IsBodyHtml = true,
            Body = Template(link, note)
        };
        msg.Headers.Add("X-PulseDesk", "invite");

        using var client = new SmtpClient(host, port) { EnableSsl = true };
        if (!string.IsNullOrEmpty(user))
            client.Credentials = new NetworkCredential(user, pass);
        try
        {
            await client.SendMailAsync(msg);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string Template(string link, string note)
    {
        var sb = new StringBuilder();
        sb.Append(@"
<div style=""background:#0b1a1c;padding:28px;font-family:Segoe UI,Arial,sans-serif;color:#dfece8"">
  <div style=""max-width:560px;margin:0 auto;background:#0f2a2a;border:1px solid #1d4a47;border-radius:14px;padding:28px"">
    <div style=""font-size:22px;font-weight:700;color:#ffffff"">Pulse<span style=""color:#e0645c"">Desk</span> <span style=""font-size:14px;color:#e0645c"">v.2</span></div>
    <p style=""color:#9fb8b3;margin-top:6px"">Connect with us</p>
    <hr style=""border:none;border-top:1px solid #1d4a47;margin:20px 0""/>
    <p>Assalam-o-Alaikum,</p>
    <p>Aap ke device ka PulseDesk setup tayyar hai. Ismein sirf 2 minute lagte hain:</p>
    <p style=""margin:22px 0"">
      <a href=""" + link + @""" style=""background:#e0645c;color:#fff;text-decoration:none;padding:12px 22px;border-radius:8px;font-weight:600;display:inline-block"">
        Open setup link
      </a>
    </p>
    <p style=""color:#9fb8b3;font-size:13px"">Ya link copy karein:<br/><span style=""color:#7fd8cc"">" + link + @"</span></p>");
        if (!string.IsNullOrWhiteSpace(note))
            sb.Append("<p style=\"background:#123634;padding:12px;border-radius:8px;font-size:14px\">" + WebUtility.HtmlEncode(note) + "</p>");
        sb.Append(@"
    <p style=""color:#9fb8b3;font-size:12px;margin-top:22px"">
      Setup ke doran consent screen aati hai - aap ki marzi ke baghair kuch share nahi hota.
      Kisi bhi sawal ke liye apne administrator se rabta karein.
    </p>
  </div>
</div>");
        return sb.ToString();
    }
}
