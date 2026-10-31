using DocRouter.Core;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace DocRouter.Infrastructure;

public sealed class SmtpMailSender(IOptions<SmtpOptions> options) : IMailSender
{
    public async Task SendAsync(IReadOnlyList<string> to, string subject, string body, CancellationToken ct)
    {
        var o = options.Value;
        var msg = new MimeMessage();
        msg.From.Add(MailboxAddress.Parse(o.From));
        foreach (var t in to) msg.To.Add(MailboxAddress.Parse(t));
        msg.Subject = subject;
        msg.Body = new TextPart("plain") { Text = body };

        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(o.Host, o.Port, o.UseSsl ? SecureSocketOptions.StartTls : SecureSocketOptions.None, ct);
        if (!string.IsNullOrEmpty(o.User)) await smtp.AuthenticateAsync(o.User, o.Password, ct);
        await smtp.SendAsync(msg, ct);
        await smtp.DisconnectAsync(true, ct);
    }
}
