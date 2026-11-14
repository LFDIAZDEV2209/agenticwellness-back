using CoppAddresd.Application.Features.Media;

namespace CoppAddresd.UnitTests.Media;

public sealed class BulkMediaValidationTests
{
    [Theory]
    [InlineData(0, false)] [InlineData(1, true)] [InlineData(100, true)] [InlineData(101, false)]
    public void Lote_ValidaLimitesSinInvalidarTodoPorUnaFila(int count, bool valid)
    {
        Assert.Equal(valid, new CreateBulkMediaDraftsValidator().Validate(new CreateBulkMediaDraftsCommand(
            Enumerable.Range(0, count).Select(n => new BulkMediaFile("archivo.mp3", "media/audio.mp3")).ToArray())).IsValid);
        Assert.Equal(valid, new PublishBulkMediaValidator().Validate(new PublishBulkMediaCommand(
            Enumerable.Range(0, count).Select(n => new BulkMediaPublishFile("archivo.mp3", Guid.NewGuid())).ToArray())).IsValid);
    }
}
