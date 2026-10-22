namespace QbtAutoUpdate.Platform;

/// <summary>
/// The exe is a WinExe so that scheduled runs never flash a console window. When it is started
/// from a terminal it borrows the parent's console for its output instead.
/// </summary>
public static class ConsoleHost
{
    /// <summary>
    /// Attaches to the parent console unless output is already redirected (to a file or a pipe).
    /// Returns true when console output will be visible somewhere.
    /// </summary>
    public static bool AttachToParent()
    {
        nint handle = NativeMethods.GetStdHandle(NativeMethods.STD_OUTPUT_HANDLE);
        if (handle != 0 && handle != -1)
            return true;

        if (!NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS))
            return false;

        // The shell has already printed its prompt: start on a fresh line.
        Console.WriteLine();
        return true;
    }
}
