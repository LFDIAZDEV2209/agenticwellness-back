namespace CoppAddresd.Domain.Exceptions;

public sealed class AccountLegalConsentException(string code, int statusCode) : Exception(code)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}
