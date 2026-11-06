using System.Runtime.InteropServices;


/// <summary>
/// Represents the return value of a message box. This enum is identical to System.Windows.Forms.DialogResult.
/// The values correspond to the native IDOK, IDCANCEL, etc. constants.
/// </summary>
public enum DialogResult
{
    /// <summary>Nothing is returned from the dialog box. This means that the modal dialog continues running.</summary>
    None = 0,
    /// <summary>The dialog box return value is OK (usually sent from a button labeled OK).</summary>
    OK = 1,
    /// <summary>The dialog box return value is Cancel (usually sent from a button labeled Cancel).</summary>
    Cancel = 2,
    /// <summary>The dialog box return value is Abort (usually sent from a button labeled Abort).</summary>
    Abort = 3,
    /// <summary>The dialog box return value is Retry (usually sent from a button labeled Retry).</summary>
    Retry = 4,
    /// <summary>The dialog box return value is Ignore (usually sent from a button labeled Ignore).</summary>
    Ignore = 5,
    /// <summary>The dialog box return value is Yes (usually sent from a button labeled Yes).</summary>
    Yes = 6,
    /// <summary>The dialog box return value is No (usually sent from a button labeled No).</summary>
    No = 7
}

/// <summary>
/// Specifies which buttons to display on a message box. Corresponds to the MB_OK, MB_OKCANCEL, etc. Win32 constants.
/// </summary>
public enum MessageBoxButtons : uint
{
    /// <summary>The message box contains an OK button. This is the default.</summary>
    OK = 0x00000000,
    /// <summary>The message box contains OK and Cancel buttons.</summary>
    OKCancel = 0x00000001,
    /// <summary>The message box contains Abort, Retry, and Ignore buttons.</summary>
    AbortRetryIgnore = 0x00000002,
    /// <summary>The message box contains Yes, No, and Cancel buttons.</summary>
    YesNoCancel = 0x00000003,
    /// <summary>The message box contains Yes and No buttons.</summary>
    YesNo = 0x00000004,
    /// <summary>The message box contains Retry and Cancel buttons.</summary>
    RetryCancel = 0x00000005
}

/// <summary>
/// Specifies which icon to display in a message box. Corresponds to the MB_ICON* Win32 constants.
/// </summary>
public enum MessageBoxIcon : uint
{
    /// <summary>No icon is displayed.</summary>
    None = 0x00000000,
    /// <summary>A hand symbol. This symbol is typically used for serious errors.</summary>
    Hand = 0x00000010,
    /// <summary>A question mark symbol. This symbol is no longer recommended as it is not clear what it implies.</summary>
    Question = 0x00000020,
    /// <summary>An exclamation point symbol. This symbol is typically used for warnings.</summary>
    Exclamation = 0x00000030,
    /// <summary>An asterisk symbol. This symbol is typically used for informational messages.</summary>
    Asterisk = 0x00000040,

    // Aliases for common usage, matching System.Windows.Forms.MessageBoxIcon
    /// <summary>An alias for Hand.</summary>
    Stop = Hand,
    /// <summary>An alias for Hand.</summary>
    Error = Hand,
    /// <summary>An alias for Exclamation.</summary>
    Warning = Exclamation,
    /// <summary>An alias for Asterisk.</summary>
    Information = Asterisk
}

/// <summary>
/// Specifies which button is the default in a message box. Corresponds to the MB_DEFBUTTON* Win32 constants.
/// </summary>
public enum MessageBoxDefaultButton : uint
{
    /// <summary>The first button is the default button.</summary>
    Button1 = 0x00000000,
    /// <summary>The second button is the default button.</summary>
    Button2 = 0x00000100,
    /// <summary>The third button is the default button.</summary>
    Button3 = 0x00000200
}

/// <summary>
/// Specifies other display and behavior options for a message box. These can be combined with other flags.
/// </summary>
[Flags]
public enum MessageBoxOptions : uint
{
    /// <summary>No special options.</summary>
    None = 0x00000000,
    /// <summary>The message box is displayed on the active desktop.</summary>
    DefaultDesktopOnly = 0x00020000,
    /// <summary>The text is right-aligned.</summary>
    RightAlign = 0x00080000,
    /// <summary>Displays message and caption text using right-to-left reading order.</summary>
    RtlReading = 0x00100000,
    /// <summary>The message box becomes the foreground window.</summary>
    SetForeground = 0x00010000,
    /// <summary>The message box is displayed as a topmost window.</summary>
    TopMost = 0x00040000,
    /// <summary>The user must respond to the message box before continuing work in the current application. This is the default.</summary>
    AppModal = 0x00000000,
    /// <summary>All applications are suspended until the user responds to the message box. Use with caution.</summary>
    SystemModal = 0x00001000,
    /// <summary>Similar to SystemModal, but not useful for GUI applications.</summary>
    TaskModal = 0x00002000
}

/// <summary>
/// Provides a replacement for System.Windows.Forms.MessageBox using P/Invoke to call the native Win32 MessageBoxW function.
/// </summary>
public static class MessageBox
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(
        IntPtr hWnd,
        string lpText,
        string lpCaption,
        uint uType);

    /// <summary>
    /// Displays a message box with the specified text, caption, buttons, icon, default button, and options.
    /// </summary>
    /// <param name="text">The text to display in the message box.</param>
    /// <param name="caption">The text to display in the title bar of the message box.</param>
    /// <param name="buttons">One of the MessageBoxButtons values that specifies which buttons to display.</param>
    /// <param name="icon">One of the MessageBoxIcon values that specifies which icon to display.</param>
    /// <param name="defaultButton">One of the MessageBoxDefaultButton values that specifies the default button.</param>
    /// <param name="options">A bitwise combination of MessageBoxOptions values for additional display and behavior options.</param>
    /// <returns>One of the DialogResult values.</returns>
    public static DialogResult Show(
        string text,
        string caption = "",
        MessageBoxButtons buttons = MessageBoxButtons.OK,
        MessageBoxIcon icon = MessageBoxIcon.None,
        MessageBoxDefaultButton defaultButton = MessageBoxDefaultButton.Button1,
        MessageBoxOptions options = MessageBoxOptions.AppModal)
    {
        // Combine all the flags into a single uint
        uint flags = (uint)buttons | (uint)icon | (uint)defaultButton | (uint)options;

        // Call the native function. A handle of IntPtr.Zero makes the message box application-modal.
        int result = MessageBoxW(IntPtr.Zero, text, caption, flags);

        return (DialogResult)result;
    }

    // Example of an overload that accepts an owner window handle.
    /// <summary>
    /// Displays a message box in front of the specified window.
    /// </summary>
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
