namespace QbtAutoUpdate.Platform;

public sealed record ShortcutInfo(string Target, string Arguments, string WorkingDirectory, string IconPath, int IconIndex, string Description);

/// <summary>Reads and writes .lnk files through IShellLinkW / IPersistFile.</summary>
public static class Shortcuts
{
    private const int MaxPath = 32768;
    private const uint STGM_READ = 0;

    public static void Create(string path, ShortcutInfo shortcut)
    {
        var link = Com.Create<IShellLinkW>(ShellLinkIds.ClsidShellLink, ShellLinkIds.IidShellLinkW);
        link.SetPath(shortcut.Target);
        link.SetArguments(shortcut.Arguments);
        link.SetWorkingDirectory(shortcut.WorkingDirectory);
        link.SetIconLocation(shortcut.IconPath, shortcut.IconIndex);
        link.SetDescription(shortcut.Description);
        ((IPersistFile)link).Save(path, true);
    }

    public static unsafe ShortcutInfo Read(string path)
    {
        var link = Com.Create<IShellLinkW>(ShellLinkIds.ClsidShellLink, ShellLinkIds.IidShellLinkW);
        ((IPersistFile)link).Load(path, STGM_READ);

        char[] buffer = new char[MaxPath];
        fixed (char* text = buffer)
        {
            link.GetPath(text, MaxPath, 0, 0);
            string target = new(text);
            link.GetArguments(text, MaxPath);
            string arguments = new(text);
            link.GetWorkingDirectory(text, MaxPath);
            string directory = new(text);
            link.GetIconLocation(text, MaxPath, out int iconIndex);
            string icon = new(text);
            link.GetDescription(text, MaxPath);
            string description = new(text);
            return new ShortcutInfo(target, arguments, directory, icon, iconIndex, description);
        }
    }
}
