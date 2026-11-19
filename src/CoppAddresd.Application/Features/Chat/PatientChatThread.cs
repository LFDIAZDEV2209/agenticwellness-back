namespace CoppAddresd.Application.Features.Chat;

public static class PatientChatThread
{
    // Misma clave usada por la app y por las notificaciones proactivas.
    public static string ForUser(Guid userId) => $"proactive-{userId:D}";
}
