using System.Text;
using System.Text.Json;

namespace CodexUsage.Authentication;

public static class JwtClaimsReader
{
    private static readonly string[] EmailClaims =
    [
        "email",
        "preferred_username",
        "https://api.openai.com/profile.email",
    ];

    public static string? TryReadEmail(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        try
        {
            var parts = token.Split('.');
            if (parts.Length < 2)
            {
                return null;
            }

            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            using var document = JsonDocument.Parse(json);

            foreach (var claim in EmailClaims)
            {
                if (document.RootElement.TryGetProperty(claim, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    var email = value.GetString();
                    if (!string.IsNullOrWhiteSpace(email) && email.Contains('@', StringComparison.Ordinal))
                    {
                        return email;
                    }
                }
            }
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            // Identity is optional and should never prevent usage from loading.
        }

        return null;
    }
}
