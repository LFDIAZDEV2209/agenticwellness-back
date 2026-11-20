using System.Text.Encodings.Web;

namespace CoppAddresd.Auth.Services;

/// <summary>
/// Plantilla compartida de los correos de código (activación y recuperación).
/// Tablas + estilos inline para clientes de correo; sin fuentes ni imágenes externas.
/// </summary>
internal static class AuthEmailLayout
{
    private const string Navy = "#142855";
    private const string NavyDeep = "#023467";
    private const string Green = "#035d4d";
    private const string GreenSoft = "#3d7b72";
    private const string Cream = "#f7f4ee";
    private const string Mist = "#eef3f1";
    private const string Ink = "#1c2b45";
    private const string Muted = "#5b6b82";

    public static string Build(
        string locale,
        string heading,
        string intro,
        string code,
        int minutes,
        string footer
    )
    {
        var isEn = locale == "en";
        var safeHeading = HtmlEncoder.Default.Encode(heading);
        var safeIntro = HtmlEncoder.Default.Encode(intro);
        var safeCode = HtmlEncoder.Default.Encode(code);
        var safeFooter = HtmlEncoder.Default.Encode(footer);
        var expiry = isEn ? $"Expires in {minutes} minutes." : $"Vence en {minutes} minutos.";
        var security = isEn
            ? "If you did not request this code, ignore this email. Nobody from Copp Adresd will ask you for it."
            : "Si no solicitaste este código, ignora este correo. Nadie de Copp Adresd te lo va a pedir.";
        var label = isEn ? "YOUR VERIFICATION CODE" : "TU CÓDIGO DE VERIFICACIÓN";
        var preheader = isEn
            ? $"Your Copp Adresd code is {code}. It expires in {minutes} minutes."
            : $"Tu código de Copp Adresd es {code}. Vence en {minutes} minutos.";
        var tagline = isEn ? "Prevention · Wellbeing · Health" : "Prevención · Bienestar · Salud";
        var brand = isEn ? "COPP ADRESD" : "COPP ADRESD";

        return $"""
<!doctype html>
<html lang="{locale}">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<meta name="color-scheme" content="light">
<title>{safeHeading}</title>
</head>
<body style="margin:0;padding:0;background:{Mist};">
<div style="display:none;max-height:0;overflow:hidden;opacity:0;color:transparent;">{HtmlEncoder.Default.Encode(
                preheader
            )}</div>
<table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="background:{Mist};padding:32px 12px;">
  <tr>
    <td align="center">
      <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="max-width:560px;background:#ffffff;border-radius:22px;overflow:hidden;box-shadow:0 18px 50px rgba(20,40,85,0.14);">
        <tr>
          <td style="background:{Navy};padding:26px 32px 24px;">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0">
              <tr>
                <td style="font-family:'Helvetica Neue',Helvetica,Arial,sans-serif;font-size:12px;letter-spacing:3.5px;font-weight:700;color:#ffffff;">{brand}</td>
                <td align="right" style="font-family:'Helvetica Neue',Helvetica,Arial,sans-serif;font-size:11px;letter-spacing:1.4px;color:#a9c6bf;">VITA</td>
              </tr>
            </table>
          </td>
        </tr>
        <tr><td style="height:4px;background:{GreenSoft};font-size:0;line-height:0;">&nbsp;</td></tr>
        <tr>
          <td style="padding:36px 34px 10px;">
            <h1 style="margin:0 0 10px;font-family:Georgia,'Times New Roman',serif;font-size:27px;line-height:1.2;font-weight:700;color:{Navy};">{safeHeading}</h1>
            <p style="margin:0;font-family:'Helvetica Neue',Helvetica,Arial,sans-serif;font-size:15px;line-height:1.6;color:{Ink};">{safeIntro}</p>
          </td>
        </tr>
        <tr>
          <td style="padding:26px 34px 6px;">
            <div style="font-family:'Helvetica Neue',Helvetica,Arial,sans-serif;font-size:10.5px;letter-spacing:2.2px;font-weight:700;color:{GreenSoft};padding-bottom:10px;">{label}</div>
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0">
              <tr>
                <td align="center" style="background:{Cream};border:1.5px dashed #c9d8d3;border-radius:16px;padding:22px 12px;">
                  <div style="font-family:'SFMono-Regular',Consolas,'Liberation Mono',Menlo,monospace;font-size:36px;line-height:1;font-weight:700;letter-spacing:11px;color:{NavyDeep};"><strong>{safeCode}</strong></div>
                </td>
              </tr>
            </table>
            <p style="margin:14px 0 0;text-align:center;font-family:'Helvetica Neue',Helvetica,Arial,sans-serif;font-size:12.5px;letter-spacing:0.2px;color:{Muted};">{expiry}</p>
          </td>
        </tr>
        <tr>
          <td style="padding:22px 34px 0;">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="background:{Mist};border-radius:14px;">
              <tr>
                <td style="padding:14px 16px;font-family:'Helvetica Neue',Helvetica,Arial,sans-serif;font-size:12.5px;line-height:1.55;color:{Muted};">
                  <strong style="color:{Green};">{HtmlEncoder.Default.Encode(
                isEn ? "Security" : "Seguridad"
            )}</strong><br>{HtmlEncoder.Default.Encode(security)}
                </td>
              </tr>
            </table>
          </td>
        </tr>
        <tr>
          <td style="padding:24px 34px 34px;">
            <p style="margin:0;font-family:'Helvetica Neue',Helvetica,Arial,sans-serif;font-size:12px;line-height:1.6;color:{Muted};">{safeFooter}</p>
          </td>
        </tr>
        <tr>
          <td style="background:{NavyDeep};padding:18px 34px;">
            <p style="margin:0;font-family:'Helvetica Neue',Helvetica,Arial,sans-serif;font-size:10.5px;letter-spacing:1.8px;color:#a9c6bf;">{tagline}</p>
            <p style="margin:4px 0 0;font-family:'Helvetica Neue',Helvetica,Arial,sans-serif;font-size:10.5px;color:#7f9bb4;">© {DateTime.UtcNow.Year} Copp Adresd</p>
          </td>
        </tr>
      </table>
    </td>
  </tr>
</table>
</body>
</html>
""";
    }
}
