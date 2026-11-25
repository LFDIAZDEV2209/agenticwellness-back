using CoppAddresd.Auth.Configuration;
using CoppAddresd.Auth.Interfaces;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using System.Text;

namespace CoppAddresd.Auth.Services;

/// <summary>
/// Envía correos vía SMTP (producción). Las credenciales y host vienen de
/// <c>Email</c> (Secrets Manager en AWS, nunca commitear valores reales).
/// </summary>
public class SmtpEmailSender(IOptions<EmailSettings> settings, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    private readonly EmailSettings _settings = settings.Value;

    public async Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        using var smtp = new SmtpClient
        {
            Host = _settings.SmtpHost,
            Port = _settings.SmtpPort,
            EnableSsl = _settings.SmtpUseSsl,
            Credentials = string.IsNullOrWhiteSpace(_settings.SmtpUsername)
                ? null
                : new NetworkCredential(_settings.SmtpUsername, _settings.SmtpPassword),
        };

        using var mail = new MailMessage
        {
            From = new MailAddress(_settings.From, EmailSettings.ResolveBrandName(_settings.FromName)),
            To = { message.To },
            Subject = message.Subject,
            BodyEncoding = Encoding.UTF8,
            SubjectEncoding = Encoding.UTF8,
        };

        if (!string.IsNullOrWhiteSpace(message.PlainTextBody))
        {
            var view = AlternateView.CreateAlternateViewFromString(message.PlainTextBody, Encoding.UTF8, "text/plain");
            mail.AlternateViews.Add(view);
        }

        var html = AlternateView.CreateAlternateViewFromString(message.HtmlBody, Encoding.UTF8, "text/html");
        foreach (var image in message.InlineImages ?? [])
        {
            html.LinkedResources.Add(new LinkedResource(new MemoryStream(image.Content), image.ContentType)
            {
                ContentId = image.ContentId, TransferEncoding = TransferEncoding.Base64,
            });
        }
        mail.AlternateViews.Add(html);

        try
        {
            await smtp.SendMailAsync(mail, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (message.TemplateCode is not null)
        {
            logger.LogWarning("Correo {TemplateCode} a {To}: fallo SMTP ({ErrorType})",
                message.TemplateCode, message.To, ex.GetType().Name);
            throw;
        }
        if (message.TemplateCode is { } code)
            logger.LogInformation("Correo {TemplateCode} a {To}: aceptado por SMTP", code, message.To);
        else
            logger.LogInformation("Correo enviado a {To} (asunto: {Subject})", message.To, message.Subject);
    }
}

/// <summary>
/// Provider "Log" (dev): registra metadatos sin cuerpo ni enlaces sensibles.
/// Para revisar el cuerpo usar el generador local de vistas o un capturador SMTP.
/// </summary>
public class LogEmailSender(ILogger<LogEmailSender> logger) : IEmailSender
{
    public Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        if (message.TemplateCode is { } code)
        {
            logger.LogInformation("Correo {TemplateCode} a {To}: simulado", code, message.To);
            return Task.CompletedTask;
        }
        logger.LogInformation(
            "[EMAIL {Provider}] Para: {To} | Asunto: {Subject} (cuerpo omitido: puede contener enlaces de acceso)",
            "LOG", message.To, message.Subject);
        return Task.CompletedTask;
    }
}
