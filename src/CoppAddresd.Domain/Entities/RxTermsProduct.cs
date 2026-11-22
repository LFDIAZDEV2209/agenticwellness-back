namespace CoppAddresd.Domain.Entities;

/// <summary>Presentación de referencia de RxTerms, identificada por RxCUI.</summary>
public sealed class RxTermsProduct
{
    public string RxCui { get; set; } = "";
    public string Name { get; set; } = "";
    public string SearchText { get; set; } = "";
    public string? Strength { get; set; }
    public string? DoseForm { get; set; }
    public string? Route { get; set; }
    public string? DoseUnit { get; set; }
    public string Release { get; set; } = "";
    public bool IsActive { get; set; }
}
