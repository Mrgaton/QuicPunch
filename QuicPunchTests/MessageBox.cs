using System.Runtime.InteropServices;

namespace QuicPunchTests;

public enum DialogResult
{
    None = 0,
    OK = 1,
    Cancel = 2,
    Abort = 3,
    Retry = 4,
    Ignore = 5,
    Yes = 6,
    No = 7
}

public enum MessageBoxButtons : uint
{
    OK = 0x00000000,
    OKCancel = 0x00000001,
    AbortRetryIgnore = 0x00000002,
    YesNoCancel = 0x00000003,
    YesNo = 0x00000004,
    RetryCancel = 0x00000005
}

public enum MessageBoxIcon : uint
{
    None = 0x00000000,
    Hand = 0x00000010,
    Question = 0x00000020,
    Exclamation = 0x00000030,
    Asterisk = 0x00000040,

    Stop = Hand,
    Error = Hand,
    Warning = Exclamation,
    Information = Asterisk
}

public enum MessageBoxDefaultButton : uint
{
    Button1 = 0x00000000,
    Button2 = 0x00000100,
    Button3 = 0x00000200
}

[Flags]
public enum MessageBoxOptions : uint
{
    None = 0x00000000,
    DefaultDesktopOnly = 0x00020000,
    RightAlign = 0x00080000,
    RtlReading = 0x00100000,
    SetForeground = 0x00010000,
    TopMost = 0x00040000,
    AppModal = 0x00000000,
    SystemModal = 0x00001000,
    TaskModal = 0x00002000
}

public static class MessageBox
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(
        IntPtr hWnd,
        string lpText,
        string lpCaption,
        uint uType);

    public static DialogResult Show(
        string text,
        string caption = "",
        MessageBoxButtons buttons = MessageBoxButtons.OK,
        MessageBoxIcon icon = MessageBoxIcon.None,
        MessageBoxDefaultButton defaultButton = MessageBoxDefaultButton.Button1,
        MessageBoxOptions options = MessageBoxOptions.AppModal)
    {
        uint flags = (uint)buttons | (uint)icon | (uint)defaultButton | (uint)options;
        int result = MessageBoxW(IntPtr.Zero, text, caption, flags);
        return (DialogResult)result;
    }

    public static DialogResult Show(
        IntPtr owner,
        string text,
        string caption = "",
        MessageBoxButtons buttons = MessageBoxButtons.OK,
        MessageBoxIcon icon = MessageBoxIcon.None,
        MessageBoxDefaultButton defaultButton = MessageBoxDefaultButton.Button1,
        MessageBoxOptions options = MessageBoxOptions.AppModal)
    {
        uint flags = (uint)buttons | (uint)icon | (uint)defaultButton | (uint)options;
        int result = MessageBoxW(owner, text, caption, flags);
        return (DialogResult)result;
    }
}
