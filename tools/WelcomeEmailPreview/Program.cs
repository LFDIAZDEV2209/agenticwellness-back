using System.Net.Mail;
using System.Net.Mime;
using System.Text;
using CoppAddresd.Auth.Configuration;
using CoppAddresd.Auth.Interfaces;
using CoppAddresd.Auth.Services;

// Genera vistas y MIME locales con datos ficticios. No envía correo ni usa credenciales.
var transactional = args.Contains("--transactional", StringComparer.Ordinal);
var output = Path.GetFullPath(args.FirstOrDefault(arg => arg != "--transactional")
    ?? Path.Combine(Path.GetTempPath(), transactional ? "copp-transactional-preview" : "copp-welcome-preview"));
Directory.CreateDirectory(output);
if (transactional)
{
    const string origin = "https://preview.example.invalid";
    var values = new Dictionary<string, string?>
    {
        ["nombre"] = "Ana",
        ["correo_pago"] = "ana@example.invalid",
        ["enlace_descarga"] = origin + "/download",
        ["enlace_plataforma"] = origin + "/platform",
        ["enlace_restablecer"] = origin + "/reset?token=QA-PREVIEW",
        ["enlace_nutricion"] = origin + "/nutrition",
        ["enlace_inventario"] = origin + "/inventory",
        ["medicamento"] = "Producto de muestra",
        ["cantidad"] = "12",
        ["dias_restantes"] = "6",
    };
    foreach (var language in new[] { "es", "en" })
    {
        var english = language == "en";
        values["resumen_dia"] = english ? "Oatmeal for breakfast, grilled vegetables for lunch." : "Avena en el desayuno y verduras a la parrilla en el almuerzo.";
        values["fecha"] = english ? "November 20, 2026" : "20 de noviembre de 2026";
        values["duracion"] = english ? "4 weeks" : "4 semanas";
        foreach (var code in new[] { "BIEN-01", "ACT-01", "ACT-02", "PWD-01", "NUT-01", "NUT-02", "INV-01" })
        {
            var message = TransactionalEmailTemplate.Build(new(code, "ana@example.invalid", language, values), new EmailSettings());
            await WritePreview($"{code}-{language}", message, output);
        }
        values["enlace_activacion"] = $"http://127.0.0.1:5196/{(english ? "activate-membership" : "activar-membresia")}#paymentId=preview&token=NOT-A-REAL-TOKEN&lang={language}";
        const string demoCode = "PAID-ACTIVATION-DEMO";
        await WritePreview($"{demoCode}-{language}",
            TransactionalEmailTemplate.Build(new(demoCode, "ana@example.invalid", language, values), new()), output);
    }
    Console.WriteLine(output);
    return;
}
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
    await WritePreview(name, message, output);
}
Console.WriteLine(output);

static async Task WritePreview(string name, EmailMessage message, string output)
{
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
