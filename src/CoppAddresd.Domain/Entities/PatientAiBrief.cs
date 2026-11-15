namespace CoppAddresd.Domain.Entities;

/// <summary>Borrador de IA del paciente; no constituye una prescripción ni una decisión clínica.</summary>
public sealed class PatientAiBrief
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PatientId { get; set; }
    /// <summary>patient_summary o weekly_plan_suggestions; independiente de una inscripción.</summary>
    public string Kind { get; set; } = string.Empty;
    /// <summary>Respuesta estructurada de IA, conservada como borrador en JSON.</summary>
    public string DraftJson { get; set; } = "{}";
    /// <summary>Modelo declarado por el proveedor; null cuando no lo informa.</summary>
    public string? ModelName { get; set; }
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
    /// <summary>SHA-256 hexadecimal minúsculo de los bytes UTF-8 del request exacto enviado a IA.</summary>
    public string InputHash { get; set; } = string.Empty;
    /// <summary>draft, applied o dismissed; la generación solo crea borradores.</summary>
    public string State { get; set; } = "draft";
}
