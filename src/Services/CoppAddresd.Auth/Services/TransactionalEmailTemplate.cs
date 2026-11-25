using System.Globalization;
using System.Net.Mail;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using CoppAddresd.Auth.Configuration;
using CoppAddresd.Auth.Interfaces;

namespace CoppAddresd.Auth.Services;

public sealed record TransactionalEmailContent(
    string Code,
    string Recipient,
    string? Language,
    IReadOnlyDictionary<string, string?> Variables);

/// <summary>Renderiza el catálogo transaccional v1 en HTML y texto plano a partir del mismo contenido.</summary>
public static partial class TransactionalEmailTemplate
{
    internal const string PaidActivationDemoCode = "PAID-ACTIVATION-DEMO";
    private static readonly Lazy<Dictionary<string, Template>> Templates = new(LoadTemplates);

    public static EmailMessage Build(TransactionalEmailContent content, EmailSettings settings)
    {
        if (!Templates.Value.TryGetValue(content.Code, out var template))
            throw new ArgumentException("Código de plantilla transaccional desconocido.", nameof(content));

        var english = content.Language?.Split('-', '_')[0].Equals("en", StringComparison.OrdinalIgnoreCase) == true;
        var copy = english ? template.En : template.Es;
        var brand = settings.Transactional;
        var recipient = Address(content.Recipient);
        var values = content.Variables;
        var copyUsesPaymentEmail = content.Code is "BIEN-01" or "ACT-01" or "ACT-02" or "PWD-01";
        var paymentEmail = copyUsesPaymentEmail
            ? Address(values.GetValueOrDefault("correo_pago") ?? recipient)
            : recipient;
        if (!string.Equals(paymentEmail, recipient, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("El destinatario debe ser el correo de acceso de la cuenta.", nameof(content));

        var support = ShortText(values.GetValueOrDefault("soporte_whatsapp"), 40)
            ?? ShortText(brand.SupportWhatsApp, 40);
        var version = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["marca"] = ShortText(EmailSettings.ResolveBrandName(brand.Name), 64) ?? EmailSettings.DefaultBrandName,
            ["nombre"] = ShortText(values.GetValueOrDefault("nombre"), 80) ?? (english ? "there" : "paciente"),
            ["correo_pago"] = paymentEmail,
            ["soporte_whatsapp"] = support ?? "",
            ["soporte"] = english
                ? support is null ? "Reply to this email and we’ll help." : $"Reply to this email or contact us on WhatsApp at {support}."
                : support is null ? "Responde a este correo y te ayudamos." : $"Responde a este correo o escríbenos por WhatsApp al {support}.",
            ["resumen_dia"] = ShortText(values.GetValueOrDefault("resumen_dia"), 200)
                ?? (english ? "See today’s menu in the app." : "Consulta el menú de hoy en la app."),
            ["fecha"] = ShortText(values.GetValueOrDefault("fecha"), 60) ?? (english ? "See the app" : "Consulta la app"),
            ["duracion"] = ShortText(values.GetValueOrDefault("duracion"), 60) ?? (english ? "See the app" : "Consulta la app"),
            ["vigencia_enlace"] = $"{PasswordResetEmailTokenProvider.LifetimeMinutes} {(english ? "minutes" : "minutos")}",
        };
        if (content.Code == "INV-01")
        {
            version["medicamento"] = ShortText(values.GetValueOrDefault("medicamento"), 100)
                ?? throw new ArgumentException("Falta medicamento en INV-01.", nameof(content));
            version["cantidad"] = StockCount(values, "cantidad", english, allowDecimal: true);
            version["dias_restantes"] = StockCount(values, "dias_restantes", english);
        }

        var isDemo = content.Code == PaidActivationDemoCode;
        var link = ActionUrl(values.GetValueOrDefault(template.Link), loopbackOnly: isDemo)
            ?? throw new ArgumentException(isDemo
                ? $"Falta una URL local de prueba válida para {template.Link}."
                : $"Falta una URL HTTPS válida para {template.Link}.", nameof(content));
        var logo = HttpsUrl(brand.LogoUrl ?? settings.WelcomeLogoUrl);
        var accent = HexColor().IsMatch(brand.AccentColor ?? "") ? brand.AccentColor! : "#075B50";

        var languages = brand.Templates.GetValueOrDefault(content.Code);
        var overrides = english ? languages?.En : languages?.Es;
        var allowed = new[] { copy.Subject, copy.Preheader, copy.Title, copy.Button, copy.Intro, copy.Callout, copy.Closing }
            .Concat(copy.Items ?? []).SelectMany(field => Variable().Matches(field).Select(match => match.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);
        allowed.Add("marca");
        if (allowed.Contains("soporte")) allowed.Add("soporte_whatsapp");
        string Render(string text) => Variable().Replace(text, match =>
        {
            var key = match.Groups[1].Value;
            return allowed.Contains(key) ? version[key] : throw new InvalidOperationException("Variable fuera del contrato de la plantilla.");
        });
        var rendered = new Copy(
            Render(ShortText(overrides?.Subject, 200) ?? copy.Subject),
            Render(ShortText(overrides?.Preheader, 300) ?? copy.Preheader),
            Render(ShortText(overrides?.Title, 160) ?? copy.Title),
            Render(ShortText(overrides?.Button, 60) ?? copy.Button),
            Render(copy.Intro),
            Render(ShortText(overrides?.Callout, 500) ?? copy.Callout),
            Render(copy.Closing),
            copy.Items?.Select(Render).ToArray(),
            copy.Numbered);

        return new EmailMessage(recipient, rendered.Subject,
            Html(rendered, link, version["marca"], logo, accent, english ? "en" : "es"),
            Plain(rendered, link))
        {
            TemplateCode = content.Code,
        };
    }

    internal static string? HttpsUrl(string? value) => ActionUrl(value, loopbackOnly: false);

    private static string? ActionUrl(string? value, bool loopbackOnly) =>
        value is { Length: > 0 and <= 8192 }
        && !value.Any(char.IsWhiteSpace)
        && !value.Any(char.IsControl)
        && Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Host.Length > 0 && uri.UserInfo.Length == 0
        && (loopbackOnly ? uri.IsLoopback && (uri.Scheme is "http" or "https") : uri.Scheme == Uri.UriSchemeHttps)
            ? uri.AbsoluteUri
            : null;

    private static string Address(string value)
    {
        if (value.Any(char.IsControl) || !MailAddress.TryCreate(value, out var address)
            || !string.Equals(address.Address, value.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Dirección de correo no válida.", nameof(value));
        return address.Address;
    }

    private static string? ShortText(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var clean = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        clean = string.Concat(clean.Where(c => !char.IsControl(c)));
        if (clean.Length == 0) return null;
        var elements = StringInfo.ParseCombiningCharacters(clean);
        return elements.Length <= max ? clean : clean[..elements[max - 1]].TrimEnd() + "…";
    }

    private static string StockCount(IReadOnlyDictionary<string, string?> values, string key, bool english,
        bool allowDecimal = false)
    {
        var format = allowDecimal ? NumberStyles.AllowDecimalPoint : NumberStyles.None;
        if (!decimal.TryParse(values.GetValueOrDefault(key), format, CultureInfo.InvariantCulture, out var count)
            || count is < 0 or > 1_000_000)
            throw new ArgumentException($"Valor de inventario no válido para {key}.", nameof(values));
        return count.ToString("0.############################", CultureInfo.GetCultureInfo(english ? "en-US" : "es-CO"));
    }

    private static string Plain(Copy copy, string link)
    {
        var text = new StringBuilder();
        text.AppendLine(copy.Title).AppendLine().AppendLine(copy.Intro);
        if (copy.Items is { Length: > 0 })
        {
            text.AppendLine();
            for (var i = 0; i < copy.Items.Length; i++)
                text.AppendLine($"{(copy.Numbered ? $"{i + 1}." : "•")} {copy.Items[i]}");
        }
        return text.AppendLine().AppendLine(copy.Button).AppendLine(link).AppendLine()
            .AppendLine(copy.Callout).AppendLine().AppendLine(copy.Closing).ToString();
    }

    private static string Html(Copy copy, string link, string brand, string? logo, string accent, string language)
    {
        static string Encode(string value) => HtmlEncoder.Default.Encode(value);
        var brandLogo = logo is null ? ""
            : $"""<td width="42" valign="middle"><img src="{Encode(logo)}" width="32" height="32" alt="" style="display:block;border:0"></td>""";
        var list = new StringBuilder();
        if (copy.Items is { Length: > 0 })
        {
            var listTag = copy.Numbered ? "ol" : "ul";
            list.Append($"""<{listTag} style="margin:0 0 24px;padding-left:24px">""");
            foreach (var item in copy.Items)
                list.Append($"""<li style="margin:0 0 8px">{Encode(item)}</li>""");
            list.Append($"</{listTag}>");
        }
        return $$"""
            <!doctype html>
            <html lang="{{language}}">
            <head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>{{Encode(copy.Subject)}}</title></head>
            <body style="margin:0;padding:0;background:#eaf0f1;color:#142855;font-family:Arial,Helvetica,sans-serif">
              <div style="display:none;font-size:1px;line-height:1px;max-height:0;max-width:0;opacity:0;overflow:hidden;mso-hide:all">{{Encode(copy.Preheader)}}</div>
              <table role="presentation" width="100%" cellspacing="0" cellpadding="0" bgcolor="#eaf0f1"><tr><td align="center" style="padding:20px 10px">
                <!--[if mso]><table role="presentation" width="600" cellspacing="0" cellpadding="0"><tr><td><![endif]-->
                <table role="presentation" width="100%" cellspacing="0" cellpadding="0" bgcolor="#ffffff" style="width:100%;max-width:600px;table-layout:fixed">
                  <tr><td bgcolor="#ffffff" style="padding:14px 24px;background:#ffffff;border-top:4px solid #142855">
                    <table role="presentation" cellspacing="0" cellpadding="0"><tr>{{brandLogo}}<td valign="middle" style="color:#142855;font-size:16px;font-weight:bold">{{Encode(brand)}}</td></tr></table>
                  </td></tr>
                  <tr><td bgcolor="#eaf4ef" style="padding:32px 24px;background:#eaf4ef;overflow-wrap:anywhere">
                    <h1 style="margin:0;color:#142855;font-size:32px;line-height:1.16">{{Encode(copy.Title)}}</h1>
                  </td></tr>
                  <tr><td style="padding:26px 24px 30px;color:#293f55;font-size:16px;line-height:1.55;overflow-wrap:anywhere;word-wrap:break-word">
                    <p style="margin:0 0 18px">{{Encode(copy.Intro)}}</p>
                    {{list}}
                    <table role="presentation" cellspacing="0" cellpadding="0" style="margin:0 0 24px"><tr><td bgcolor="{{accent}}" style="background:{{accent}};border-radius:9px;mso-padding-alt:13px 20px">
                      <a href="{{Encode(link)}}" style="display:inline-block;padding:13px 20px;color:#ffffff;font-weight:bold;text-decoration:none;mso-padding-alt:0">{{Encode(copy.Button)}}</a>
                    </td></tr></table>
                    <table role="presentation" width="100%" cellspacing="0" cellpadding="0" bgcolor="#edf5f2" style="margin:0 0 18px;background:#edf5f2"><tr><td style="padding:16px 18px;border-left:3px solid {{accent}}">{{Encode(copy.Callout)}}</td></tr></table>
                    <p style="margin:0">{{Encode(copy.Closing)}}</p>
                  </td></tr>
                </table>
                <!--[if mso]></td></tr></table><![endif]-->
              </td></tr></table>
            </body>
            </html>
            """;
    }

    private static Dictionary<string, Template> LoadTemplates()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("CoppAddresd.Auth.Email.transactional-v1.json")
            ?? throw new InvalidOperationException("No se encontró el catálogo de correo v1.");
        return JsonSerializer.Deserialize<Dictionary<string, Template>>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("El catálogo de correo v1 no es válido.");
    }

    [GeneratedRegex(@"\{\{([a-z_]+)\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex Variable();

    [GeneratedRegex(@"\A#[0-9a-fA-F]{6}\z", RegexOptions.CultureInvariant)]
    private static partial Regex HexColor();

    private sealed record Template(string Link, Copy Es, Copy En);
    private sealed record Copy(string Subject, string Preheader, string Title, string Button, string Intro,
        string Callout, string Closing, string[]? Items = null, bool Numbered = false);
}
