namespace CoppAddresd.Application.Interfaces;

/// <summary>Intenta proyectar las mediciones de una batería ya confirmada.</summary>
public interface IInitialBatteryMeasurementSync
{
    Task SyncAsync(Guid patientId, Guid batteryAssignmentId, CancellationToken ct);
}
