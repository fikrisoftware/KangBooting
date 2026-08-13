using System.Runtime.InteropServices;
using System.Text;

namespace KangBooting.App;

// Windows.Storage.Pickers.FileOpenPicker (the modern WinRT picker) does not work when the
// process runs elevated (Administrator) — its broker refuses to activate for elevated
// callers, throwing an unhandled COMException. Since this app requires Administrator
// (app.manifest) for raw disk access, we use the classic Win32 common dialog
// (GetOpenFileNameW, comdlg32.dll) instead, which works fine when elevated and needs no
// extra project SDK toggles (unlike WinForms/WPF, which conflict with UseWinUI's MSBuild
// targets).
internal static class Win32FileDialog
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OPENFILENAME
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public string lpstrFilter;
        public string? lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public IntPtr lpstrFile;
        public int nMaxFile;
        public string? lpstrFileTitle;
        public int nMaxFileTitle;
        public string? lpstrInitialDir;
        public string? lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public string? lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public string? lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    private const int OFN_FILEMUSTEXIST = 0x00001000;
    private const int OFN_PATHMUSTEXIST = 0x00000800;
    private const int OFN_EXPLORER = 0x00080000;

    [DllImport("comdlg32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetOpenFileName(ref OPENFILENAME ofn);

    public static string? PickIsoFile(IntPtr ownerHwnd)
    {
        const int maxPath = 4096;
        var fileBuffer = Marshal.AllocHGlobal(maxPath * sizeof(char));
        try
        {
            Marshal.WriteInt16(fileBuffer, 0);

            var ofn = new OPENFILENAME
            {
                lStructSize = Marshal.SizeOf<OPENFILENAME>(),
                hwndOwner = ownerHwnd,
                lpstrFilter = "ISO files (*.iso)\0*.iso\0All files (*.*)\0*.*\0\0",
                lpstrFile = fileBuffer,
                nMaxFile = maxPath,
                Flags = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_EXPLORER
            };

            if (!GetOpenFileName(ref ofn))
            {
                return null;
            }

            return Marshal.PtrToStringUni(fileBuffer);
        }
        finally
        {
            Marshal.FreeHGlobal(fileBuffer);
        }
    }
}
