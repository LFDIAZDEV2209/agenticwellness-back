using System.Globalization;
using CoppAddresd.Application.Features.Medications;
using CoppAddresd.Application.Features.Patients;
using CoppAddresd.Application.WhatsApp.Messaging;
using CoppAddresd.WhatsApp.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CoppAddresd.WhatsApp.Messaging;

public sealed class MedicationWhatsAppChannel(WhatsAppDbContext db, IWhatsAppMessageSender sender,
    ISender mediator, IConfiguration configuration, ILogger<MedicationWhatsAppChannel> logger) : IMedicationReminderChannel
{
    public string Code => "whatsapp";
    public bool Enabled => configuration.GetValue<bool>("Medications:WhatsAppRemindersEnabled")
        && !string.IsNullOrWhiteSpace(configuration["Medications:WhatsAppTemplate"]);

    internal static bool IsDoseReply(string? payload)
    {
        if (payload is null || payload.Length > 64 || !payload.StartsWith("med-dose:", StringComparison.Ordinal)) return false;
        var parts = payload.Split(':');
        return parts.Length == 3 && parts[1] is "taken" or "omitted" or "snooze" && Guid.TryParseExact(parts[2], "N", out _);
    }

    public async Task DispatchAsync(MedicationReminderOccurrence occurrence, Guid deliveryId, CancellationToken ct)
    {
        var template = configuration["Medications:WhatsAppTemplate"];
        if (!Enabled || occurrence.Preferences?.WhatsApp != true || template is null) return;
        var link = await db.PatientLinks.AsNoTracking().SingleOrDefaultAsync(
            l => l.PatientId == occurrence.PatientId && l.UserId == occurrence.UserId && !l.OptOut, ct);
        if (link is null || !await mediator.Send(
            new VerifyPatientChannelLinkQuery(occurrence.PatientId, occurrence.UserId), ct)) return;
        var english = link.Language == "en";
        var zone = MedicationControlService.Zone(occurrence.Dose.TimeZone);
        var local = TimeZoneInfo.ConvertTimeFromUtc(occurrence.Dose.ScheduledAt, zone);
        var culture = CultureInfo.GetCultureInfo(english ? "en-US" : "es-CO");
        var quantity = occurrence.Dose.Dosage.ToString("G29", culture);
        var instructions = Clean(occurrence.Instructions, english ? "See your plan in the app" : "Consulta tu plan en la app");
        var result = await sender.SendQuickReplyTemplateAsync(link.WaId, template, english ? "en_US" : "es",
            [Clean(link.FirstName, english ? "there" : "paciente"),
             Clean($"{occurrence.Dose.Name}, {quantity} {occurrence.Dose.Unit}", "Agentic Wellness IA"),
             local.ToString("HH:mm", culture), instructions],
            [$"med-dose:taken:{deliveryId:N}", $"med-dose:snooze:{deliveryId:N}", $"med-dose:omitted:{deliveryId:N}"], ct);
        if (!result.Succeeded)
            logger.LogWarning("WhatsApp no confirmó el recordatorio de medicación {DeliveryId}: {ErrorCode}", deliveryId, result.ErrorCode);
    }

    private static string Clean(string? value, string fallback)
    {
        var text = string.Join(' ', (value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length == 0 ? fallback : text.Length <= 300 ? text : text[..300];
    }
}
