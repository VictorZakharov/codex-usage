using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexUsage.Errors;

namespace CodexUsage.Authentication;

public sealed class CodexCredentialStore
{
    private static readonly SemaphoreSlim WriteLock = new(1, 1);

    private readonly string _authFilePath;

    public CodexCredentialStore(string? codexHome = null)
    {
        var home = string.IsNullOrWhiteSpace(codexHome)
            ? Environment.GetEnvironmentVariable("CODEX_HOME")
            : codexHome;

        if (string.IsNullOrWhiteSpace(home))
        {
            var userProfile = Environment.GetEnvironmentVariable("USERPROFILE");
            if (string.IsNullOrWhiteSpace(userProfile))
            {
                userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }

            home = Path.Combine(userProfile, ".codex");
        }

        _authFilePath = Path.Combine(Path.GetFullPath(home), "auth.json");
    }

    public string AuthFilePath => _authFilePath;

    public async Task<CodexCredentials> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_authFilePath))
        {
            throw new CodexUsageException(
                CodexUsageErrorKind.NotSignedIn,
                "Codex auth.json was not found. Sign in to Codex, then refresh.");
        }

        try
        {
            var json = await ReadSharedAsync(_authFilePath, cancellationToken).ConfigureAwait(false);
            return CodexCredentialParser.Parse(json, _authFilePath);
        }
        catch (CodexUsageException)
        {
            throw;
        }
        catch (IOException exception)
        {
            throw new CodexUsageException(
                CodexUsageErrorKind.NotSignedIn,
                "Codex credentials could not be read. Close any setup process and try again.",
                exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new CodexUsageException(
                CodexUsageErrorKind.NotSignedIn,
                "Windows denied access to the Codex credential file.",
                exception);
        }
    }

    public async Task<CodexCredentials> SaveRefreshedAsync(
        CodexCredentials original,
        RefreshedTokens refreshed,
        CancellationToken cancellationToken = default)
    {
        await WriteLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var currentJson = await ReadSharedAsync(_authFilePath, cancellationToken).ConfigureAwait(false);
            var current = CodexCredentialParser.Parse(currentJson, _authFilePath);

            // Codex may have refreshed the file while our request was in flight. Its newer value wins.
            if (!string.Equals(current.RefreshToken, original.RefreshToken, StringComparison.Ordinal))
            {
                return current;
            }

            var root = JsonNode.Parse(currentJson)?.AsObject()
                ?? throw new CodexUsageException(
                    CodexUsageErrorKind.CredentialUpdateFailed,
                    "Codex credentials changed into an unsupported format during refresh.");

            var tokens = root["tokens"]?.AsObject()
                ?? throw new CodexUsageException(
                    CodexUsageErrorKind.CredentialUpdateFailed,
                    "Codex credentials no longer contain OAuth tokens.");

            SetCompatibleValue(tokens, "access_token", "accessToken", refreshed.AccessToken);
            SetCompatibleValue(tokens, "refresh_token", "refreshToken", refreshed.RefreshToken);
            if (!string.IsNullOrWhiteSpace(refreshed.IdToken))
            {
                SetCompatibleValue(tokens, "id_token", "idToken", refreshed.IdToken);
            }

            root["last_refresh"] = DateTimeOffset.UtcNow.ToString("O");

            var updatedJson = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            await AtomicReplaceAsync(_authFilePath, updatedJson, cancellationToken).ConfigureAwait(false);
            return CodexCredentialParser.Parse(updatedJson, _authFilePath);
        }
        catch (CodexUsageException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new CodexUsageException(
                CodexUsageErrorKind.CredentialUpdateFailed,
                "The refreshed Codex login could not be saved safely. Open Codex and sign in again.",
                exception);
        }
        finally
        {
            WriteLock.Release();
        }
    }

    private static void SetCompatibleValue(JsonObject tokens, string snakeCase, string camelCase, string value)
    {
        if (tokens.ContainsKey(camelCase) && !tokens.ContainsKey(snakeCase))
        {
            tokens[camelCase] = value;
        }
        else
        {
            tokens[snakeCase] = value;
        }
    }

    private static async Task<string> ReadSharedAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task AtomicReplaceAsync(
        string destination,
        string content,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(destination)
            ?? throw new IOException("Credential path has no parent directory.");
        var temporary = Path.Combine(directory, $".auth.codexusage.{Guid.NewGuid():N}.tmp");

        try
        {
            await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(false), cancellationToken)
                .ConfigureAwait(false);

            try
            {
                File.Replace(temporary, destination, null, ignoreMetadataErrors: true);
            }
            catch (PlatformNotSupportedException)
            {
                File.Move(temporary, destination, overwrite: true);
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
