using Wasla.Application.Abstractions.Email;

namespace Wasla.Infrastructure.Email;

/// <summary>
/// Builds the post-provisioning "panel is ready" email sent to the restaurant owner.
/// </summary>
public static class ProvisioningEmailTemplate
{
    public static EmailMessage BuildPanelReadyEmail(
        string ownerEmail,
        string ownerFullName,
        string primaryDomain)
    {
        var loginUrl = $"https://{primaryDomain}/auth/login";

        var html = $"""
            <!DOCTYPE html>
            <html lang="tr">
            <head>
              <meta charset="UTF-8" />
              <title>Wasla paneliniz hazır</title>
            </head>
            <body style="font-family: Arial, sans-serif; color: #222222; max-width: 600px; margin: 0 auto; padding: 24px;">
              <h2 style="color: #0d6efd;">Wasla</h2>
              <p>Merhaba {ownerFullName},</p>
              <p>Restoran paneliniz başarıyla oluşturuldu.</p>
              <p><strong>Panel adresiniz:</strong></p>
              <p style="margin: 16px 0;">
                <a href="{loginUrl}"
                   style="background-color: #0d6efd; color: #ffffff; padding: 12px 24px;
                          text-decoration: none; border-radius: 4px; display: inline-block;">
                  Panele Giriş Yap
                </a>
              </p>
              <p style="color: #555555;">{loginUrl}</p>
              <p>Bu adres üzerinden Wasla restoran panelinize giriş yapabilirsiniz.</p>
              <hr style="border: none; border-top: 1px solid #eeeeee; margin: 24px 0;" />
              <p style="color: #888888; font-size: 13px;">Wasla</p>
            </body>
            </html>
            """;

        var text = $"""
            Merhaba {ownerFullName},

            Restoran paneliniz başarıyla oluşturuldu.

            Panel adresiniz:
            {loginUrl}

            Bu adres üzerinden Wasla restoran panelinize giriş yapabilirsiniz.

            Wasla
            """;

        return new EmailMessage
        {
            ToEmail = ownerEmail,
            ToName = ownerFullName,
            Subject = "Wasla restoran paneliniz hazır",
            HtmlBody = html,
            TextBody = text,
        };
    }
}
