using CoppAddresd.Application.DTOs;

namespace CoppAddresd.UnitTests.Media;

public sealed class ExerciseExternalReferenceTests
{
    [Theory]
    [InlineData("https://youtu.be/0FkRr45YeEQ", "https://www.youtube-nocookie.com/embed/0FkRr45YeEQ")]
    [InlineData("https://vimeo.com/418873416", "https://player.vimeo.com/video/418873416?dnt=1")]
    public void MedioConfigurado_PredominaSobreVideoPorNombre(string url, string embed)
    {
        var reference = ExerciseReferenceVideo.FromExternal("Plancha", url, "Otro autor");
        Assert.Equal(embed, reference!.EmbedUrl); Assert.Equal("Otro autor", reference.Author);
        Assert.NotEqual(ExerciseReferenceVideo.ForExercise("Plancha"), reference);
    }

    [Theory]
    [InlineData("http://www.youtube.com/watch?v=cCMob-mi19U")]
    [InlineData("https://sitio-ajeno.example/embed/cCMob-mi19U")]
    [InlineData("https://www.youtube.com/watch?v=cCMob-mi19U#fragmento")]
    public void ReferenciaInvalida_NoSeEmiteNiCaeEnRespaldo(string url)
        => Assert.Null(ExerciseReferenceVideo.FromExternal("Plancha", url, "Autor"));
}
