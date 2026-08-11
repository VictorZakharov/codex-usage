namespace CodexUsage.Errors;

public enum CodexUsageErrorKind
{
    NotSignedIn,
    UnsupportedAuthentication,
    Unauthorized,
    Network,
    InvalidResponse,
    CredentialUpdateFailed,
    Unknown,
}

public sealed class CodexUsageException : Exception
{
    public CodexUsageException(CodexUsageErrorKind kind, string message)
        : base(message)
    {
        Kind = kind;
    }

    public CodexUsageException(CodexUsageErrorKind kind, string message, Exception innerException)
        : base(message, innerException)
    {
        Kind = kind;
    }

    public CodexUsageErrorKind Kind { get; }
}
