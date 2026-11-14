using CoppAddresd.Api.Authorization;
using CoppAddresd.Api.Controllers;
using CoppAddresd.Application.Features.Wellness;
using CoppAddresd.Domain.Entities;

namespace CoppAddresd.UnitTests.Media;

public sealed class CloneRoutineContractTests
{
    [Fact]
    public void Clonar_ExigePermisoDeBibliotecaYGuardDePacienteOpcional()
    {
        var action = typeof(WellnessController).GetMethod(nameof(WellnessController.CloneExerciseRoutine))!;
        Assert.Equal("Wellness.Manage", Assert.Single(action.GetCustomAttributes(typeof(RequireContextPermissionAttribute), true)
            .Cast<RequireContextPermissionAttribute>()).PermissionCode);
        Assert.Contains(action.GetCustomAttributes(true), a => a.GetType().Name == "PatientScopeAttribute");
    }

    [Fact]
    public void Contraindicaciones_SeExponenEnDetalleYLista()
    {
        var routine = new ExerciseRoutine { Contraindications = "Revisar lesión de rodilla" };
        Assert.Equal(routine.Contraindications, ExerciseRoutineDto.FromEntity(routine).Contraindications);
        Assert.Equal(routine.Contraindications, ExerciseRoutineListItemDto.FromEntity(routine).Contraindications);
        Assert.False(new CloneExerciseRoutineValidator().Validate(new CloneExerciseRoutineCommand(Guid.NewGuid(), new("", Guid.Empty))).IsValid);
    }
}
