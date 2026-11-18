using System.Net.Mail;
using System.Net.Mime;
using System.Text;
using CoppAddresd.Auth.Configuration;
using CoppAddresd.Auth.Services;

// Genera vistas y MIME locales con datos ficticios. No envía correo ni usa credenciales.
var output = Path.GetFullPath(
    args.FirstOrDefault() ?? Path.Combine(Path.GetTempPath(), "copp-welcome-preview")
);
Directory.CreateDirectory(output);
foreach (
    var (name, app, erp, invite, language) in new[]
    {
        ("erp", false, true, true, "es"),
        ("app", true, false, false, "es"),
        ("both", true, true, false, "es"),
        ("english", false, true, true, "en"),
    }
)
{
    var content = new WelcomeEmailContent(
        "ana@example.invalid",
        "Ana",
        language,
        app,
        erp,
        erp ? ["Professional"] : [],
        erp
            ? [new("Pacientes", ["Consultar", "Editar"]), new("Citas", ["Consultar", "Gestionar"])]
            : [],
        invite ? "https://erp.coppadresd.com/invitaciones?token=PREVIEW-NOT-A-REAL-TOKEN" : null,
        invite ? DateTime.UtcNow.AddHours(72) : null
    );
    var message = WelcomeEmailTemplate.Build(
        content,
        new EmailSettings { FrontendUrl = "https://erp.coppadresd.com" }
    );
    var preview = message.HtmlBody;
    foreach (var image in message.InlineImages ?? [])
        preview = preview.Replace(
            "cid:" + image.ContentId,
            "data:" + image.ContentType + ";base64," + Convert.ToBase64String(image.Content)
        );
    await File.WriteAllTextAsync(Path.Combine(output, name + ".html"), preview);
    using var mail = new MailMessage("no-reply@coppaddresd.com", message.To)
    {
        Subject = message.Subject,
        SubjectEncoding = Encoding.UTF8,
    };
    mail.AlternateViews.Add(
        AlternateView.CreateAlternateViewFromString(
            message.PlainTextBody!,
            Encoding.UTF8,
            "text/plain"
        )
    );
    var html = AlternateView.CreateAlternateViewFromString(
        message.HtmlBody,
        Encoding.UTF8,
        "text/html"
    );
    foreach (var image in message.InlineImages ?? [])
        html.LinkedResources.Add(
            new LinkedResource(new MemoryStream(image.Content), image.ContentType)
            {
                ContentId = image.ContentId,
                TransferEncoding = TransferEncoding.Base64,
            }
        );
    mail.AlternateViews.Add(html);
    var before = Directory.GetFiles(output, "*.eml").ToHashSet();
    using var smtp = new SmtpClient
    {
        DeliveryMethod = SmtpDeliveryMethod.SpecifiedPickupDirectory,
        PickupDirectoryLocation = output,
    };
    await smtp.SendMailAsync(mail);
    var generated = Directory.GetFiles(output, "*.eml").Single(x => !before.Contains(x));
    File.Move(generated, Path.Combine(output, name + ".eml"), overwrite: true);
}
Console.WriteLine(output);
