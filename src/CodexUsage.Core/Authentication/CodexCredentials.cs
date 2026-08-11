namespace CodexUsage.Authentication;

public sealed record CodexCredentials(
    string AccessToken,
    string? RefreshToken,
    string? IdToken,
    string? AccountId,
    DateTimeOffset? LastRefresh,
    string AuthFilePath);

public sealed record RefreshedTokens(
    string AccessToken,
    string RefreshToken,
    string? IdToken);
