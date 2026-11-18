using CoppAddresd.Application.Features.Medications;
using FluentValidation;
using FluentValidation.Results;
using MediatR;

namespace CoppAddresd.Application.Features.Agents.Vita;

public sealed record GetVitaProfileQuery(Guid UserId) : IRequest<VitaProfileDto>;
public sealed record GetVitaVitalsQuery(Guid UserId, DateOnly? From, DateOnly? To) : IRequest<VitaVitalsDto>;
public sealed record GetVitaMedicationsQuery(Guid UserId) : IRequest<VitaMedicationsDto>;

public sealed class GetVitaProfileQueryValidator : AbstractValidator<GetVitaProfileQuery>
{
    public GetVitaProfileQueryValidator() => RuleFor(x => x.UserId).NotEmpty();
}

public sealed class GetVitaVitalsQueryValidator : AbstractValidator<GetVitaVitalsQuery>
{
    public GetVitaVitalsQueryValidator() => RuleFor(x => x.UserId).NotEmpty();
}

public sealed class GetVitaMedicationsQueryValidator : AbstractValidator<GetVitaMedicationsQuery>
{
    public GetVitaMedicationsQueryValidator() => RuleFor(x => x.UserId).NotEmpty();
}

public sealed class VitaClinicalReadQueries(VitaPatientAccess access, IVitaClinicalReader clinical,
    MedicationControlService medications, TimeProvider clock)
    : IRequestHandler<GetVitaProfileQuery, VitaProfileDto>,
        IRequestHandler<GetVitaVitalsQuery, VitaVitalsDto>,
        IRequestHandler<GetVitaMedicationsQuery, VitaMedicationsDto>
{
    public async Task<VitaProfileDto> Handle(GetVitaProfileQuery request, CancellationToken ct)
    {
        var patient = await access.RequireAsync(request.UserId, "chat", ct);
        var asOf = clock.GetUtcNow();
        var zone = TimeZoneInfo.FindSystemTimeZoneById(patient.Timezone);
        var today = LocalDate(asOf, zone);
        int? age = null;
        if (patient.DateOfBirth is { } birthDate && DateOnly.FromDateTime(birthDate) <= today)
        {
            var birth = DateOnly.FromDateTime(birthDate);
            var years = today.Year - birth.Year;
            age = today < birth.AddYears(years) ? years - 1 : years;
        }
        var details = await clinical.GetProfileAsync(patient.Id, today, asOf.UtcDateTime, ct);
        return new(asOf.UtcDateTime, new(patient.FirstName, age, patient.Gender, patient.Timezone),
            details.Goals, details.Allergies, details.Restrictions, details.Anthropometry);
    }

    public async Task<VitaVitalsDto> Handle(GetVitaVitalsQuery request, CancellationToken ct)
    {
        var patient = await access.RequireAsync(request.UserId, "chat", ct);
        var asOf = clock.GetUtcNow();
        var zone = TimeZoneInfo.FindSystemTimeZoneById(patient.Timezone);
        var to = request.To ?? LocalDate(asOf, zone);
        var from = request.From ?? DateOnly.FromDayNumber(Math.Max(1, to.DayNumber - 6));
        if (from == DateOnly.MinValue || to == DateOnly.MaxValue || to.DayNumber - from.DayNumber is < 0 or > 30)
            throw new ValidationException([new ValidationFailure("from", "Selecciona un intervalo de hasta 31 días.")]);

        var details = await clinical.GetVitalsAsync(patient.Id, StartOfDayUtc(from, zone),
            StartOfDayUtc(to.AddDays(1), zone), asOf.UtcDateTime, ct);
        return new(asOf.UtcDateTime, patient.Timezone, from, to, details.Latest, details.History, details.HasMore);
    }

    public async Task<VitaMedicationsDto> Handle(GetVitaMedicationsQuery request, CancellationToken ct)
    {
        medications.EnsureEnabled();
        var patient = await access.RequireAsync(request.UserId, "chat", ct);
        var asOf = clock.GetUtcNow();
        var zone = MedicationControlService.Zone(patient.Timezone);
        var today = LocalDate(asOf, zone);
        var day = await medications.ReadCurrentDayAsync(patient.Id, today, zone, asOf, ct);
        var doses = day.Doses.ToLookup(d => d.TreatmentId,
            d => new VitaMedicationDoseDto(d.ScheduledAt, d.Status, d.OccurredAt));
        var treatments = day.Treatments.Select(t => new VitaMedicationTreatmentDto(t.Id, t.Name, t.ProductType,
            t.Dosage, t.Unit, t.Route, t.TimeZone, t.Times, t.StartDate, t.EndDate, doses[t.Id].ToArray())).ToArray();
        return new(asOf.UtcDateTime, patient.Timezone, today, treatments);
    }

    private static DateOnly LocalDate(DateTimeOffset instant, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);

    private static DateTime StartOfDayUtc(DateOnly day, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local))
            local = local.AddMinutes(1);
        return zone.IsAmbiguousTime(local)
            ? new DateTimeOffset(local, zone.GetAmbiguousTimeOffsets(local).Max()).UtcDateTime
            : TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }
}
