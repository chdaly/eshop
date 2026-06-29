using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace eShop.Catalog.API.Services;

internal static class CatalogSecurity
{
    private const int MaxUserIdLength = 128;
    private const int MaxSearchLogLength = 100;

    private static readonly Regex s_alphanumericUserIdPattern = new("^[a-zA-Z0-9]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex s_numericPiiPattern = new(@"\b\d{7,}\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex s_whitespacePattern = new(@"\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Validates authenticated user identifiers before they are used in Redis keys or background work so malformed claims cannot poison shared cache state.
    /// </summary>
    public static string ValidateUserId(string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var normalizedUserId = userId.Trim();
        if (!string.Equals(normalizedUserId, userId, StringComparison.Ordinal))
        {
            throw new ArgumentException("User id cannot include leading or trailing whitespace.", nameof(userId));
        }

        if (normalizedUserId.Length > MaxUserIdLength)
        {
            throw new ArgumentException($"User id cannot exceed {MaxUserIdLength} characters.", nameof(userId));
        }

        if (Guid.TryParse(normalizedUserId, out _) || s_alphanumericUserIdPattern.IsMatch(normalizedUserId))
        {
            return normalizedUserId;
        }

        throw new ArgumentException("User id format is invalid.", nameof(userId));
    }

    /// <summary>
    /// Redacts user identifiers to a short, stable representation so diagnostics remain useful without persisting the full identifier in logs.
    /// </summary>
    public static string FormatUserIdForLogging(string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return "<empty>";
        }

        var normalizedUserId = NormalizeForLogging(userId);
        var prefixLength = Math.Min(8, normalizedUserId.Length);
        var prefix = normalizedUserId[..prefixLength];
        return $"{prefix}...#{ComputeHash(normalizedUserId, 8)}";
    }

    /// <summary>
    /// Sanitizes free-form search text before logging so long inputs, control characters, and likely PII are not written to diagnostic sinks.
    /// </summary>
    public static string SanitizeSearchTextForLogging(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var normalizedText = NormalizeForLogging(text);
        if (normalizedText.Length == 0)
        {
            return "<empty>";
        }

        normalizedText = s_whitespacePattern.Replace(normalizedText, " ");
        if (LooksLikePii(normalizedText))
        {
            return $"sha256:{ComputeHash(normalizedText, 12)}";
        }

        return normalizedText.Length <= MaxSearchLogLength
            ? normalizedText
            : normalizedText[..MaxSearchLogLength];
    }

    private static bool LooksLikePii(string value) =>
        value.Contains('@', StringComparison.Ordinal) || s_numericPiiPattern.IsMatch(value);

    private static string NormalizeForLogging(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Trim())
        {
            if (!char.IsControl(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private static string ComputeHash(string value, int length)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash)[..length];
    }
}
