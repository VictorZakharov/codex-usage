using System.Globalization;
using System.Text.Json;
using CodexUsage.Errors;

namespace CodexUsage.Authentication;

public static class CodexCredentialParser
{
    public static CodexCredentials Parse(string json, string authFilePath)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (TryReadString(root, "OPENAI_API_KEY", out var apiKey) && !string.IsNullOrWhiteSpace(apiKey))
            {
                throw new CodexUsageException(
                    CodexUsageErrorKind.UnsupportedAuthentication,
                    "Codex is signed in with an API key. ChatGPT plan usage requires `codex login`.");
            }

            if (!root.TryGetProperty("tokens", out var tokens) || tokens.ValueKind != JsonValueKind.Object)
            {
                throw MissingTokens();
            }

            var accessToken = ReadEither(tokens, "access_token", "accessToken");
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                throw MissingTokens();
            }

            var refreshToken = ReadEither(tokens, "refresh_token", "refreshToken");
            var idToken = ReadEither(tokens, "id_token", "idToken");
            var accountId = ReadEither(tokens, "account_id", "accountId");
            var lastRefresh = ParseTimestamp(root, "last_refresh");

            return new CodexCredentials(
                accessToken,
                refreshToken,
                idToken,
                accountId,
                lastRefresh,
                authFilePath);
        }
        catch (CodexUsageException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new CodexUsageException(
                CodexUsageErrorKind.InvalidResponse,
                "Codex auth.json is not valid JSON.",
                exception);
        }
    }

    private static CodexUsageException MissingTokens() => new(
        CodexUsageErrorKind.NotSignedIn,
        "No Codex OAuth login was found. Sign in to Codex, then refresh.");

    private static string? ReadEither(JsonElement element, string snakeCase, string camelCase)
    {
        return TryReadString(element, snakeCase, out var value)
            ? value
            : TryReadString(element, camelCase, out value)
                ? value
                : null;
    }

    private static bool TryReadString(JsonElement element, string propertyName, out string? value)
    {
        value = null;
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return true;
    }

    private static DateTimeOffset? ParseTimestamp(JsonElement element, string propertyName)
    {
        if (!TryReadString(element, propertyName, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            raw,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var value)
            ? value
            : null;
    }
}
