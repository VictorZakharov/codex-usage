using CodexUsage.Authentication;
using CodexUsage.Errors;
using CodexUsage.Models;
using CodexUsage.Networking;
using CodexUsage.Parsing;

namespace CodexUsage.Services;

public sealed class CodexUsageService : IDisposable
{
    private readonly CodexCredentialStore _credentialStore;
    private readonly CodexUsageClient _client;
    private readonly bool _ownsClient;

    public CodexUsageService(
        CodexCredentialStore? credentialStore = null,
        CodexUsageClient? client = null)
    {
        _credentialStore = credentialStore ?? new CodexCredentialStore();
        _client = client ?? new CodexUsageClient();
        _ownsClient = client is null;
    }

    public string AuthFilePath => _credentialStore.AuthFilePath;

    public async Task<UsageSnapshot> FetchAsync(CancellationToken cancellationToken = default)
    {
        var credentials = await _credentialStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        string json;

        try
        {
            json = await _client.GetUsageJsonAsync(credentials, cancellationToken).ConfigureAwait(false);
        }
        catch (CodexUsageException exception) when (exception.Kind == CodexUsageErrorKind.Unauthorized)
        {
            var refreshed = await _client.RefreshAsync(credentials, cancellationToken).ConfigureAwait(false);
            credentials = await _credentialStore.SaveRefreshedAsync(credentials, refreshed, cancellationToken)
                .ConfigureAwait(false);
            json = await _client.GetUsageJsonAsync(credentials, cancellationToken).ConfigureAwait(false);
        }

        var email = JwtClaimsReader.TryReadEmail(credentials.IdToken);
        return CodexUsageParser.Parse(json, DateTimeOffset.UtcNow, email);
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }
}
