using System.Text.Json;
using FluentValidation;

namespace CoppAddresd.Application.Features.Motor365;

public sealed class WriteMotorPersonValidator : AbstractValidator<WriteMotorPerson>
{
    public WriteMotorPersonValidator()
    {
        RuleFor(x => x.Input).SetValidator(new MotorPersonInputValidator());
        When(x => x.Id == null, () =>
        {
            RuleFor(x => x.Input.Sex).NotNull().OverridePropertyName("sex");
            RuleFor(x => x.Input.Age).NotNull().OverridePropertyName("age");
            RuleFor(x => x.Input.H).NotNull().OverridePropertyName("h");
            RuleFor(x => x.Input.W0).NotNull().OverridePropertyName("w0");
            RuleFor(x => x.Input.Waist0).NotNull().OverridePropertyName("waist0");
            RuleFor(x => x.Input.Target).NotNull().OverridePropertyName("target");
            RuleFor(x => x.Input.Act).NotNull().OverridePropertyName("act");
            RuleFor(x => x.Input.Goal).NotNull().OverridePropertyName("goal");
        });
        RuleFor(x => x.Input.Id).Must((x, id) => x.Id is null || id is null || id == x.Id)
            .OverridePropertyName("id");
    }
}

public sealed class UpdateMotorPersonValidator : AbstractValidator<UpdateMotorPerson>
{
    public UpdateMotorPersonValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Input).SetValidator(new MotorPersonInputValidator());
        RuleFor(x => x.Input.Id).Must((x, id) => id is null || id == x.Id).OverridePropertyName("id");
    }
}

public sealed class MotorPersonInputValidator : AbstractValidator<MotorPersonInput>
{
    public MotorPersonInputValidator()
    {
        RuleFor(x => x.Id).Must(id => id != Guid.Empty);
        RuleFor(x => x.SubmissionId).Must(id => id != Guid.Empty);
        RuleFor(x => x.N).MaximumLength(200);
        RuleFor(x => x.Sex).Must(x => x is null or "F" or "M");
        RuleFor(x => x.Age).InclusiveBetween(0, 120);
        RuleFor(x => x.H).GreaterThanOrEqualTo(.01m).LessThanOrEqualTo(300).PrecisionScale(5, 2, true);
        RuleFor(x => x.W0).GreaterThanOrEqualTo(.01m).LessThanOrEqualTo(1000).PrecisionScale(6, 2, true);
        RuleFor(x => x.Waist0).GreaterThanOrEqualTo(.01m).LessThanOrEqualTo(500).PrecisionScale(5, 2, true);
        RuleFor(x => x.Target).GreaterThanOrEqualTo(.01m).LessThanOrEqualTo(1000).PrecisionScale(6, 2, true);
        RuleFor(x => x.Act).InclusiveBetween(1, 4);
        RuleFor(x => x.Goal).Must(x => x is null or "bajar" or "masa" or "def");
        RuleFor(x => x.Pace).Must(x => x is null or "s" or "m" or "max");
        RuleFor(x => x.Veg).InclusiveBetween(0, 1);
        RuleFor(x => x.Nolac).InclusiveBetween(0, 1);
        RuleFor(x => x.StartDate).Null();
        RuleFor(x => x.Ver).GreaterThan(0);
        RuleFor(x => x.ParqA).Must(x => x is null || x.Length == 7);
        RuleForEach(x => x.ParqA).Must(x => x is null or 0 or 1);
        RuleFor(x => x.NqA).Must(x => x is null || x.Length == 6);
        RuleForEach(x => x.NqA).Must(x => x is null or 0 or 1);
        RuleFor(x => x.Terms!.Version).MaximumLength(32).When(x => x.Terms is not null);
        RuleFor(x => x.Log).Must(log => log is null || log.Count <= 365 && log.Keys.All(d => d is >= 1 and <= 365));
        RuleForEach(x => x.Log).ChildRules(log =>
            log.RuleFor(x => x.Value).NotNull().SetValidator(new MotorLogInputValidator()));
    }
}

public sealed class MotorLogInputValidator : AbstractValidator<MotorLogInput>
{
    public MotorLogInputValidator()
    {
        RuleFor(x => x.Adh).NotNull().InclusiveBetween(0, 1).PrecisionScale(5, 4, true);
        RuleFor(x => x.Sleep).NotNull().InclusiveBetween(0, 24).PrecisionScale(4, 2, true);
        RuleFor(x => x.Energy).NotNull().InclusiveBetween(1, 5);
        RuleFor(x => x.Pain).NotNull().InclusiveBetween(0, 10);
        RuleFor(x => x.Ex).InclusiveBetween(0, 1);
        RuleFor(x => x.W).GreaterThanOrEqualTo(.01m).LessThanOrEqualTo(1000).PrecisionScale(6, 2, true);
        RuleFor(x => x.Waist).GreaterThanOrEqualTo(.01m).LessThanOrEqualTo(500).PrecisionScale(5, 2, true);
        RuleFor(x => x.Sym).Must(sym => sym is null || sym.Length <= 4 && sym.Distinct().Count() == sym.Length);
        RuleForEach(x => x.Sym).Must(s => s is "mareo" or "fatiga" or "hambre" or "insomnio");
    }
}

public sealed class PutMotorLogValidator : AbstractValidator<PutMotorLog>
{
    public PutMotorLogValidator()
    {
        RuleFor(x => x.Day).InclusiveBetween(1, 365);
        RuleFor(x => x.Input).SetValidator(new MotorLogInputValidator());
    }
}

public sealed class GetMotorDayValidator : AbstractValidator<GetMotorDay>
{
    public GetMotorDayValidator() => RuleFor(x => x.Day).InclusiveBetween(1, 365);
}

public sealed class PutMotorParamsValidator : AbstractValidator<PutMotorParams>
{
    public PutMotorParamsValidator()
    {
        RuleFor(x => x.Input.Params).Must(x => x.ValueKind == JsonValueKind.Object);
        RuleFor(x => x.Input.Reason).MaximumLength(500);
        RuleFor(x => x.Input.ExpectedVersion).GreaterThan(0);
    }
}

public sealed class GetMotorAuditValidator : AbstractValidator<GetMotorAudit>
{
    public GetMotorAuditValidator()
    {
        RuleFor(x => x.Page).InclusiveBetween(1, 10_000);
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100);
    }
}
