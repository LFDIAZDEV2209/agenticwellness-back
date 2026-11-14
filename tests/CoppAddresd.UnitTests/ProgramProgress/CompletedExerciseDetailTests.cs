using CoppAddresd.Application.DTOs.ProgramProgress;
using CoppAddresd.Domain.Entities;

namespace CoppAddresd.UnitTests.ProgramProgress;

public sealed class CompletedExerciseDetailTests
{
    [Fact]
    public void Captura_ContratoInmutable_ConReferenciaEstableYSinUrlFirmada()
    {
        var routine = new ExerciseRoutine { Id = Guid.NewGuid(), Name = "Fuerza", Exercises = [new() { Id = Guid.NewGuid(), Name = "Sentadilla", Sets = 3, Repetitions = 12, MediaId = Guid.NewGuid() }] };
        var captured = CompletedExerciseDetail.Capture(routine, DateTimeOffset.UtcNow);
        routine.Name = "Editada"; routine.Exercises.Single().Sets = 8;
        Assert.Equal("Fuerza", captured.GetProperty("name").GetString());
        var exercise = Assert.Single(captured.GetProperty("exercises").EnumerateArray());
        Assert.Equal(3, exercise.GetProperty("sets").GetInt32()); Assert.Equal(12, exercise.GetProperty("reps").GetInt32());
        Assert.Equal(routine.Exercises.Single().MediaId, exercise.GetProperty("mediaRef").GetGuid());
        Assert.Equal(3, CompletedExerciseDetail.Read(captured)!.ToExercises().Single().Sets);
        Assert.DoesNotContain("mediaUrl", captured.GetRawText());
        Assert.Null(CompletedExerciseDetail.Read(null));
    }
}
