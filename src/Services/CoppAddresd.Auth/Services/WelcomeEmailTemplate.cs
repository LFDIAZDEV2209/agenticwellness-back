using System.Net;
using System.Text;
using CoppAddresd.Auth.Configuration;
using CoppAddresd.Auth.Interfaces;

namespace CoppAddresd.Auth.Services;

public sealed record WelcomeCapability(string Module, IReadOnlyList<string> Actions);

public sealed record WelcomeEmailContent(
    string Email,
    string FirstName,
    string? Language,
    bool AppAccess,
    bool ErpAccess,
    IReadOnlyList<string> Roles,
    IReadOnlyList<WelcomeCapability> Capabilities,
    string? InvitationUrl = null,
    DateTime? InvitationExpiresAt = null,
    bool IsPatientOnboarding = false
);

/// <summary>HTML compatible con correo: tablas, estilos inline, logo HTTPS y alternativa de texto.</summary>
public static class WelcomeEmailTemplate
{
    public static EmailMessage Build(WelcomeEmailContent content, EmailSettings settings)
    {
        var en = content.Language?.StartsWith("en", StringComparison.OrdinalIgnoreCase) == true;
        string T(string es, string english) => en ? english : es;
        var subject = T("Bienvenido a Agentic Wellness IA", "Welcome to Agentic Wellness IA");
        var name = string.IsNullOrWhiteSpace(content.FirstName)
            ? ""
            : ", " + content.FirstName.Trim();
        var intro = content.IsPatientOnboarding
            ? T(
                "Tu registro en Agentic Wellness IA está listo. Sigue estos pasos para entrar a la app.",
                "Your Agentic Wellness IA registration is ready. Follow these steps to sign in to the app."
            )
            : T(
                "Tu cuenta está lista. Aquí tienes tus accesos y los pasos para comenzar.",
                "Your account is ready. Here are your enabled services and how to get started."
            );
        var plain = new StringBuilder($"{subject}{name}\n\n{intro}\n");
        var sections = new StringBuilder();

        if (content.Roles.Count > 0)
        {
            var roles = string.Join(" · ", content.Roles);
            sections.Append(
                $"<p style=\"margin:0 0 30px;padding:15px 18px;background:#f0f5f4;border-radius:10px;font-size:13px;color:#416268\">{E(T("Rol asignado", "Assigned role"))}: <strong>{E(roles)}</strong></p>"
            );
            plain.AppendLine($"{T("Rol asignado", "Assigned role")}: {roles}");
        }

        if (content.InvitationUrl is not null)
        {
            var url =
                ValidUrl(content.InvitationUrl)
                ?? throw new InvalidOperationException("InvalidInvitationUrl");
            sections.Append(
                "<table role=\"presentation\" width=\"100%\" cellspacing=\"0\" cellpadding=\"0\" style=\"margin:0 0 32px\"><tr><td class=\"activation-pad\" bgcolor=\"#eaf4ef\" style=\"padding:26px 28px;border-radius:16px;background:#eaf4ef\">"
            );
            sections.Append(
                Heading(T("Comienza con tu contraseña", "Start by setting your password"))
            );
            var instruction = T(
                "Crea tu contraseña con este enlace personal de un solo uso.",
                "Create your password using this personal, single-use link."
            );
            sections.Append(Paragraph(instruction));
            sections.Append(Button(url, T("Crear mi contraseña", "Set my password")));
            plain.AppendLine($"\n{instruction}\n{url}");
            if (content.InvitationExpiresAt is DateTime expires)
            {
                var expiry =
                    T("Enlace válido hasta", "Link valid until")
                    + " "
                    + expires.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'")
                    + ".";
                sections.Append(
                    $"<p style=\"margin:14px 0 0;font-size:11px;line-height:1.6;color:#416268\">{E(expiry)}</p>"
                );
                plain.AppendLine(expiry);
            }
            sections.Append("</td></tr></table>");
        }

        if (content.ErpAccess)
        {
            var login =
                ValidUrl(settings.ErpLoginUrl ?? settings.FrontendUrl.TrimEnd('/') + "/login")
                ?? throw new InvalidOperationException("InvalidErpLoginUrl");
            sections.Append(Heading(T("Tu espacio de trabajo", "Your workspace")));
            var instruction = T(
                "Accede al ERP con este correo y tu contraseña. Tus permisos se aplican dentro de las clínicas y organizaciones asignadas.",
                "Sign in to the ERP with this email and your password. Your permissions apply within your assigned clinics and organizations."
            );
            sections.Append(Paragraph(instruction));
            plain.AppendLine($"\nERP\n{instruction}\n{login}");
            if (content.Capabilities.Count > 0)
            {
                sections.Append(
                    "<table role=\"presentation\" width=\"100%\" cellspacing=\"0\" cellpadding=\"0\" style=\"border-collapse:separate;border-spacing:0 8px;margin:18px 0 24px\">"
                );
                foreach (var capability in content.Capabilities)
                {
                    var actions = string.Join(" · ", capability.Actions);
                    sections.Append(
                        $"<tr><td style=\"padding:17px 20px;background:#f3f6f8;border-radius:10px\"><strong style=\"font-size:16px;color:#142855\">{E(capability.Module)}</strong><br><span style=\"font-size:13px;line-height:1.9;color:#416268\">{E(actions)}</span></td></tr>"
                    );
                    plain.AppendLine($"{capability.Module}: {actions}");
                }
                sections.Append("</table>");
            }
            else
            {
                var empty = T(
                    "Tu acceso está habilitado. Tu administrador puede asignarte los módulos que necesites.",
                    "Your access is enabled. Your administrator can assign the modules you need."
                );
                sections.Append(Paragraph(empty));
                plain.AppendLine(empty);
            }
            sections.Append(Button(login, T("Entrar al ERP", "Open the ERP")));
        }

        if (content.AppAccess)
        {
            if (content.ErpAccess)
                sections.Append(
                    "<table role=\"presentation\" width=\"100%\" cellspacing=\"0\" cellpadding=\"0\"><tr><td height=\"30\" style=\"height:30px;border-top:1px solid #dfe9e8\"></td></tr></table>"
                );
            sections.Append(
                Heading(T("Agentic Wellness IA, también en tu móvil", "Agentic Wellness IA on your phone"))
            );
            var instruction = T(
                "En la app, ingresa tu número de identificación y confirma el código que recibirás en tu contacto registrado.",
                "In the app, enter your identification number and confirm the code sent to your registered contact."
            );
            sections.Append(Paragraph(instruction));
            plain.AppendLine($"\nApp Agentic Wellness IA\n{instruction}");
            foreach (
                var (platform, configured) in new[]
                {
                    ("iOS", settings.IosDownloadUrl),
                    ("Android", settings.AndroidDownloadUrl),
                }
            )
            {
                var link = ValidUrl(configured);
                if (link is not null)
                {
                    sections.Append(Button(link, T("Descargar para ", "Download for ") + platform));
                    plain.AppendLine($"{platform}: {link}");
                }
                else
                {
                    var label = platform + " · " + T("Próximamente", "Coming soon");
                    sections.Append(
                        $"<p style=\"margin:12px 0;padding:18px 20px;background:#f0f5f4;color:#416268;border:1px dashed #a8c4bc;border-radius:12px;font-size:14px;font-weight:700\">{E(label)}</p>"
                    );
                    plain.AppendLine(label);
                }
            }
        }

        var help = T(
            "¿Necesitas ayuda? Contacta a la persona que creó tu cuenta.",
            "Need help? Contact the person who created your account."
        );
        var unexpected = T(
            "Si no esperabas este correo, puedes ignorarlo. Nunca compartas tu contraseña ni tu enlace de acceso.",
            "If you were not expecting this email, you can ignore it. Never share your password or access link."
        );
        plain.AppendLine($"\n{help}\n{unexpected}");
        var greetingName = string.IsNullOrWhiteSpace(content.FirstName)
            ? ""
            : content.FirstName.Trim();
        var logoUrl = ValidUrl(settings.WelcomeLogoUrl);
        var logoCell = logoUrl is not null && logoUrl.StartsWith("https://", StringComparison.Ordinal)
            ? $"""<td width="48" valign="middle"><img class="mail-logo" src="{E(logoUrl)}" alt="" width="36" height="36" style="display:block;border:0;border-radius:8px;background:#ffffff"></td>"""
            : "";
        var html = $$"""
            <!DOCTYPE html><html lang="{{(
                en ? "en" : "es"
            )}}"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><meta name="color-scheme" content="light"><title>{{subject}}</title>
            <style>@media only screen and (max-width:600px){.mail-pad{padding:28px 24px!important}.hero-pad{padding:34px 24px 38px!important}.mail-heading{font-size:38px!important}.activation-pad{padding:22px 20px!important}.mail-footer{padding:26px 24px!important} }</style></head>
            <body style="margin:0;padding:0;background:#e9efef;font-family:Arial,Helvetica,sans-serif;color:#142855">
            <div style="display:none;max-height:0;overflow:hidden;mso-hide:all">{{E(intro)}}</div>
            <table role="presentation" width="100%" cellspacing="0" cellpadding="0"><tr><td align="center" style="padding:30px 10px">
            <table role="presentation" width="600" cellspacing="0" cellpadding="0" style="width:100%;max-width:600px;table-layout:fixed;background:#ffffff;border-radius:22px;overflow:hidden">
            <tr><td bgcolor="#142855" style="padding:16px 24px;background:#142855">
            <table role="presentation" cellspacing="0" cellpadding="0"><tr>
            {{logoCell}}
            <td class="brand-name" style="font-size:16px;font-weight:700;letter-spacing:1.2px;color:#ffffff;vertical-align:middle">Agentic Wellness IA</td>
            </tr></table></td></tr>
            <tr><td class="hero-pad" align="center" bgcolor="#edf6f3" style="padding:42px 38px;background:#edf6f3;color:#142855">
            <h1 class="mail-heading" style="margin:0 0 14px;font-family:Arial,Helvetica,sans-serif;font-size:46px;line-height:1.12;font-weight:700;letter-spacing:-1.3px;color:#142855;overflow-wrap:anywhere;word-break:break-word">{{E(
                T("Hola", "Hello")
            )}}{{(greetingName.Length > 0 ? ", " + E(greetingName) + "." : ".")}}</h1>
            <p style="margin:0;font-size:16px;line-height:1.65;color:#415b6d">{{E(intro)}}</p>
            </td></tr>
            <tr><td class="mail-pad" style="padding:30px 42px 40px;font-size:15px;line-height:1.65">{{sections}}</td></tr>
            <tr><td class="mail-footer" bgcolor="#142855" style="padding:30px 42px;background:#142855;color:#ffffff">
            <p style="margin:0;font-family:Georgia,'Times New Roman',serif;font-size:25px;line-height:1.3;color:#ffffff">{{E(
                T("Estamos para ayudarte.", "We are here to help.")
            )}}</p>
            <p style="margin:14px 0 0;font-size:13px;line-height:1.7;color:#dcece9">{{E(help)}}</p>
            <p style="margin:20px 0 0;padding-top:18px;border-top:1px solid #405474;font-size:11px;line-height:1.7;color:#c0d2d8">{{E(
                unexpected
            )}}</p>
            </td></tr>
            <tr><td style="padding:20px 24px;text-align:center;font-size:11px;letter-spacing:1px;color:#52677b">Agentic Wellness IA</td></tr>
            </table></td></tr></table></body></html>
            """;
        return new EmailMessage(
            content.Email,
            subject,
            html,
            plain.ToString()
        );
    }

    public static string? ValidUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == "https" || uri.Scheme == "http" && uri.IsLoopback)
        && string.IsNullOrEmpty(uri.UserInfo)
            ? uri.AbsoluteUri
            : null;

    private static string E(string value) => WebUtility.HtmlEncode(value);

    private static string Heading(string value) =>
        $"<h2 style=\"margin:0 0 14px;font-size:23px;line-height:1.35;letter-spacing:-0.4px;color:#142855\">{E(value)}</h2>";

    private static string Paragraph(string value) =>
        $"<p style=\"margin:0 0 18px;font-size:15px;line-height:1.7;color:#52677b\">{E(value)}</p>";

    private static string Button(string url, string label) =>
        $"<table role=\"presentation\" cellspacing=\"0\" cellpadding=\"0\" style=\"margin:16px 0\"><tr><td bgcolor=\"#035d4d\" style=\"border-radius:28px;background:#035d4d;mso-padding-alt:16px 26px\"><a href=\"{E(url)}\" style=\"display:inline-block;padding:16px 26px;color:#ffffff;font-size:15px;font-weight:700;text-decoration:none;border:1px solid #035d4d;border-radius:28px\">{E(label)}</a></td></tr></table>";
}
