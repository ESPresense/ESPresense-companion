namespace ESPresense.Utils;

/// <summary>Helpers for putting untrusted values (topics, ids, URLs) into log messages.</summary>
public static class LogSanitizer
{
    /// <summary>
    /// Removes carriage returns and line feeds so an untrusted value cannot forge extra log lines.
    /// </summary>
    public static string Strip(string value) => value.Replace("\r", "").Replace("\n", "");

    /// <inheritdoc cref="Strip(string)"/>
    public static string? StripOrNull(string? value) => value == null ? null : Strip(value);
}
