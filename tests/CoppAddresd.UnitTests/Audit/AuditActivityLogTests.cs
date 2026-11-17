using System.Text.Json;
using CoppAddresd.Application.Features.Audit;
using CoppAddresd.Application.Interfaces;
using NSubstitute;

namespace CoppAddresd.UnitTests.Audit;

public sealed class AuditActivityLogTests
{
    [Theory]
    [InlineData(0, 20, null)]
    [InlineData(-1, 20, null)]
    [InlineData(1, 0, null)]
    [InlineData(1, 101, null)]
    [InlineData(1, 20, "AdaptationApplied")]
    [InlineData(1, 20, "1")]
    [InlineData(1, 20, "OTRA")]
    public void Validator_RechazaPaginacionOAccionInvalida(int page, int size, string? action)
        => Assert.False(new ListAuditActivityLogQueryValidator().Validate(new ListAuditActivityLogQuery(Page: page, PageSize: size, Action: action)).IsValid);

    [Theory]
    [InlineData(1, 1, "INSERT")]
    [InlineData(1, 100, " update ")]
    [InlineData(int.MaxValue, 100, "DELETE")]
    [InlineData(1, 20, null)]
    [InlineData(1, 20, " ")]
    public void Validator_AceptaLimitesYFiltrosOpcionales(int page, int size, string? action)
        => Assert.True(new ListAuditActivityLogQueryValidator().Validate(new ListAuditActivityLogQuery(Page: page, PageSize: size, Action: action)).IsValid);

    [Fact]
    public void Validator_ComparaInstantesConOffsetYRechazaRangoInvertido()
    {
        var from = DateTimeOffset.Parse("2026-11-17T08:00:00-05:00");
        var same = DateTimeOffset.Parse("2026-11-17T13:00:00Z");
        var validator = new ListAuditActivityLogQueryValidator();
        Assert.True(validator.Validate(new ListAuditActivityLogQuery(From: from, To: same)).IsValid);
        Assert.False(validator.Validate(new ListAuditActivityLogQuery(From: from, To: same.AddTicks(-1))).IsValid);
    }

    [Fact]
    public async Task Handler_NormalizaFiltrosYUtc_PropagaTokenYResultado()
    {
        var repository = Substitute.For<IAuditLogRepository>();
        var result = new PaginatedAuditActivityLogResult([], 0, 3, 10, 0);
        using var cancellation = new CancellationTokenSource();
        var from = DateTimeOffset.Parse("2026-11-17T08:00:00-05:00");
        var to = from.AddHours(1);
        repository.ListAsync(Arg.Any<ListAuditActivityLogQuery>(), cancellation.Token).Returns(result);
        var handler = new ListAuditActivityLogQueryHandler(repository);
        var actual = await handler.Handle(new(3, 10, " auth ", " users ", " update ", " ANA%_ ", from, to, " corr%_ "), cancellation.Token);
        Assert.Same(result, actual);
        await repository.Received(1).ListAsync(new(3, 10, "auth", "users", "UPDATE", "ANA%_", from.ToUniversalTime(), to.ToUniversalTime(), "corr%_"), cancellation.Token);
        var normalized = (ListAuditActivityLogQuery)repository.ReceivedCalls().Single().GetArguments()[0]!;
        Assert.Equal(TimeSpan.Zero, normalized.From!.Value.Offset);
        Assert.Equal(TimeSpan.Zero, normalized.To!.Value.Offset);
    }

    [Fact]
    public async Task Handler_ConvierteFiltrosVaciosEnNull()
    {
        var repository = Substitute.For<IAuditLogRepository>();
        await new ListAuditActivityLogQueryHandler(repository).Handle(new(Schema: " ", Table: "", Action: " ", Actor: " ", Search: " "), CancellationToken.None);
        await repository.Received().ListAsync(new ListAuditActivityLogQuery(), CancellationToken.None);
    }

    [Fact]
    public async Task Handler_NoOcultaCancelacionDelRepositorio()
    {
        var repository = Substitute.For<IAuditLogRepository>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        repository.ListAsync(Arg.Any<ListAuditActivityLogQuery>(), cancellation.Token)
            .Returns(Task.FromCanceled<PaginatedAuditActivityLogResult>(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ListAuditActivityLogQueryHandler(repository)
            .Handle(new(), cancellation.Token));
    }

    [Fact]
    public void Dto_ExponeExclusivamenteLaListaPublica()
    {
        var dto = new AuditActivityLogDto(Guid.NewGuid(), DateTimeOffset.UtcNow, "INSERT", "auth", "users", "fila", "USER", null, null, null, null);
        var json = JsonSerializer.SerializeToElement(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(new[] { "action", "actorType", "correlationId", "id", "ipAddress", "occurredAt", "recordId", "schemaName", "tableName", "userEmail", "userRole" },
            json.EnumerateObject().Select(x => x.Name).OrderBy(x => x).ToArray());
        Assert.Contains(CoppAddresd.Api.Constants.PermissionCodes.AuditView, CoppAddresd.Api.Constants.PermissionCodes.All);
        Assert.Contains(CoppAddresd.Api.Constants.PermissionCodes.AuditView, CoppAddresd.Auth.Constants.PermissionCodes.GetAll());
    }
}
