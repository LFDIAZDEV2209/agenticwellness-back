using CoppAddresd.Application.Common;
using CoppAddresd.Application.Features.Medications;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Application.Services.Localization;
using CoppAddresd.Infrastructure.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CoppAddresd.Infrastructure.Services;

public sealed class MedicationAppReminderChannel(IDeviceTokenRepository deviceTokens, IFcmClient fcm,
    IConfiguration configuration, ILogger<MedicationAppReminderChannel> logger) : IMedicationReminderChannel
{
    public string Code => MedicationControlStore.AppReminderChannel;
    public bool Enabled => configuration.GetValue<bool>("Medications:RemindersEnabled");

    public async Task DispatchAsync(MedicationReminderOccurrence occurrence, Guid deliveryId, CancellationToken ct)
    {
        if (occurrence.Preferences?.Push == false) return;
        var tokens = await deviceTokens.GetByUserIdAsync(occurrence.UserId, ct);
        var data = new Dictionary<string, string>
        {
            ["type"] = MedicationControlStore.ReminderType,
            ["screen"] = "medications",
            ["notificationId"] = deliveryId.ToString("D"),
        };
        foreach (var device in tokens)
        {
            try
            {
                var english = PushContentLocalization.Language(device) == "en";
                var response = await fcm.SendAsync(device.Token,
                    english ? "Medication reminder" : MedicationControlStore.ReminderTitle,
                    english ? "You have a pending dose in My medications." : MedicationControlStore.ReminderMessage,
                    data, ct);
                if (response.Status == FcmSendStatus.TokenInvalid)
                    await deviceTokens.UnregisterForUserAsync(occurrence.UserId, device.Token, ct);
                else if (response.Status != FcmSendStatus.Sent)
                    logger.LogWarning("FCM no confirmó recordatorio {DeliveryId}: {Status}", deliveryId, response.Status);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                logger.LogWarning("FCM no confirmado para recordatorio {DeliveryId}: {ErrorType}", deliveryId, ex.GetType().Name);
            }
        }
    }
}
