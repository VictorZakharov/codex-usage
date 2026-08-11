using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CodexUsage.Authentication;
using CodexUsage.Errors;

namespace CodexUsage.Networking;

public sealed class CodexUsageClient : IDisposable
{
    public static readonly Uri DefaultUsageEndpoint = new("https://chatgpt.com/backend-api/wham/usage");
    public static readonly Uri DefaultRefreshEndpoint = new("https://auth.openai.com/oauth/token");

    // Public client identifier used by the open-source Codex CLI OAuth flow.
    private const string CodexClientId = "app_EMoamEEZ73f0CkXaXp7hrann";

    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly Uri _usageEndpoint;
    private readonly Uri _refreshEndpoint;

    public CodexUsageClient(
        HttpClient? httpClient = null,
        Uri? usageEndpoint = null,
        Uri? refreshEndpoint = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _ownsClient = httpClient is null;
        _usageEndpoint = usageEndpoint ?? DefaultUsageEndpoint;
        _refreshEndpoint = refreshEndpoint ?? DefaultRefreshEndpoint;
    }

    public async Task<string> GetUsageJsonAsync(
        CodexCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _usageEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("CodexUsage/0.1");
        if (!string.IsNullOrWhiteSpace(credentials.AccountId))
        {
            request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", credentials.AccountId);
        }

        try
        {
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new CodexUsageException(
                    CodexUsageErrorKind.Unauthorized,
                    "The Codex login expired. Attempting to refresh it may resolve this.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new CodexUsageException(
                    CodexUsageErrorKind.Network,
                    $"Codex usage returned HTTP {(int)response.StatusCode}.");
            }

            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (CodexUsageException)
        {
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CodexUsageException(
                CodexUsageErrorKind.Network,
                "The Codex usage request timed out.");
        }
        catch (HttpRequestException exception)
        {
            throw new CodexUsageException(
                CodexUsageErrorKind.Network,
                "Codex usage could not be reached. Check your connection.",
                exception);
        }
    }

    public async Task<RefreshedTokens> RefreshAsync(
        CodexCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(credentials.RefreshToken))
        {
            throw new CodexUsageException(
                CodexUsageErrorKind.Unauthorized,
                "The Codex login has expired. Sign in to Codex again.");
        }

        var body = new Dictionary<string, string>
        {
            ["client_id"] = CodexClientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = credentials.RefreshToken,
            ["scope"] = "openid profile email",
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, _refreshEndpoint)
        {
            Content = JsonContent.Create(body),
        };

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new CodexUsageException(
                    CodexUsageErrorKind.Unauthorized,
                    "The Codex login could not be refreshed. Sign in to Codex again.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var root = document.RootElement;
            var accessToken = ReadRequiredString(root, "access_token");
            var refreshToken = ReadString(root, "refresh_token") ?? credentials.RefreshToken;
            var idToken = ReadString(root, "id_token") ?? credentials.IdToken;
            return new RefreshedTokens(accessToken, refreshToken, idToken);
        }
        catch (CodexUsageException)
        {
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CodexUsageException(
                CodexUsageErrorKind.Network,
                "Refreshing the Codex login timed out.");
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        {
            throw new CodexUsageException(
                CodexUsageErrorKind.Network,
                "The Codex login could not be refreshed.",
                exception);
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }

    private static string ReadRequiredString(JsonElement element, string propertyName)
    {
        return ReadString(element, propertyName)
            ?? throw new CodexUsageException(
                CodexUsageErrorKind.InvalidResponse,
                "The Codex login refresh returned an incomplete response.");
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}
