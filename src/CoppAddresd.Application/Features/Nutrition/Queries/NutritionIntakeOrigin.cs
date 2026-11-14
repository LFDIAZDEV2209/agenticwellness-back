namespace CoppAddresd.Application.Features.Nutrition.Queries;

/// <summary>Una etiqueta libre no acredita autoría clínica; el estimado debe ser propio y no estar corregido.</summary>
public static class NutritionIntakeOrigin
{
    public static string Resolve(string source, IntakeMacrosDto recorded, IntakeMacrosDto? ownedEstimate)
        => ownedEstimate is not null && source == "ai_photo" && recorded == ownedEstimate ? "ai_estimated" : "patient_confirmed";
}
