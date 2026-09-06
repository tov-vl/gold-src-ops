namespace GoldSrcOps.Web.Security;

internal static class SecretFileReader
{
    public static string ReadRequiredSecret(
        string? directValue,
        string? filePath,
        bool allowDirectValue,
        string settingName)
    {
        var hasDirectValue = !string.IsNullOrEmpty(directValue);
        var hasFilePath = !string.IsNullOrWhiteSpace(filePath);

        if (hasDirectValue == hasFilePath)
        {
            throw new InvalidOperationException(
                $"Configure exactly one direct or file-backed value for '{settingName}'.");
        }

        if (hasDirectValue)
        {
            if (!allowDirectValue)
            {
                throw new InvalidOperationException(
                    $"Configuration value '{settingName}' must be file-backed outside Development.");
            }

            return Validate(directValue!, settingName);
        }

        try
        {
            return Validate(File.ReadAllText(filePath!), settingName);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"The file-backed value for '{settingName}' could not be read.",
                exception);
        }
    }

    private static string Validate(string value, string settingName)
    {
        var normalized = value.TrimEnd('\r', '\n');
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Contains('\r') || normalized.Contains('\n'))
        {
            throw new InvalidOperationException(
                $"Configuration value '{settingName}' must contain exactly one non-empty line.");
        }

        return normalized;
    }
}
