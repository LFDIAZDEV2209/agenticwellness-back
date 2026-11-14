using System.Text.Json.Serialization;

namespace CoppAddresd.Application.DTOs.ProgramProgress;

/// <summary>Vista de un próximo día, sin completaciones ni efectos de activación.</summary>
public sealed record ProgramUpcomingDayDto(DateOnly Date, int Day, int WeekNumber, IReadOnlyList<ProgramUpcomingMissionDto> Missions,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Rest = null);

/// <summary>Contenido previsto; conserva identificadores separados de plan y prescripción.</summary>
public sealed record ProgramUpcomingMissionDto(string Type, string Title,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? RoutineId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? MediaId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? NutritionPlanId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? SourcePrescriptionId = null,
    bool ContentUnavailable = false);
