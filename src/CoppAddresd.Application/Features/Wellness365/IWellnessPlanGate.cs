using CoppAddresd.Domain.Wellness365;

namespace CoppAddresd.Application.Features.Wellness365;

public interface IWellnessPlanGate
{
    // Cerrar el día requiere acceso vigente. Leer un plan exige además la compuerta del cuestionario.
    Task<WellnessBlock?> CheckAsync(Guid patientId, bool includeScreening, CancellationToken ct);
}
