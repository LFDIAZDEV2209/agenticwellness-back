namespace CoppAddresd.Application.DTOs.ProgramProgress;

/// <summary>Resumen propio de una inscripción terminada; nunca crea la siguiente.</summary>
public sealed record ProgramCompletionDto(Guid EnrollmentId, string TemplateName, string Timezone,
    DateOnly StartLocalDate, DateOnly EndLocalDate, DateTime CompletedAt, int TotalDays,
    int CompletedWeeks, int DaysWithActivity, int PerfectDays, int TasksCompleted,
    int XpEarned, int LongestStreak, bool CanStartNewProgram);

/// <summary>Sin un programa terminado devuelve completion:null, sin inventar un resumen.</summary>
public sealed record ProgramCompletionResponse(ProgramCompletionDto? Completion);
