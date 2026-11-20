using System.Text;
using CoppAddresd.Auth.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace CoppAddresd.Auth.Services;

public sealed class PasswordResetEmailTokenOptions : DataProtectionTokenProviderOptions
{
    public PasswordResetEmailTokenOptions()
    {
        Name = PasswordResetEmailTokenProvider.ProviderName;
        TokenLifespan = TimeSpan.FromMinutes(PasswordResetEmailTokenProvider.LifetimeMinutes);
    }
}

public sealed class PasswordResetEmailTokenProvider(
    IDataProtectionProvider dataProtection,
    IOptions<PasswordResetEmailTokenOptions> options,
    ILogger<DataProtectorTokenProvider<ApplicationUser>> logger)
    : DataProtectorTokenProvider<ApplicationUser>(dataProtection, options, logger)
{
    public const string ProviderName = "CoppPasswordResetEmailV1";
    public const int LifetimeMinutes = 60;
}

/// <summary>Emite un enlace para la cuenta. Identity invalida el token al cambiar el sello de seguridad.</summary>
public sealed class PasswordResetEmailLink(UserManager<ApplicationUser> users)
{
    public async Task<string> IssueAsync(ApplicationUser user, string destination)
    {
        if (!user.IsActive || string.IsNullOrWhiteSpace(user.Email))
            throw new ArgumentException("La cuenta debe estar activa y tener correo.", nameof(user));
        var target = TransactionalEmailTemplate.HttpsUrl(destination)
            ?? throw new ArgumentException("Se requiere el destino HTTPS de restablecimiento.", nameof(destination));
        var address = new Uri(target);
        var parameters = QueryHelpers.ParseQuery(address.Query);
        if (parameters.ContainsKey("userId") || parameters.ContainsKey("token"))
            throw new ArgumentException("El destino ya contiene parámetros de restablecimiento.", nameof(destination));

        var token = await users.GeneratePasswordResetTokenAsync(user);
        return QueryHelpers.AddQueryString(target, new Dictionary<string, string?>
        {
            ["userId"] = user.Id.ToString(),
            ["token"] = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token)),
        });
    }
}
