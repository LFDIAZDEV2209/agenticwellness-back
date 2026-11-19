using CoppAddresd.WhatsApp.Messaging;

namespace CoppAddresd.WhatsApp.Tests;

/// <summary>
/// Pruebas del normalizador de formato para WhatsApp: negrita con asteriscos
/// simples, sin separadores Markdown que el canal muestra literales y
/// encabezados convertidos a negrita.
/// </summary>
[Trait("Category", "Unit")]
public sealed class MessagePolicyTests
{
    [Theory]
    [InlineData("**Nutrición**", "*Nutrición*")]
    [InlineData("Lo que **no** puedo", "Lo que *no* puedo")]
    [InlineData("Texto sin markdown", "Texto sin markdown")]
    [InlineData("", "")]
    public void ConvierteNegritaDobleASimple(string input, string expected) =>
        Assert.Equal(expected, MessagePolicy.NormalizeForWhatsApp(input));

    [Theory]
    [InlineData("---\r\nSiguiente sección", "\nSiguiente sección")]
    [InlineData("***\r\nTexto", "\nTexto")]
    [InlineData("___\r\nTexto", "\nTexto")]
    public void EliminaSeparadoresHorizontales(string input, string expected) =>
        Assert.Equal(expected, MessagePolicy.NormalizeForWhatsApp(input));

    [Theory]
    [InlineData("### Nutrición", "*Nutrición*")]
    [InlineData("## Tu plan de salud", "*Tu plan de salud*")]
    public void ConvierteEncabezadosANegrita(string input, string expected) =>
        Assert.Equal(expected, MessagePolicy.NormalizeForWhatsApp(input));

    [Fact]
    public void NoAlternaAsteriscosSimplesYaValidos() =>
        Assert.Equal(
            "*Nutrición* con detalle",
            MessagePolicy.NormalizeForWhatsApp("*Nutrición* con detalle")
        );

    [Fact]
    public void PreservaSaltosDeLineaYBloques() =>
        Assert.Equal(
            "Saludo\n\n*Item uno*\nDetalle\n\n*Item dos*",
            MessagePolicy.NormalizeForWhatsApp(
                "Saludo\r\n\r\n**Item uno**\r\nDetalle\r\n\r\n**Item dos**"
            )
        );

    [Fact]
    public void AdaptaVinculosTablasTachadoYBloquesDeCodigo() =>
        Assert.Equal(
            "Guía (https://example.invalid/guia)\nNombre · Dosis\n\nÁcido · ~10~ *20*\n\n# conservar **código**\n",
            MessagePolicy.NormalizeForWhatsApp(
                "[Guía](https://example.invalid/guia)\n| Nombre | Dosis |\n| --- | :---: |\n| Ácido | ~~10~~ __20__ |\n```text\n# conservar **código**\n```"));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(4095, 1)]
    [InlineData(4096, 1)]
    [InlineData(4097, 2)]
    [InlineData(5000, 2)]
    public void RespetaLimiteYConservaContenido(int length, int expectedParts)
    {
        var original = new string('a', length);
        var parts = MessagePolicy.Split(original);
        Assert.Equal(expectedParts, parts.Length);
        Assert.All(parts, part => Assert.InRange(part.Length, 1, 4096));
        Assert.Equal(original, string.Concat(parts));
    }

    [Theory]
    [InlineData("👩🏽‍⚕️")]
    [InlineData("🇨🇴")]
    [InlineData("e\u0301")]
    [InlineData("😀")]
    public void ConservaGrafemasEnLaFrontera(string grapheme)
    {
        var beginning = new string('a', 4095);
        var original = beginning + grapheme + "fin";
        Assert.Equal(new[] { beginning, grapheme + "fin" }, MessagePolicy.Split(original));
    }

    [Fact]
    public void GrafemaMayorAlLimiteConservaEscalaresYNoPierdeTexto()
    {
        var original = "😀" + new string('\u0301', 4998);
        var parts = MessagePolicy.Split(original);
        Assert.Equal(4096, parts[0].Length);
        Assert.Equal(original, string.Concat(parts));
    }
}
