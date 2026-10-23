using System.Globalization;
using System.Runtime.CompilerServices;

namespace QbtAutoUpdate.Tests;

internal static class TestCulture
{
    /// <summary>
    /// Tests assert on English messages and invariant number formats, whatever the language of the
    /// machine running them (the development PC is French). LocalizationTests checks French explicitly.
    /// </summary>
    [ModuleInitializer]
    internal static void UseInvariantCulture()
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
    }
}
