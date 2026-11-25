namespace CoppAddresd.Domain.Wellness365;

/// <summary>Perfil del motor anclado al paciente canónico.</summary>
public sealed class Motor365Person
{
    public Guid PatientId { get; init; }
    public WellnessSex Sex { get; set; }
    public int BirthYear { get; set; }
    public decimal HeightCm { get; set; }
    public decimal InitialWeightKg { get; set; }
    public decimal InitialWaistCm { get; set; }
    public decimal? TargetWeightKg { get; set; }
    public int ActivityLevel { get; set; }
    public WellnessGoal Goal { get; set; }
    public WellnessPace Pace { get; set; }
    public bool Vegetarian { get; set; }
    public bool LactoseFree { get; set; }
    public int FitnessLevel { get; set; }
    public DateOnly StartDate { get; set; }
    public string EngineVer { get; set; } = WellnessEngine.ReferenceVersion;
    public int PlanVersion { get; set; } = 1;
}
