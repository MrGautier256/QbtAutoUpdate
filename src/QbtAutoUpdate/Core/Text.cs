using System.Globalization;
using System.Resources;

namespace QbtAutoUpdate.Core;

/// <summary>
/// User-facing text from Resources\Strings.resx (English) and Strings.fr.resx (French), picked from the
/// Windows display language. Diagnostic log lines stay in English.
/// </summary>
public static class T
{
    private static readonly ResourceManager Strings = new("QbtAutoUpdate.Resources.Strings", typeof(T).Assembly);

    public static string Get(string key) => Strings.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    public static string Format(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);

    /// <summary>For tests and for the French completeness check.</summary>
    internal static string? Get(string key, CultureInfo culture) => Strings.GetString(key, culture);

    internal static ResourceSet? Neutral => Strings.GetResourceSet(CultureInfo.InvariantCulture, true, false);
}
