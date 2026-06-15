namespace Wasla.Infrastructure.Options;

public sealed class EmailOptions
{
    public const string SectionName = "Email";

    /// <summary>Log (development default — no real email sent) or Smtp.</summary>
    public string Provider { get; set; } = "Log";

    public string FromEmail { get; set; } = "noreply@wasla.local";

    public string FromName { get; set; } = "Wasla";

    public string SmtpHost { get; set; } = string.Empty;

    public int SmtpPort { get; set; } = 587;

    public string SmtpUsername { get; set; } = string.Empty;

    /// <summary>Read from configuration / environment / user-secrets. Never hardcode.</summary>
    public string SmtpPassword { get; set; } = string.Empty;

    public bool EnableSsl { get; set; } = true;
}
