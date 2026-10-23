using System.Text;

namespace QbtAutoUpdate.Platform;

/// <summary>
/// The exe is a WinExe so that scheduled runs never flash a console window. When it is started
/// from a terminal it borrows the parent's console for its output instead.
/// </summary>
public static class ConsoleHost
{
    /// <summary>
    /// Attaches to the parent console, keeping output that is already redirected (to a file or a pipe).
    /// Returns true when console output will be visible somewhere.
    /// </summary>
    public static bool AttachToParent()
    {
        // Text is written in the console's code page (850 on a French Windows), which .NET only encodes
        // with this provider; without it accents come out wrong.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        nint output = NativeMethods.GetStdHandle(NativeMethods.STD_OUTPUT_HANDLE);
        nint error = NativeMethods.GetStdHandle(NativeMethods.STD_ERROR_HANDLE);
        bool redirected = output != 0 && output != -1;

        // Attached even when redirected: the shell reading the pipe decodes it with its console's code page,
        // and being attached makes .NET encode with that same code page. Attaching replaces the standard
        // handles with the console's, so redirected ones are put back.
        bool attached = NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS);
        if (redirected)
        {
            NativeMethods.SetStdHandle(NativeMethods.STD_OUTPUT_HANDLE, output);
            if (error != 0 && error != -1)
                NativeMethods.SetStdHandle(NativeMethods.STD_ERROR_HANDLE, error);
            return true;
        }
        if (!attached)
            return false;

        // The shell has already printed its prompt: start on a fresh line.
        Console.WriteLine();
        return true;
    }
}
