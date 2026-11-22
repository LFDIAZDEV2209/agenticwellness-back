using CoppAddresd.Application.Features.FamilyCameras;
using CoppAddresd.Domain.Entities;

namespace CoppAddresd.Infrastructure.Services;

internal sealed class DemoFamilyCameraFeed : IFamilyCameraFeed
{
    private static readonly Lazy<byte[][]> Frames = new(() => Enumerable.Range(0, 8).Select(LoadFrame).ToArray());

    public string Provider => FamilyCamera.DemoProvider;

    public ValueTask<FamilyCameraFrame?> ReadAsync(Guid cameraId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var phase = (int)(Environment.TickCount64 / 1000 % 8);
        return ValueTask.FromResult<FamilyCameraFrame?>(new FamilyCameraFrame(Frames.Value[phase]));
    }

    private static byte[] LoadFrame(int phase)
    {
        var assembly = typeof(DemoFamilyCameraFeed).Assembly;
        var resource = $"CoppAddresd.Infrastructure.Services.FamilyCameraDemo.frame{phase}.jpg";
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Falta la imagen de prueba {resource}");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
