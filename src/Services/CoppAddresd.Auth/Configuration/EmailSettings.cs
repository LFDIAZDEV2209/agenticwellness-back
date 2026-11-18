namespace CoppAddresd.Auth.Configuration;

/// <summary>
/// Configuración del envío de correos transaccionales (invitaciones).
/// <c>Provider</c>: "Log" (dev: registra metadatos sin enlaces de acceso) o "Smtp"
/// (producción). <c>FrontendUrl</c> se usa para construir los enlaces de
/// invitación que llegan al profesional.
/// </summary>
public class EmailSettings
{
    public const string SectionName = "Email";

    public string Provider { get; set; } = "Log";

    public string From { get; set; } = "no-reply@coppaddresd.com";

    public string FromName { get; set; } = "CoppAddresd";

    /// <summary>Base URL del frontend para enlaces (ej. http://localhost:3000).</summary>
    public string FrontendUrl { get; set; } = "http://localhost:3000";

    /// <summary>Vacío: se usa FrontendUrl/login. Debe apuntar al ERP oficial.</summary>
    public string? ErpLoginUrl { get; set; }
    /// <summary>Sin publicar: el correo muestra Próximamente, sin enlaces ficticios.</summary>
    public string? IosDownloadUrl { get; set; }
    public string? AndroidDownloadUrl { get; set; }
    public bool WelcomeEnabled { get; set; } = true;
    public int WelcomeMaxAttempts { get; set; } = 5;

    // SMTP (usado solo con Provider = "Smtp").
    public string SmtpHost { get; set; } = string.Empty;

    public int SmtpPort { get; set; } = 587;

    public string? SmtpUsername { get; set; }

    public string? SmtpPassword { get; set; }

    public bool SmtpUseSsl { get; set; } = true;
}
