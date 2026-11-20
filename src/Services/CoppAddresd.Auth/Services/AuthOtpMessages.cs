using CoppAddresd.Auth.Interfaces;

namespace CoppAddresd.Auth.Services;

/// <summary>Idioma acotado de los mensajes OTP; los clientes anteriores conservan español.</summary>
public static class AuthMessageLanguage
{
    public static string Normalize(string? language)
    {
        var value = language?.Trim();
        return
            string.Equals(value, "en", StringComparison.OrdinalIgnoreCase)
            || value?.StartsWith("en-", StringComparison.OrdinalIgnoreCase) == true
            ? "en"
            : "es";
    }
}

/// <summary>Texto transaccional de activación y recuperación, separado de los códigos y su validación.</summary>
public static class AuthOtpMessages
{
    public static EmailMessage Activation(
        string to,
        string firstName,
        string code,
        int lifetimeSeconds,
        string? language
    )
    {
        var locale = AuthMessageLanguage.Normalize(language);
        var minutes = lifetimeSeconds / 60;
        var heading = locale == "en" ? "Confirm your identity" : "Confirma tu identidad";
        var intro =
            locale == "en"
                ? $"Hello {firstName}, use this single-use code to sign in to Copp Adresd."
                : $"Hola {firstName}, usa este código de un solo uso para entrar a Copp Adresd.";
        var footer =
            locale == "en"
                ? "This code is personal and single-use. If you open the app from another device, request a new one."
                : "Este código es personal y de un solo uso. Si abres la app desde otro dispositivo, solicita uno nuevo.";
        var html = AuthEmailLayout.Build(locale, heading, intro, code, minutes, footer);
        return locale == "en"
            ? new EmailMessage(
                to,
                "Your CoppAddresd verification code",
                html,
                $"Hello {firstName},\n\nYour verification code is {code}.\n\nIt expires in {minutes} minutes. If you did not request this code, ignore this email."
            )
            : new EmailMessage(
                to,
                "Tu código de verificación de CoppAddresd",
                html,
                $"Hola {firstName},\n\nTu código de verificación es {code}.\n\nVence en {minutes} minutos. Si no solicitaste este código, ignora este correo."
            );
    }

    public static EmailMessage Recovery(
        string to,
        string code,
        int lifetimeSeconds,
        string? language
    )
    {
        var locale = AuthMessageLanguage.Normalize(language);
        var minutes = lifetimeSeconds / 60;
        var heading = locale == "en" ? "Reset your password" : "Recupera tu contraseña";
        var intro =
            locale == "en"
                ? "Use this code to create a new password for your Copp Adresd account."
                : "Usa este código para crear una nueva contraseña de tu cuenta Copp Adresd.";
        var footer =
            locale == "en"
                ? "Choose a password with uppercase, lowercase, a number and a symbol. Never share this code."
                : "Elige una contraseña con mayúscula, minúscula, número y símbolo. Nunca compartas este código.";
        var html = AuthEmailLayout.Build(locale, heading, intro, code, minutes, footer);
        return locale == "en"
            ? new EmailMessage(
                to,
                "Reset your CoppAddresd password",
                html,
                $"Your password reset code: {code}. It expires in {minutes} minutes. If you did not request this, ignore this email."
            )
            : new EmailMessage(
                to,
                "Recupera tu contraseña de CoppAddresd",
                html,
                $"Código para cambiar tu contraseña: {code}. Vence en {minutes} minutos. Si no lo solicitaste, ignora este correo."
            );
    }
}
