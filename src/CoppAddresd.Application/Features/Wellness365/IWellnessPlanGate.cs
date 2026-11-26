using CoppAddresd.Domain.Wellness365;

namespace CoppAddresd.Application.Features.Wellness365;

public interface IWellnessPlanGate
{
    // El adaptador de pacientes con membresía exige acceso vigente. MotorPeople aplica la compuerta del plan.
    Task<WellnessBlock?> CheckAsync(Guid patientId, CancellationToken ct);
}
