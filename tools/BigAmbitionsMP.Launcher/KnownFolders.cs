using System.Runtime.InteropServices;

namespace BigAmbitionsMP.Launcher;

internal static class KnownFolders
{
    private static readonly Guid LocalAppDataLow = new("A520A1A4-1780-4FF6-BD18-167343C5AF16");

    public static string GetLocalAppDataLow()
    {
        int result = SHGetKnownFolderPath(LocalAppDataLow, 0, IntPtr.Zero, out var pathPointer);
        if (result != 0)
            Marshal.ThrowExceptionForHR(result);

        try
        {
            return Marshal.PtrToStringUni(pathPointer)
                ?? throw new InvalidOperationException("Windows returned an empty LocalAppDataLow path.");
        }
        finally
        {
            Marshal.FreeCoTaskMem(pathPointer);
        }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rfid,
        uint dwFlags,
        IntPtr hToken,
        out IntPtr ppszPath);
}
