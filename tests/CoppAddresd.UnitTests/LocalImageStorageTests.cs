using CoppAddresd.Application.Interfaces;
using CoppAddresd.Infrastructure.Services;
using NSubstitute;

namespace CoppAddresd.UnitTests;

public sealed class LocalImageStorageTests
{
    [Theory]
    [InlineData("\"etag-s3\"")]
    [InlineData("C:/storage/foodai/image.jpg")]
    public async Task SaveImage_PersisteClaveCanonicaIndependienteDelResultadoDelProveedor(string receipt)
    {
        var storage = Substitute.For<IObjectStorageService>(); var id = Guid.NewGuid();
        using var content = new MemoryStream([1, 2, 3]); using var cancellation = new CancellationTokenSource();
        var key = $"foodai/{id:N}.jpg";
        storage.PutObjectAsync(key, content, null, cancellation.Token).Returns(receipt);
        Assert.Equal(key, await new LocalImageStorage(storage).SaveImageAsync(id, "comida.JPG", content, cancellation.Token));
        await storage.Received(1).PutObjectAsync(key, content, null, cancellation.Token);
        Assert.True(content.CanRead);
    }

    [Theory]
    [InlineData("\"etag-s3\"")]
    [InlineData("C:/storage/foodai/masks/item.png")]
    public async Task SaveMask_PersisteClaveCanonicaIndependienteDelResultadoDelProveedor(string receipt)
    {
        var storage = Substitute.For<IObjectStorageService>(); var id = Guid.NewGuid();
        using var content = new MemoryStream([1, 2, 3]); using var cancellation = new CancellationTokenSource();
        var key = $"foodai/masks/{id:N}/2.png";
        storage.PutObjectAsync(key, content, "image/png", cancellation.Token).Returns(receipt);
        Assert.Equal(key, await new LocalImageStorage(storage).SaveMaskAsync(id, 2, content, cancellation.Token));
        await storage.Received(1).PutObjectAsync(key, content, "image/png", cancellation.Token);
        Assert.True(content.CanRead);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Guardar_CancelacionNoDevuelveClaveYConservaStreamDelLlamador(bool mask)
    {
        var storage = Substitute.For<IObjectStorageService>(); using var content = new MemoryStream([1]);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        storage.PutObjectAsync(Arg.Any<string>(), content, Arg.Any<string?>(), cancellation.Token)
            .Returns(Task.FromCanceled<string>(cancellation.Token));
        var images = new LocalImageStorage(storage); var id = Guid.NewGuid();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mask
            ? images.SaveMaskAsync(id, 0, content, cancellation.Token)
            : images.SaveImageAsync(id, "comida.png", content, cancellation.Token));
        Assert.True(content.CanRead);
    }
}
