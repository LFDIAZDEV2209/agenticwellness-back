using CoppAddresd.Application.Features.ProgramProgress;
using CoppAddresd.Domain.Exceptions;

namespace CoppAddresd.UnitTests.ProgramProgress;

public sealed class ProgramRestDayPolicyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("valor-invalido")]
    public void Descanso_ApagadoPorDefecto_RechazaCrearPeroPermiteOtrosCambios(string? value)
    {
        Assert.Throws<UnprocessableEntityException>(() => ProgramRestDayPolicy.EnsureAllowed(value, true));
        ProgramRestDayPolicy.EnsureAllowed(value, false);
    }

    [Fact]
    public void Descanso_EncendidoExplicitamente_PermiteCrear()
        => ProgramRestDayPolicy.EnsureAllowed("true", true);
}
