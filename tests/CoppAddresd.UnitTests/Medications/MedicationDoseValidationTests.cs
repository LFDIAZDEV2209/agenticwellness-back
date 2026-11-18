using CoppAddresd.Application.Features.Medications;
using CoppAddresd.Application.Interfaces;
using FluentValidation;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace CoppAddresd.UnitTests.Medications;

public sealed class MedicationDoseValidationTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private static MedicationControlService Service(IMedicationControlStore store, TimeProvider clock)
        => new(store, Substitute.For<IPatientScopeGuard>(), Substitute.For<IOptionsSnapshot<MedicationControlSettings>>(), clock);

    [Theory]
    [InlineData("0001-01-01T00:00:00Z")]
    [InlineData("9999-12-31T23:59:59Z")]
    [InlineData("2025-11-15T12:00:00Z")]
    [InlineData("2027-11-19T12:00:00Z")]
    public async Task Record_ScheduledOutsideWorkspaceWindow_RejectsBeforePersistence(string date)
    {
        var store = Substitute.For<IMedicationControlStore>();
        var request = new RecordMedicationDoseRequest(Guid.NewGuid(), "taken", ScheduledAt: DateTimeOffset.Parse(date));

        var error = await Assert.ThrowsAsync<ValidationException>(() => Service(store, new Clock())
            .RecordAsync(Guid.NewGuid(), Guid.NewGuid(), "staff_recorded", Guid.NewGuid(), request, default));

        Assert.Contains(error.Errors, issue => issue.PropertyName == "scheduledAt");
        await store.DidNotReceiveWithAnyArgs().RecordAsync(default, default, default!, default, default!, default);
    }

    [Theory]
    [InlineData(-366)]
    [InlineData(0)]
    [InlineData(366)]
    public async Task Record_WorkspaceWindowBoundary_AllowedWithoutMaterializingOmittedOccurrence(int days)
    {
        var store = Substitute.For<IMedicationControlStore>();
        var clock = new Clock();
        var patient = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var dose = Guid.NewGuid();
        var request = new RecordMedicationDoseRequest(Guid.NewGuid(), "taken", ExpectedRevision: 0,
            ScheduledAt: clock.UtcNow.AddDays(days));
        var service = Service(store, clock);

        await service.RecordAsync(patient, actor, "staff_recorded", dose, request, default);
        clock.UtcNow = clock.UtcNow.AddSeconds(30);
        await service.RecordAsync(patient, actor, "staff_recorded", dose, request, default);

        await store.Received(2).RecordAsync(patient, actor, "staff_recorded", dose,
            Arg.Is<RecordMedicationDoseRequest>(body => body.ClientRequestId == request.ClientRequestId
                && body.OccurredAt == null && body.ScheduledAt == request.ScheduledAt), CancellationToken.None);
    }

    [Fact]
    public async Task Record_WindowUsesUtcDate_AndRetainsOptionalScheduledAtCompatibility()
    {
        var store = Substitute.For<IMedicationControlStore>();
        var clock = new Clock();
        var patient = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var dose = Guid.NewGuid();
        // Fecha local fuera del límite; su fecha UTC sigue en el último día permitido.
        var scheduled = new DateTimeOffset(2027, 11, 19, 0, 30, 0, TimeSpan.FromHours(14));
        var request = new RecordMedicationDoseRequest(Guid.NewGuid(), "taken", ScheduledAt: scheduled);
        var service = Service(store, clock);

        await service.RecordAsync(patient, actor, "staff_recorded", dose, request, default);
        await service.RecordAsync(patient, actor, "staff_recorded", dose, request with { ScheduledAt = null }, default);

        await store.ReceivedWithAnyArgs(2).RecordAsync(default, default, default!, default, default!, default);
    }
}
