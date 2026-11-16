using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace CoppAddresd.Api.Authorization;

/// <summary>Clave dedicada del transporte Custom LLM; jamás acepta JWT de paciente o clave interna.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class RequireVitaVoiceKeyAttribute : Attribute, IAuthorizationFilter
{
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var config = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        if (!config.GetValue<bool>("Vita:VoiceProxyEnabled")) { context.Result = new NotFoundResult(); return; }
        var expected = config["Vita:VoiceServiceKey"];
        var header = context.HttpContext.Request.Headers.Authorization;
        if (string.IsNullOrWhiteSpace(expected) || expected.Length < 32 || header.Count != 1
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes("Bearer " + expected),
                Encoding.UTF8.GetBytes(header.ToString()))) context.Result = new UnauthorizedResult();
    }
}
