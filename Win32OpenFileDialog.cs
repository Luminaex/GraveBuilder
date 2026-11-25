using System.Runtime.InteropServices;
using GraveyardOrbit;

namespace GraveBuilder
{
    // Minimal P/Invoke wrapper around the classic Win32 "Open" common dialog
    // (GetOpenFileNameW, comdlg32.dll) for LOAD IMAGE's BROWSE button — not the newer
    // IFileOpenDialog COM interface, which needs CoInitialize/STA. This one has no apartment
    // requirement, so it's safe to call directly from a UI button's click handler: it blocks
    // the calling thread (Unity's main thread) until the dialog closes, same as any other
    // native modal, with no extra threading needed.
    internal static class Win32OpenFileDialog
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private class OpenFileName
        {
            public int lStructSize = Marshal.SizeOf(typeof(OpenFileName));
            public System.IntPtr hwndOwner;
            public System.IntPtr hInstance;
            public string lpstrFilter;
            public string lpstrCustomFilter;
            public int nMaxCustFilter;
            public int nFilterIndex;
            // Pre-sized so the native call has a buffer to write the chosen path back into.
            public string lpstrFile = new string('\0', 1024);
            public int nMaxFile = 1024;
            public string lpstrFileTitle;
            public int nMaxFileTitle;
            public string lpstrInitialDir;
            public string lpstrTitle;
            public int Flags;
            public short nFileOffset;
            public short nFileExtension;
            public string lpstrDefExt;
            public System.IntPtr lCustData;
            public System.IntPtr lpfnHook;
            public string lpTemplateName;
            public System.IntPtr pvReserved;
            public int dwReserved;
            public int FlagsEx;
        }

        [DllImport("comdlg32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool GetOpenFileName([In, Out] OpenFileName ofn);

        private const int OfnFileMustExist = 0x00001000;
        private const int OfnPathMustExist = 0x00000800;
        // Without this, a native common dialog resets the process's current directory to
        // wherever the user browsed to, which would break any of this plugin's own relative
        // file access (content/, parts/, etc.) for the rest of the session.
        private const int OfnNoChangeDir = 0x00000008;

        // Null if the user cancelled or the dialog failed. initialDir may be null; the OS
        // falls back to its own remembered last-used folder.
        public static string PickImage(string initialDir)
        {
            OpenFileName ofn = new OpenFileName
            {
                lpstrFilter = Loc.T("gravebuilder.file_dialog.filter_image_files", "Image files") + "\0*.png;*.jpg;*.jpeg\0" + Loc.T("gravebuilder.file_dialog.filter_all_files", "All files") + "\0*.*\0\0",
                lpstrTitle = Loc.T("gravebuilder.file_dialog.title", "Select an image to load"),
                lpstrInitialDir = initialDir,
                Flags = OfnFileMustExist | OfnPathMustExist | OfnNoChangeDir
            };
            if (!GetOpenFileName(ofn))
            {
                return null;
            }
            int nul = ofn.lpstrFile.IndexOf('\0');
            return nul >= 0 ? ofn.lpstrFile.Substring(0, nul) : ofn.lpstrFile;
        }
    }
}
