using CoppAddresd.Api.Controllers;
using CoppAddresd.Api.Security;
using CoppAddresd.Application.Features.Nutrition.Commands;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Domain.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace CoppAddresd.UnitTests.Nutrition;

public sealed class NutritionCycleAttachmentSignatureTests
{
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-12-03T18:00:00Z"); }

    [Fact]
    public void CapacidadNutricion_NoAceptaFirmaGenerica_OtroId_NiExpiracionExcesiva()
    {
        var signatures = new StorageSignatureService("clave-sintetica-de-prueba"); var clock = new Clock(); var id = Guid.NewGuid();
        var end = clock.GetUtcNow().AddMinutes(5); var exp = end.ToUnixTimeSeconds().ToString();
        var signature = signatures.SignNutritionAttachment(id, end);
        Assert.True(signatures.ValidateNutritionAttachment(id, signature, exp, clock.GetUtcNow()));
        Assert.False(signatures.ValidateNutritionAttachment(Guid.NewGuid(), signature, exp, clock.GetUtcNow()));
        Assert.False(signatures.ValidateNutritionAttachment(id, signature, exp, end.AddSeconds(1)));
        Assert.False(signatures.ValidateNutritionAttachment(id, signatures.Sign($"nutrition-cycle-attachment/{id:D}", end), exp, clock.GetUtcNow()));
        Assert.False(signatures.ValidateNutritionAttachment(id, signatures.SignNutritionAttachment(id, end.AddSeconds(1)), end.AddSeconds(1).ToUnixTimeSeconds().ToString(), clock.GetUtcNow()));
        Assert.False(signatures.ValidateNutritionAttachment(id, signature, long.MaxValue.ToString(), clock.GetUtcNow()));
    }

    [Fact]
    public async Task Descarga_SinCapacidadNoLeeBD_YFirmaValidaSoloLeeSuOriginalPrivado()
    {
        var signatures = new StorageSignatureService("clave-sintetica-de-prueba"); var clock = new Clock(); var id = Guid.NewGuid(); var patient = Guid.NewGuid();
        var store = Substitute.For<INutritionCycleReviewStore>(); var storage = Substitute.For<IObjectStorageService>();
        var controller = new NutritionAttachmentContentController(signatures, store, storage, clock)
        { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
        Assert.IsType<UnauthorizedResult>(await controller.Download(id, "invalida", "0", default));
        await store.DidNotReceive().AttachmentAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        var key = $"documents/{patient:D}/{id:D}/original.png";
        store.AttachmentAsync(id, Arg.Any<CancellationToken>()).Returns(new ReviewAttachmentFile(patient,
            new NutritionCycleAttachment { Id = id, StorageKey = key, Kind = "image", ContentType = "image/png" }));
        storage.GetDocumentArchiveObjectAsync(key, Arg.Any<CancellationToken>()).Returns(new MemoryStream([137, 80, 78, 71]));
        var end = clock.GetUtcNow().AddMinutes(5);
        var result = await controller.Download(id, signatures.SignNutritionAttachment(id, end), end.ToUnixTimeSeconds().ToString(), default);
        var file = Assert.IsType<FileStreamResult>(result); Assert.Equal("image/png", file.ContentType); await file.FileStream.DisposeAsync();
        Assert.Equal("private, no-store", controller.Response.Headers.CacheControl.ToString());
        await storage.DidNotReceive().GetObjectAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
