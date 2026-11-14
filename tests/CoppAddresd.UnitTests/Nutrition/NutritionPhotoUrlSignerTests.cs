using CoppAddresd.Api.Security;
using CoppAddresd.Application.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using NSubstitute;

namespace CoppAddresd.UnitTests.Nutrition;

public sealed class NutritionPhotoUrlSignerTests
{
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-11-07T18:00:00Z"); }

    [Fact]
    public async Task FotoLocal_FirmaTemporalLigadaAlAnalisis_YCaduca()
    {
        var storage = Substitute.For<IObjectStorageService>(); var id = Guid.NewGuid(); var key = $"foodai/{id:N}.jpg";
        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        http.HttpContext.Request.Scheme = "https"; http.HttpContext.Request.Host = new("example.test");
        var signatures = new StorageSignatureService("clave-sintetica-exclusiva-de-test"); var clock = new Clock();
        var signer = new NutritionPhotoUrlSigner(storage, signatures, http, clock);
        var photo = await signer.SignAsync(id, key, default); Assert.NotNull(photo);
        var query = QueryHelpers.ParseQuery(new Uri(photo.Url).Query);
        Assert.True(signatures.Validate(key, query["sig"].ToString(), query["exp"].ToString(), clock.GetUtcNow()));
        Assert.False(signatures.Validate(key, query["sig"].ToString(), query["exp"].ToString(), photo.ExpiresAt.AddSeconds(1)));
        Assert.Equal(clock.GetUtcNow().AddMinutes(5), photo.ExpiresAt);
        Assert.Null(await signer.SignAsync(Guid.NewGuid(), key, default));
        Assert.Null(await signer.SignAsync(id, "public/imagen.jpg", default));
    }

    [Fact]
    public async Task FotoCloud_UsaOperacionProtegidaYExpiracionDeCincoMinutos()
    {
        var storage = Substitute.For<IObjectStorageService>(); storage.IsCloudStorage.Returns(true);
        var id = Guid.NewGuid(); var key = $"foodai/{id:N}.jpg";
        storage.GetFoodPhotoReadUrlAsync(key, TimeSpan.FromMinutes(5), Arg.Any<CancellationToken>()).Returns("https://s3.example/photo?X-Amz-Signature=test");
        var signer = new NutritionPhotoUrlSigner(storage, new("clave-sintetica"), new HttpContextAccessor(), new Clock());
        Assert.Contains("X-Amz-Signature", (await signer.SignAsync(id, key, default))!.Url);
        await storage.DidNotReceive().GetPreSignedUrlAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }
}
