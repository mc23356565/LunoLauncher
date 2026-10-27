using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace OpenLauncher.UI
{
    /// <summary>文件 / 文件夹选择（文件夹用 Vista 风格的 IFileOpenDialog，不依赖 WinForms）</summary>
    public static class Pickers
    {
        public static string PickFile(string title, string filter)
        {
            var d = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
            return d.ShowDialog(MainWindow.Current) == true ? d.FileName : null;
        }

        public static string SaveFile(string title, string filter, string fileName)
        {
            var d = new Microsoft.Win32.SaveFileDialog { Title = title, Filter = filter, FileName = fileName };
            return d.ShowDialog(MainWindow.Current) == true ? d.FileName : null;
        }

        public static string PickFolder(string title)
        {
            var dlg = (IFileOpenDialog)new FileOpenDialogRCW();
            try
            {
                dlg.GetOptions(out var opts);
                dlg.SetOptions(opts | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_NOCHANGEDIR);
                dlg.SetTitle(title);
                var hwnd = MainWindow.Current != null ? new WindowInteropHelper(MainWindow.Current).Handle : IntPtr.Zero;
                if (dlg.Show(hwnd) != 0) return null;
                dlg.GetResult(out var item);
                item.GetDisplayName(SIGDN_FILESYSPATH, out var path);
                return path;
            }
            catch { return null; }
            finally { Marshal.ReleaseComObject(dlg); }
        }

        const uint FOS_PICKFOLDERS = 0x20, FOS_FORCEFILESYSTEM = 0x40, FOS_NOCHANGEDIR = 0x8;
        const uint SIGDN_FILESYSPATH = 0x80058000;

        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        class FileOpenDialogRCW { }

        [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IFileOpenDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
            void SetFileTypeIndex(uint iFileType);
            void GetFileTypeIndex(out uint piFileType);
            void Advise(IntPtr pfde, out uint pdwCookie);
            void Unadvise(uint dwCookie);
            void SetOptions(uint fos);
            void GetOptions(out uint pfos);
            void SetDefaultFolder(IShellItem psi);
            void SetFolder(IShellItem psi);
            void GetFolder(out IShellItem ppsi);
            void GetCurrentSelection(out IShellItem ppsi);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
            void GetResult(out IShellItem ppsi);
            void AddPlace(IShellItem psi, int fdap);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
            void Close(int hr);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr pFilter);
            void GetResults(out IntPtr ppenum);
            void GetSelectedItems(out IntPtr ppsai);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem ppsi);
            void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
            void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            void Compare(IShellItem psi, uint hint, out int piOrder);
        }
    }
}
