using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using CoppAddresd.Application.Features.Agents.Vita;
using CoppAddresd.Domain.Exceptions;
using Microsoft.AspNetCore.WebUtilities;

namespace CoppAddresd.Api.Security;

/// <summary>Protección AES-GCM de propuestas con clave y propósito por capacidad.</summary>
public sealed class VitaProposalTokens : IVitaProposalTokens
{
    private static readonly byte[] TaskPurpose = Encoding.UTF8.GetBytes("CoppAddresd.Vita.TaskCompletion.v1");
    private static readonly byte[] CapturePurpose = Encoding.UTF8.GetBytes("CoppAddresd.Vita.Capture.v1");
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
    {
        if (!ValidSecret(value)) return false;
        var candidate = Convert.FromBase64String(value!);
        return otherSecrets.Where(s => !string.IsNullOrEmpty(s)).All(s =>
            !CryptographicOperations.FixedTimeEquals(candidate, Encoding.UTF8.GetBytes(s!))
            && (!ValidSecret(s) || !CryptographicOperations.FixedTimeEquals(candidate, Convert.FromBase64String(s!))));
    }

    public string Protect(VitaTaskProposal proposal) => Protect(proposal, key, TaskPurpose);

    public VitaTaskProposal Unprotect(string token)
    {
        try { return Unprotect<VitaTaskProposal>(token, key, TaskPurpose); }
        catch (Exception ex) when (IsInvalidToken(ex)) { throw new ForbiddenException("VITA_PROPOSAL_INVALID"); }
    }

    public static IVitaCaptureProposalTokens ForCapture(string sharedSecret)
    {
        if (!ValidSecret(sharedSecret))
            throw new InvalidOperationException("Vita:CaptureProposalSecret debe ser una clave dedicada de 32 bytes en Base64.");
        return new CaptureTokens(Convert.FromBase64String(sharedSecret));
    }

    private sealed class CaptureTokens(byte[] key) : IVitaCaptureProposalTokens
    {
        public string Protect(VitaCaptureProposal proposal) => VitaProposalTokens.Protect(proposal, key, CapturePurpose);

        public VitaCaptureProposal Unprotect(string token)
        {
            try { return VitaProposalTokens.Unprotect<VitaCaptureProposal>(token, key, CapturePurpose); }
            catch (Exception ex) when (IsInvalidToken(ex))
            { throw VitaCaptureRules.Invalid("token", "VITA_CAPTURE_TOKEN_INVALID"); }
        }
    }

    private static bool IsInvalidToken(Exception ex) => ex is CryptographicException or JsonException or ArgumentException or FormatException;

    private static string Protect<T>(T proposal, byte[] key, byte[] purpose)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(proposal);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(12);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[16];
            using var aes = new AesGcm(key, tag.Length);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, purpose);
            return string.Join('.', "v1", WebEncoders.Base64UrlEncode(nonce), WebEncoders.Base64UrlEncode(ciphertext), WebEncoders.Base64UrlEncode(tag));
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private static T Unprotect<T>(string token, byte[] key, byte[] purpose)
    {
        if (token.Length > 4096) throw new CryptographicException();
        var parts = token.Split('.');
        if (parts.Length != 4 || parts[0] != "v1") throw new CryptographicException();
        var nonce = DecodeCanonical(parts[1]);
        var ciphertext = DecodeCanonical(parts[2]);
        var tag = DecodeCanonical(parts[3]);
        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, purpose);
            return JsonSerializer.Deserialize<T>(plaintext) ?? throw new CryptographicException();
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private static byte[] DecodeCanonical(string value)
    {
        var bytes = WebEncoders.Base64UrlDecode(value);
        return WebEncoders.Base64UrlEncode(bytes) == value ? bytes : throw new CryptographicException();
    }
}

public sealed class VitaTaskProtectionSettings
{
    public bool TaskCompletionEnabled { get; set; }
    public string TaskProposalSecret { get; set; } = "";
}

public sealed class VitaCaptureSettings
{
    public bool CaptureEnabled { get; set; }
    public string CaptureProposalSecret { get; set; } = "";
}
