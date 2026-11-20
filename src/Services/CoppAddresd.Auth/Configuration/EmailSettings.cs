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
    /// <summary>URL HTTPS pública del logo, accesible sin iniciar sesión.</summary>
    public string WelcomeLogoUrl { get; set; } =
        "https://cdn.jsdelivr.net/gh/FontalvoB/antares-paciente@7e375e812ef94685485ee666389da2a1a00636cb/public/LogoIndividual.png";

    public bool WelcomeEnabled { get; set; } = true;
    public int WelcomeMaxAttempts { get; set; } = 5;
    public TransactionalEmailSettings Transactional { get; set; } = new();

    // SMTP (usado solo con Provider = "Smtp").
    public string SmtpHost { get; set; } = string.Empty;

    public int SmtpPort { get; set; } = 587;

    public string? SmtpUsername { get; set; }

    public string? SmtpPassword { get; set; }

    public bool SmtpUseSsl { get; set; } = true;
}

public sealed class TransactionalEmailSettings
{
    public string? Name { get; set; }
    public string? LogoUrl { get; set; }
    public string? AccentColor { get; set; }
    public string? SupportWhatsApp { get; set; }
    public Dictionary<string, TransactionalEmailLanguages> Templates { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class TransactionalEmailLanguages
{
    public TransactionalEmailCopy? Es { get; set; }
    public TransactionalEmailCopy? En { get; set; }
}

public sealed class TransactionalEmailCopy
{
    public string? Subject { get; set; }
    public string? Preheader { get; set; }
    public string? Title { get; set; }
    public string? Button { get; set; }
    public string? Callout { get; set; }
}
