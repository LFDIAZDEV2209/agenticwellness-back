using CoppAddresd.Application.Services.ProgramProgress;

namespace CoppAddresd.UnitTests.ProgramProgress;

public sealed class RestStreakPolicyTests
{
    [Theory]
    [InlineData(2, 2, 3, true)]
    [InlineData(1, 2, 3, false)]
    [InlineData(1, 2, 1, true)]
    [InlineData(0, 0, 1, false)]
    public void Racha_ExigeUmbralOCompletarLasMisionesExistentes(int completed, int scheduled, int minimum, bool expected) =>
        Assert.Equal(expected, ProgramDayMissionPolicy.QualifiesForStreak(completed, scheduled, minimum));
}
