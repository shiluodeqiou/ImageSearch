using System.Runtime.InteropServices;

namespace 以图搜图.Helpers;

/// <summary>文件夹选择结果。</summary>
/// <param name="Folders">选中的目录；用户取消时为预置的备用目录。</param>
/// <param name="Error">失败原因；成功或用户取消时为 null。</param>
public sealed record FolderPickResult(List<string> Folders, string? Error);

/// <summary>
/// 文件夹选择器。
/// WPF 自带的 OpenFileDialog 无法多选目录，因此调用 Shell 的 IFileOpenDialog，
/// 通过 FOS_PICKFOLDERS | FOS_ALLOWMULTISELECT 实现一次选择多个文件夹。
/// </summary>
public static class FolderPicker
{
    private const uint FOS_PICKFOLDERS = 0x00000020;
    private const uint FOS_FORCEFILESYSTEM = 0x00000040;
    private const uint FOS_ALLOWMULTISELECT = 0x00000200;
    private const uint FOS_PATHMUSTEXIST = 0x00000800;

    private const uint SIGDN_FILESYSPATH = 0x80058000;

    /// <summary>
    /// 弹出文件夹选择对话框。
    /// 失败时通过 <see cref="FolderPickResult.Error"/> 返回原因，不静默吞掉异常——
    /// 否则接口声明出错这类问题会表现为"对话框正常但选完没反应"，极难排查。
    /// </summary>
    public static FolderPickResult PickFolders(string title = "选择要索引的文件夹")
    {
        var results = new List<string>();
        IFileOpenDialog? dialog = null;
        try
        {
            dialog = (IFileOpenDialog)new FileOpenDialogRCW();

            var hr = dialog.GetOptions(out var options);
            if (hr != 0)
            {
                return new FolderPickResult(results, $"读取对话框选项失败（HRESULT 0x{hr:X8}）");
            }

            options |= FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_ALLOWMULTISELECT | FOS_PATHMUSTEXIST;

            hr = dialog.SetOptions(options);
            if (hr != 0)
            {
                return new FolderPickResult(results, $"设置对话框选项失败（HRESULT 0x{hr:X8}）");
            }

            dialog.SetTitle(title);

            hr = dialog.Show(IntPtr.Zero);
            if (hr != 0)
            {
                // 0x800704C7 = 用户取消；其余为失败
                if (unchecked((uint)hr) == 0x800704C7)
                {
                    return new FolderPickResult(results, null);
                }

                return new FolderPickResult(results, $"打开文件夹选择器失败（HRESULT 0x{hr:X8}）");
            }

            hr = dialog.GetResults(out var items);
            if (hr != 0 || items == null)
            {
                return new FolderPickResult(results, $"获取选择结果失败（HRESULT 0x{hr:X8}）");
            }

            try
            {
                items.GetCount(out var count);
                for (uint i = 0; i < count; i++)
                {
                    items.GetItemAt(i, out var item);
                    if (item == null)
                    {
                        continue;
                    }

                    try
                    {
                        item.GetDisplayName(SIGDN_FILESYSPATH, out var path);
                        if (!string.IsNullOrWhiteSpace(path))
                        {
                            results.Add(path);
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(item);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(items);
            }
        }
        catch (Exception ex)
        {
            // 不再静默：把真实原因交给界面提示
            return new FolderPickResult(results, $"{ex.GetType().Name}：{ex.Message}");
        }
        finally
        {
            if (dialog != null)
            {
                Marshal.ReleaseComObject(dialog);
            }
        }

        return new FolderPickResult(results, null);
    }

    [ComImport]
    [Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
    private class FileOpenDialogRCW
    {
    }

    [ComImport]
    [Guid("d57c7288-d4ad-4768-be02-9d969532d960")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        // IModalWindow
        [PreserveSig]
        int Show(IntPtr parent);

        // IFileDialog
        [PreserveSig]
        int SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
        [PreserveSig]
        int SetFileTypeIndex(uint iFileType);
        [PreserveSig]
        int GetFileTypeIndex(out uint piFileType);
        [PreserveSig]
        int Advise(IntPtr pfde, out uint pdwCookie);
        [PreserveSig]
        int Unadvise(uint dwCookie);
        [PreserveSig]
        int SetOptions(uint fos);
        [PreserveSig]
        int GetOptions(out uint pfos);
        [PreserveSig]
        int SetDefaultFolder(IShellItem psi);
        [PreserveSig]
        int SetFolder(IShellItem psi);
        [PreserveSig]
        int GetFolder(out IShellItem ppsi);
        [PreserveSig]
        int GetCurrentSelection(out IShellItem ppsi);
        [PreserveSig]
        int SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        [PreserveSig]
        int GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        [PreserveSig]
        int SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        [PreserveSig]
        int SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        [PreserveSig]
        int SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        [PreserveSig]
        int GetResult(out IShellItem ppsi);
        [PreserveSig]
        int AddPlace(IShellItem psi, int fdap);
        [PreserveSig]
        int SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        [PreserveSig]
        int Close(int hr);
        [PreserveSig]
        int SetClientGuid(ref Guid guid);
        [PreserveSig]
        int ClearClientData();
        [PreserveSig]
        int SetFilter(IntPtr pFilter);

        // IFileOpenDialog
        [PreserveSig]
        int GetResults(out IShellItemArray ppenum);
        [PreserveSig]
        int GetSelectedItems(out IShellItemArray ppsai);
    }

    /// <summary>
    /// IShellItem 的 IID 必须是 43826D1E-E718-42EE-BC55-A1E261C37BFE。
    /// 注意不要误用 IFileDialog 的 IID（42F85136-...），否则 GetItemAt 的
    /// QueryInterface 会返回 E_NOINTERFACE，表现为"选完文件夹没有反应"。
    /// </summary>
    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig]
        int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        [PreserveSig]
        int GetParent(out IShellItem ppsi);
        [PreserveSig]
        int GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        [PreserveSig]
        int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        [PreserveSig]
        int Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport]
    [Guid("B63EA76D-1F85-456F-A19C-48159EFA858B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemArray
    {
        [PreserveSig]
        int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        [PreserveSig]
        int GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv);
        [PreserveSig]
        int GetPropertyDescriptionList(IntPtr keyType, ref Guid riid, out IntPtr ppv);
        [PreserveSig]
        int GetAttributes(int attribFlags, uint sfgaoMask, out uint psfgaoAttribs);
        [PreserveSig]
        int GetCount(out uint pdwNumItems);
        [PreserveSig]
        int GetItemAt(uint dwIndex, out IShellItem ppsi);
        [PreserveSig]
        int EnumItems(out IntPtr ppenumShellItems);
    }
}
