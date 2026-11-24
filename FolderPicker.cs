using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BoothConfig
{
    /// <summary>The standard Windows folder dialog (.NET Framework only wraps the old tree-style one).</summary>
    static class FolderPicker
    {
        public static string Show(IWin32Window owner, string title, string startIn)
        {
            IFileOpenDialog dlg = null;
            try
            {
                dlg = (IFileOpenDialog)new FileOpenDialogClass();
                dlg.SetOptions(0x20 | 0x40 | 0x800); // PICKFOLDERS | FORCEFILESYSTEM | PATHMUSTEXIST
                dlg.SetTitle(title);
                if (!string.IsNullOrEmpty(startIn))
                {
                    var iid = typeof(IShellItem).GUID;
                    if (SHCreateItemFromParsingName(startIn, IntPtr.Zero, ref iid, out var start) == 0) dlg.SetFolder(start);
                }
                if (dlg.Show(owner.Handle) != 0) return null; // cancelled
                dlg.GetResult(out var item);
                item.GetDisplayName(0x80058000, out var path); // SIGDN_FILESYSPATH
                return path;
            }
            finally { if (dlg != null) Marshal.ReleaseComObject(dlg); }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHCreateItemFromParsingName(string path, IntPtr ctx, ref Guid iid, out IShellItem item);

        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        class FileOpenDialogClass { }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItem
        {
            void BindToHandler(IntPtr bc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem parent);
            void GetDisplayName(uint form, [MarshalAs(UnmanagedType.LPWStr)] out string name);
        }

        // Only the leading part of the vtable is declared; order matters.
        [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IFileOpenDialog
        {
            [PreserveSig] int Show(IntPtr owner);
            void SetFileTypes(uint n, IntPtr types);
            void SetFileTypeIndex(uint i);
            void GetFileTypeIndex(out uint i);
            void Advise(IntPtr events, out uint cookie);
            void Unadvise(uint cookie);
            void SetOptions(uint options);
            void GetOptions(out uint options);
            void SetDefaultFolder(IShellItem item);
            void SetFolder(IShellItem item);
            void GetFolder(out IShellItem item);
            void GetCurrentSelection(out IShellItem item);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
            void GetResult(out IShellItem item);
        }
    }
}
