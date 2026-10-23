using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace QbtAutoUpdate.Platform;

public enum TaskDialogIcon
{
    None = 0,
    Warning = -1,
    Error = -2,
    Information = -3,
    Shield = -4,
}

public sealed record TaskDialogButton(int Id, string Text, bool RequiresElevation = false);

/// <summary>One native Windows task dialog (comctl32 v6, enabled by the application manifest).</summary>
public sealed class TaskDialogPage
{
    public const int IdOk = 1;
    public const int IdCancel = 2;
    public const int IdYes = 6;
    public const int IdNo = 7;
    public const int IdClose = 8;

    public string Title { get; init; } = "QbtAutoUpdate";
    public string? MainInstruction { get; init; }
    public string? Content { get; init; }
    public string? Footer { get; init; }
    public TaskDialogIcon Icon { get; init; }
    public IReadOnlyList<TaskDialogButton> Buttons { get; init; } = [];
    /// <summary>Show <see cref="Buttons"/> as large command links.</summary>
    public bool CommandLinks { get; init; }
    public IReadOnlyList<TaskDialogButton> RadioButtons { get; init; } = [];
    public int DefaultRadioButton { get; init; }
    public int DefaultButton { get; init; }
    public bool Ok { get; init; }
    public bool Cancel { get; init; }
    public bool Close { get; init; }
    public bool Yes { get; init; }
    public bool No { get; init; }
    public string? VerificationText { get; init; }

    /// <summary>
    /// Progress mode: a marquee bar is shown while this runs; the dialog cannot be closed until it ends,
    /// then closes by itself.
    /// </summary>
    public Func<Task>? Work { get; init; }
}

public sealed record TaskDialogResult(int Button, int RadioButton, bool VerificationChecked);

public static unsafe partial class TaskDialog
{
    private const uint TDF_ENABLE_HYPERLINKS = 0x0001;
    private const uint TDF_ALLOW_DIALOG_CANCELLATION = 0x0008;
    private const uint TDF_USE_COMMAND_LINKS = 0x0010;
    private const uint TDF_SHOW_MARQUEE_PROGRESS_BAR = 0x0400;
    private const uint TDF_CALLBACK_TIMER = 0x0800;
    private const uint WM_USER = 0x0400;
    private const uint TDM_CLICK_BUTTON = WM_USER + 102;
    private const uint TDM_SET_PROGRESS_BAR_MARQUEE = WM_USER + 107;
    private const uint TDM_SET_BUTTON_ELEVATION_REQUIRED_STATE = WM_USER + 115;
    private const uint TDN_CREATED = 0;
    private const uint TDN_BUTTON_CLICKED = 2;
    private const uint TDN_TIMER = 4;
    private const int S_OK = 0;
    private const int S_FALSE = 1;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct TASKDIALOG_BUTTON
    {
        public int nButtonID;
        public char* pszButtonText;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct TASKDIALOGCONFIG
    {
        public uint cbSize;
        public nint hwndParent;
        public nint hInstance;
        public uint dwFlags;
        public uint dwCommonButtons;
        public char* pszWindowTitle;
        public nint pszMainIcon;
        public char* pszMainInstruction;
        public char* pszContent;
        public uint cButtons;
        public TASKDIALOG_BUTTON* pButtons;
        public int nDefaultButton;
        public uint cRadioButtons;
        public TASKDIALOG_BUTTON* pRadioButtons;
        public int nDefaultRadioButton;
        public char* pszVerificationText;
        public char* pszExpandedInformation;
        public char* pszExpandedControlText;
        public char* pszCollapsedControlText;
        public nint pszFooterIcon;
        public char* pszFooter;
        public delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint, int> pfCallback;
        public nint lpCallbackData;
        public uint cxWidth;
    }

    /// <summary>State shared with the native callback through a GCHandle.</summary>
    private sealed class CallbackState(TaskDialogPage page)
    {
        public TaskDialogPage Page { get; } = page;
        public Task? Running { get; set; }
        public bool Finished { get; set; }
    }

    public static TaskDialogResult Show(TaskDialogPage page)
    {
        var strings = new List<nint>();
        char* Text(string? value)
        {
            if (value is null)
                return null;
            nint pointer = Marshal.StringToHGlobalUni(value);
            strings.Add(pointer);
            return (char*)pointer;
        }

        var state = new CallbackState(page);
        GCHandle handle = GCHandle.Alloc(state);
        TASKDIALOG_BUTTON* buttons = stackalloc TASKDIALOG_BUTTON[Math.Max(page.Buttons.Count, 1)];
        TASKDIALOG_BUTTON* radios = stackalloc TASKDIALOG_BUTTON[Math.Max(page.RadioButtons.Count, 1)];
        try
        {
            for (int i = 0; i < page.Buttons.Count; i++)
                buttons[i] = new TASKDIALOG_BUTTON { nButtonID = page.Buttons[i].Id, pszButtonText = Text(page.Buttons[i].Text) };
            for (int i = 0; i < page.RadioButtons.Count; i++)
                radios[i] = new TASKDIALOG_BUTTON { nButtonID = page.RadioButtons[i].Id, pszButtonText = Text(page.RadioButtons[i].Text) };

            uint flags = TDF_ALLOW_DIALOG_CANCELLATION | TDF_ENABLE_HYPERLINKS;
            if (page.CommandLinks)
                flags |= TDF_USE_COMMAND_LINKS;
            if (page.Work is not null)
                flags = (flags & ~TDF_ALLOW_DIALOG_CANCELLATION) | TDF_SHOW_MARQUEE_PROGRESS_BAR | TDF_CALLBACK_TIMER;

            uint common = (page.Ok ? 0x01u : 0) | (page.Yes ? 0x02u : 0) | (page.No ? 0x04u : 0) | (page.Cancel ? 0x08u : 0) | (page.Close ? 0x20u : 0);
            if (page.Work is not null)
                common |= 0x08; // a Cancel button the callback keeps disabled-in-effect until the work ends

            var config = new TASKDIALOGCONFIG
            {
                cbSize = (uint)sizeof(TASKDIALOGCONFIG),
                dwFlags = flags,
                dwCommonButtons = common,
                pszWindowTitle = Text(page.Title),
                pszMainIcon = page.Icon == TaskDialogIcon.None ? 0 : (nint)(ushort)(short)page.Icon,
                pszMainInstruction = Text(page.MainInstruction),
                pszContent = Text(page.Content),
                cButtons = (uint)page.Buttons.Count,
                pButtons = page.Buttons.Count > 0 ? buttons : null,
                nDefaultButton = page.DefaultButton,
                cRadioButtons = (uint)page.RadioButtons.Count,
                pRadioButtons = page.RadioButtons.Count > 0 ? radios : null,
                nDefaultRadioButton = page.DefaultRadioButton,
                pszVerificationText = Text(page.VerificationText),
                pszFooter = Text(page.Footer),
                pfCallback = &Callback,
                lpCallbackData = GCHandle.ToIntPtr(handle),
            };

            int hr = TaskDialogIndirect(&config, out int button, out int radio, out int verification);
            Marshal.ThrowExceptionForHR(hr);
            state.Running?.GetAwaiter().GetResult(); // surface exceptions of the work, if any
            return new TaskDialogResult(button, radio, verification != 0);
        }
        finally
        {
            handle.Free();
            foreach (nint pointer in strings)
                Marshal.FreeHGlobal(pointer);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Callback(nint window, uint notification, nint wParam, nint lParam, nint data)
    {
        var state = (CallbackState)GCHandle.FromIntPtr(data).Target!;
        switch (notification)
        {
            case TDN_CREATED:
                foreach (TaskDialogButton button in state.Page.Buttons.Where(b => b.RequiresElevation))
                    SendMessage(window, TDM_SET_BUTTON_ELEVATION_REQUIRED_STATE, button.Id, 1);
                if (state.Page.Work is { } work)
                {
                    SendMessage(window, TDM_SET_PROGRESS_BAR_MARQUEE, 1, 30);
                    state.Running = Task.Run(work);
                }
                break;
            case TDN_TIMER when state.Running is { IsCompleted: true } && !state.Finished:
                state.Finished = true;
                SendMessage(window, TDM_CLICK_BUTTON, TaskDialogPage.IdCancel, 0);
                break;
            case TDN_BUTTON_CLICKED when state.Running is not null && !state.Finished:
                return S_FALSE; // keep the dialog open while the work runs
        }
        return S_OK;
    }

    [LibraryImport("comctl32.dll")]
    private static partial int TaskDialogIndirect(TASKDIALOGCONFIG* config, out int button, out int radioButton, out int verificationChecked);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial nint SendMessage(nint window, uint message, nint wParam, nint lParam);
}
