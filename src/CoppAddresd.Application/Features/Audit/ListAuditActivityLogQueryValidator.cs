using FluentValidation;

namespace CoppAddresd.Application.Features.Audit;

/// <summary>Rechaza paginación, acciones y ventanas de fechas inválidas antes de consultar.</summary>
public sealed class ListAuditActivityLogQueryValidator : AbstractValidator<ListAuditActivityLogQuery>
{
    public ListAuditActivityLogQueryValidator()
    {
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1).WithMessage("La página debe ser mayor o igual a 1.");
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100).WithMessage("El tamaño de página debe estar entre 1 y 100.");
        RuleFor(x => x.Action).Must(action => string.IsNullOrWhiteSpace(action)
                || action.Trim().ToUpperInvariant() is "INSERT" or "UPDATE" or "DELETE")
            .WithMessage("La acción debe ser INSERT, UPDATE o DELETE.");
        RuleFor(x => x.To).Must((query, to) => query.From is null || to is null || query.From <= to)
            .WithMessage("La fecha final debe ser mayor o igual a la inicial.");
    }
}
