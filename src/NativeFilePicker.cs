using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace BigAmbitionsMP
{
    internal static class NativeFilePicker
    {
        private const int ERROR_CANCELLED_HRESULT = unchecked((int)0x800704C7);
        private static readonly Guid CLSID_FileOpenDialog = new Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");
        private static readonly Guid IID_IShellItem = new Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe");

        public static string[] PickBugReportAttachments()
        {
            IFileOpenDialog? dialog = null;
            IShellItemArray? results = null;

            try
            {
                var dialogType = Type.GetTypeFromCLSID(CLSID_FileOpenDialog, throwOnError: true);
                dialog = (IFileOpenDialog)Activator.CreateInstance(dialogType)!;

                dialog.SetTitle("Attach files to the BigAmbitionsMP bug report");
                dialog.SetOptions(FileOpenOptions.FOS_FORCEFILESYSTEM |
                                  FileOpenOptions.FOS_FILEMUSTEXIST |
                                  FileOpenOptions.FOS_PATHMUSTEXIST |
                                  FileOpenOptions.FOS_ALLOWMULTISELECT |
                                  FileOpenOptions.FOS_NOCHANGEDIR);

                var filters = new[]
                {
                    new COMDLG_FILTERSPEC
                    {
                        pszName = "Useful report files",
                        pszSpec = "*.png;*.jpg;*.jpeg;*.webp;*.gif;*.mp4;*.mov;*.mkv;*.webm;*.avi;*.txt;*.log;*.json;*.zip"
                    },
                    new COMDLG_FILTERSPEC { pszName = "All files", pszSpec = "*.*" }
                };
                dialog.SetFileTypes((uint)filters.Length, filters);
                dialog.SetFileTypeIndex(1);

                TrySetDefaultFolder(dialog);

                int hr = dialog.Show(GetOwnerWindow());
                if (hr == ERROR_CANCELLED_HRESULT)
                    return Array.Empty<string>();
                if (hr < 0)
                    Marshal.ThrowExceptionForHR(hr);

                dialog.GetResults(out results);
                results.GetCount(out uint count);

                var files = new List<string>((int)Math.Min(count, 64));
                for (uint i = 0; i < count; i++)
                {
                    IShellItem? item = null;
                    IntPtr pathPtr = IntPtr.Zero;
                    try
                    {
                        results.GetItemAt(i, out item);
                        item.GetDisplayName(ShellItemDisplayName.SIGDN_FILESYSPATH, out pathPtr);
                        string? path = Marshal.PtrToStringUni(pathPtr);
                        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                            files.Add(path);
                    }
                    finally
                    {
                        if (pathPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(pathPtr);
                        if (item != null) Marshal.ReleaseComObject(item);
                    }
                }

                return files.ToArray();
            }
            finally
            {
                if (results != null) Marshal.ReleaseComObject(results);
                if (dialog != null) Marshal.ReleaseComObject(dialog);
            }
        }

        private static IntPtr GetOwnerWindow()
        {
            try
            {
                var handle = Process.GetCurrentProcess().MainWindowHandle;
                if (handle != IntPtr.Zero) return handle;
            }
            catch { }

            try
            {
                var handle = GetForegroundWindow();
                if (handle != IntPtr.Zero) return handle;
            }
            catch { }

            return IntPtr.Zero;
        }

        private static void TrySetDefaultFolder(IFileDialog dialog)
        {
            string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string folderPath = Directory.Exists(pictures) ? pictures : desktop;
            if (!Directory.Exists(folderPath)) return;

            IShellItem? folder = null;
            try
            {
                Guid iid = IID_IShellItem;
                SHCreateItemFromParsingName(folderPath, IntPtr.Zero, ref iid, out folder);
                dialog.SetDefaultFolder(folder);
            }
            catch { }
            finally
            {
                if (folder != null) Marshal.ReleaseComObject(folder);
            }
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
            IntPtr pbc,
            [In] ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct COMDLG_FILTERSPEC
        {
            [MarshalAs(UnmanagedType.LPWStr)] public string pszName;
            [MarshalAs(UnmanagedType.LPWStr)] public string pszSpec;
        }

        [Flags]
        private enum FileOpenOptions : uint
        {
            FOS_NOCHANGEDIR = 0x00000008,
            FOS_FORCEFILESYSTEM = 0x00000040,
            FOS_ALLOWMULTISELECT = 0x00000200,
            FOS_PATHMUSTEXIST = 0x00000800,
            FOS_FILEMUSTEXIST = 0x00001000
        }

        private enum ShellItemDisplayName : uint
        {
            SIGDN_FILESYSPATH = 0x80058000
        }

        [ComImport]
        [Guid("42F85136-DB7E-439C-85F1-E4075D135FC8")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            void SetFileTypes(uint cFileTypes, [MarshalAs(UnmanagedType.LPArray)] COMDLG_FILTERSPEC[] rgFilterSpec);
            void SetFileTypeIndex(uint iFileType);
            void GetFileTypeIndex(out uint piFileType);
            void Advise(IntPtr pfde, out uint pdwCookie);
            void Unadvise(uint dwCookie);
            void SetOptions(FileOpenOptions fos);
            void GetOptions(out FileOpenOptions pfos);
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
            void AddPlace(IShellItem psi, uint fdap);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
            void Close(int hr);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr pFilter);
        }

        [ComImport]
        [Guid("D57C7288-D4AD-4768-BE02-9D969532D960")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOpenDialog : IFileDialog
        {
            [PreserveSig] new int Show(IntPtr parent);
            new void SetFileTypes(uint cFileTypes, [MarshalAs(UnmanagedType.LPArray)] COMDLG_FILTERSPEC[] rgFilterSpec);
            new void SetFileTypeIndex(uint iFileType);
            new void GetFileTypeIndex(out uint piFileType);
            new void Advise(IntPtr pfde, out uint pdwCookie);
            new void Unadvise(uint dwCookie);
            new void SetOptions(FileOpenOptions fos);
            new void GetOptions(out FileOpenOptions pfos);
            new void SetDefaultFolder(IShellItem psi);
            new void SetFolder(IShellItem psi);
            new void GetFolder(out IShellItem ppsi);
            new void GetCurrentSelection(out IShellItem ppsi);
            new void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            new void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
            new void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
            new void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
            new void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
            new void GetResult(out IShellItem ppsi);
            new void AddPlace(IShellItem psi, uint fdap);
            new void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
            new void Close(int hr);
            new void SetClientGuid(ref Guid guid);
            new void ClearClientData();
            new void SetFilter(IntPtr pFilter);
            void GetResults(out IShellItemArray ppenum);
            void GetSelectedItems(out IShellItemArray ppsai);
        }

        [ComImport]
        [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem ppsi);
            void GetDisplayName(ShellItemDisplayName sigdnName, out IntPtr ppszName);
            void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            void Compare(IShellItem psi, uint hint, out int piOrder);
        }

        [ComImport]
        [Guid("B63EA76D-1F85-456F-A19C-48159EFA858B")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemArray
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppvOut);
            void GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv);
            void GetPropertyDescriptionList(ref Guid keyType, ref Guid riid, out IntPtr ppv);
            void GetAttributes(uint attribFlags, uint sfgaoMask, out uint psfgaoAttribs);
            void GetCount(out uint pdwNumItems);
            void GetItemAt(uint dwIndex, out IShellItem ppsi);
            void EnumItems(out IntPtr ppenumShellItems);
        }
    }
}
