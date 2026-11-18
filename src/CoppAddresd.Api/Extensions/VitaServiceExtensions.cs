using CoppAddresd.Application.Features.Agents.Vita;
using CoppAddresd.Infrastructure.Repositories;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Api.Security;

namespace CoppAddresd.Api.Extensions;

public static class VitaServiceExtensions
{
    public static IServiceCollection AddVita(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<VitaPatientAccess>();
        services.AddScoped<IVitaAgentCatalogReader, VitaAgentCatalogReader>();
        services.AddScoped<IVitaClinicalReader, VitaClinicalReader>();
        services.AddOptions<VitaTaskProtectionSettings>().Bind(configuration.GetSection("Vita"))
            .Validate(o => !o.TaskCompletionEnabled || VitaProposalTokens.DedicatedSecret(o.TaskProposalSecret,
                configuration["Jwt:Secret"], configuration["AiService:InternalApiKey"], configuration["Vita:VoiceServiceKey"]),
                "Las acciones Vita requieren TaskProposalSecret compartido, Base64 de 32 bytes, independiente de JWT/AI/voz.")
            .ValidateOnStart();
        services.AddScoped<IVitaProposalTokens>(sp => new VitaProposalTokens(sp.GetRequiredService<
            Microsoft.Extensions.Options.IOptionsMonitor<VitaTaskProtectionSettings>>().CurrentValue.TaskProposalSecret));
        services.AddOptions<VitaCaptureSettings>().Bind(configuration.GetSection("Vita"))
            .Validate(o => !o.CaptureEnabled || VitaProposalTokens.DedicatedSecret(o.CaptureProposalSecret,
                configuration["Jwt:Secret"], configuration["AiService:InternalApiKey"], configuration["Vita:VoiceServiceKey"],
                configuration["Vita:TaskProposalSecret"]),
                "La captura Vita requiere CaptureProposalSecret dedicado de 32 bytes en Base64.")
            .ValidateOnStart();
        services.AddScoped<IVitaCaptureProposalTokens>(sp => VitaProposalTokens.ForCapture(sp.GetRequiredService<
            Microsoft.Extensions.Options.IOptionsMonitor<VitaCaptureSettings>>().CurrentValue.CaptureProposalSecret));
        services.AddScoped<IVitaCaptureStore, VitaCaptureStore>();
        services.AddScoped<MedicationControlStore>();
        services.AddScoped<IVitaTaskWriter>(sp => (ProgramRepository)sp.GetRequiredService<IProgramRepository>());
        services.AddHttpClient("VitaVoice", (sp, client) =>
        {
            var settings = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<CoppAddresd.Application.Common.AiServiceSettings>>().Value;
            client.BaseAddress = ApplicationServiceExtensions.ResolveBaseUri(settings.BaseUrl, "http://localhost:8000");
            client.Timeout = TimeSpan.FromSeconds(90);
        }).RedactLoggedHeaders(_ => true);
        return services;
    }
}
