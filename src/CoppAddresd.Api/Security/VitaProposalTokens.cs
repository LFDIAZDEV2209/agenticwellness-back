using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using CoppAddresd.Application.Features.Agents.Vita;
using CoppAddresd.Domain.Exceptions;
using Microsoft.AspNetCore.WebUtilities;

namespace CoppAddresd.Api.Security;

/// <summary>Protección de propuestas de tareas, independiente de la firma de sesiones de voz en AI.</summary>
public sealed class VitaProposalTokens : IVitaProposalTokens
{
    private static readonly byte[] Purpose = Encoding.UTF8.GetBytes("CoppAddresd.Vita.TaskCompletion.v1");
    private readonly byte[] key;
    public VitaProposalTokens(string sharedSecret)
    {
        if (!ValidSecret(sharedSecret)) throw new InvalidOperationException("Vita:TaskProposalSecret debe ser una clave dedicada de 32 bytes en Base64.");
        key = Convert.FromBase64String(sharedSecret);
    }
    public static bool ValidSecret(string? value)
    {
        try { return !string.IsNullOrWhiteSpace(value) && Convert.FromBase64String(value).Length == 32; }
        catch (FormatException) { return false; }
    }
    public static bool DedicatedSecret(string? value, params string?[] otherSecrets)
        => ValidSecret(value) && otherSecrets.Where(s => !string.IsNullOrEmpty(s)).All(s =>
            s != value && !Convert.FromBase64String(value!).AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(s!)));

    public string Protect(VitaTaskProposal proposal)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(proposal);
        var nonce = RandomNumberGenerator.GetBytes(12); var ciphertext = new byte[plaintext.Length]; var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, Purpose);
        return string.Join('.', "v1", WebEncoders.Base64UrlEncode(nonce), WebEncoders.Base64UrlEncode(ciphertext), WebEncoders.Base64UrlEncode(tag));
    }
    public VitaTaskProposal Unprotect(string token)
    {
        try
        {
            if (token.Length > 4096) throw new CryptographicException();
            var parts = token.Split('.');
            if (parts.Length != 4 || parts[0] != "v1") throw new CryptographicException();
            var nonce = WebEncoders.Base64UrlDecode(parts[1]); var ciphertext = WebEncoders.Base64UrlDecode(parts[2]);
            var tag = WebEncoders.Base64UrlDecode(parts[3]); var plaintext = new byte[ciphertext.Length];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, Purpose);
            return JsonSerializer.Deserialize<VitaTaskProposal>(plaintext)
                ?? throw new CryptographicException();
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or ArgumentException or FormatException)
        { throw new ForbiddenException("VITA_PROPOSAL_INVALID"); }
    }
}

public sealed class VitaTaskProtectionSettings
{
    public bool TaskCompletionEnabled { get; set; }
    public string TaskProposalSecret { get; set; } = "";
}
