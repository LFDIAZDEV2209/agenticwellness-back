namespace CoppAddresd.Application.Interfaces;

// Límites de entrada compartidos con el check-in. El catálogo conserva las unidades.
public static class VitalCaptureLimits
{
    public const int HeartRateMin = 20;
    public const int HeartRateMax = 250;
    public const int SystolicMin = 50;
    public const int SystolicMax = 260;
    public const int DiastolicMin = 20;
    public const int DiastolicMax = 180;
    public const int OxygenMin = 30;
    public const int OxygenMax = 100;
    public const int TemperatureMin = 30;
    public const int TemperatureMax = 45;
}
