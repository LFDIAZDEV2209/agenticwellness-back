using Microsoft.Extensions.Configuration;

namespace CoppAddresd.Infrastructure.Services;

/// <summary>Permite probar el transporte de recordatorios con la cuenta sintética reservada.</summary>
public static class MedicationReminderTestScope
{
    public static readonly Guid PatientId = Guid.Parse("bd0e7dd6-5b05-4fb9-8d09-09d53fe809f5");
    public static readonly Guid UserId = Guid.Parse("f1ea5b76-52c8-47d8-ae59-ab82ad13b2a9");

    public static bool IsDemoOnly(IConfiguration? configuration)
        => configuration?.GetValue<bool>("Medications:DemoRemindersOnly") == true;

    public static bool AppEnabled(IConfiguration? configuration)
        => IsDemoOnly(configuration) || configuration?.GetValue<bool>("Medications:RemindersEnabled") == true;

    public static bool AppEnabledForUser(IConfiguration? configuration, Guid userId)
        => AppEnabled(configuration) && (!IsDemoOnly(configuration) || userId == UserId);
}
