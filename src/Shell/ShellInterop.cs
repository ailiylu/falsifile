using System.Runtime.InteropServices;

namespace DeskTidy.Shell;

/// <summary>
/// 只读的 shell COM 互操作。全部是"读"：拿视图句柄、拿条目位置、拿显示名。
/// 不 SendMessage 到 SysListView32、不改条目、不触发刷新。
/// </summary>
internal static class ShellInterop
{
    public static readonly Guid CLSID_ShellWindows = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    public static readonly Guid IID_IShellWindows = new("85CB6900-4D95-11CF-960C-0080C7F4EE85");
    public static readonly Guid IID_IServiceProvider = new("6D5140C1-7436-11CE-8034-00AA006009FA");
    public static readonly Guid IID_IShellBrowser = new("000214E2-0000-0000-C000-000000000046");
    public static readonly Guid IID_IOleWindow = new("00000114-0000-0000-C000-000000000046");
    public static readonly Guid IID_IFolderView = new("CDE725B0-CCC9-4519-917E-325D72FAB4CE");
    public static readonly Guid SID_STopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");

    public const int SWC_DESKTOP = 8;
    public const int SWFO_NEEDDISPATCH = 1;
    public const uint SVGIO_ALLVIEW = 2;
    public const int SIGDN_NORMALDISPLAY = 0;
    public const int CSIDL_DESKTOP = 0;

    /// <summary>shell 的 IServiceProvider（同一个 IID 也用于标准 IServiceProvider）。</summary>
    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IServiceProvider
    {
        [PreserveSig]
        int QueryService(ref Guid guidService, ref Guid riid,
            [MarshalAs(UnmanagedType.IUnknown)] out object? ppvObject);
    }

    /// <summary>
    /// IShellWindows 是 dual 接口。这里必须按真实 vtable 顺序声明（IDispatch 的 7 个槽由 CLR 自动前置），
    /// 少一个/多一个都会错位——所以把 11 个方法都列出来，虽然只调用 Item / FindWindowSW / Count。
    /// </summary>
    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"),
     InterfaceType(ComInterfaceType.InterfaceIsDual)]
    public interface IShellWindows
    {
        int Count { get; }

        [return: MarshalAs(UnmanagedType.IDispatch)]
        object Item([MarshalAs(UnmanagedType.Struct)] object index);

        [return: MarshalAs(UnmanagedType.IUnknown)]
        object NewEnum();

        int Register([MarshalAs(UnmanagedType.IDispatch)] object pid, int hwnd, int swClass, int swfwOptions);

        int RegisterPending(int threadId, [MarshalAs(UnmanagedType.Struct)] ref object loc,
            [MarshalAs(UnmanagedType.Struct)] ref object locRoot, int swClass, int swfwOptions);

        void Revoke(int cookie);

        void OnNavigate(int cookie, [MarshalAs(UnmanagedType.Struct)] ref object loc);

        void OnActivated(int cookie, [MarshalAs(UnmanagedType.Struct)] ref object loc,
            [MarshalAs(UnmanagedType.Struct)] ref object locRoot,
            [MarshalAs(UnmanagedType.Struct)] ref object item,
            [MarshalAs(UnmanagedType.Struct)] ref object itemRoot, int active);

        [return: MarshalAs(UnmanagedType.IDispatch)]
        object FindWindowSW(IntPtr pvarLoc, IntPtr pvarLocRoot,
            int swClass, out int hwnd, int options);

        void OnCreated(int cookie, [MarshalAs(UnmanagedType.IDispatch)] object disp);

        void ProcessAttachDetach(int attach);
    }

    /// <summary>IShellBrowser：QueryActiveShellView 在第 12 号槽（IOleWindow 2 + 10）。</summary>
    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellBrowser
    {
        IntPtr GetWindow();
        void ContextSensitiveHelp(bool fEnterMode);
        void InsertMenusSB(IntPtr hmenuShared, IntPtr lpMenuWidths);
        void SetMenuSB(IntPtr hmenuShared, IntPtr holemenuRes, IntPtr hwndActiveObject);
        void RemoveMenusSB(IntPtr hmenuShared);
        void SetStatusTextSB([MarshalAs(UnmanagedType.LPWStr)] string? pszStatusText);
        void EnableModelessSB(bool bEnable);
        void TranslateAcceleratorSB(IntPtr pmsg, ushort wID);
        void BrowseObject(IntPtr pidl, uint wFlags);
        void GetViewStateStream(uint grfMode, out IntPtr ppStrm);
        void GetControlWindow(uint id, out IntPtr phwnd);
        void SendControlMsg(uint id, uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr pret);
        void QueryActiveShellView([MarshalAs(UnmanagedType.IUnknown)] out object? ppshv);
        void OnViewWindowActive(IntPtr pshv);
        void SetToolbarItems(IntPtr lpButtons, uint nButtons, uint uFlags);
    }

    /// <summary>IOleWindow：GetWindow 在 3 号槽。</summary>
    [ComImport, Guid("00000114-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IOleWindow
    {
        IntPtr GetWindow();
        void ContextSensitiveHelp(bool fEnterMode);
    }

    /// <summary>IFolderView：只声明到 SelectAndPositionItems（14 个），够读位置也够将来写位置。</summary>
    [ComImport, Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IFolderView
    {
        void GetCurrentViewMode(out uint viewMode);
        void SetCurrentViewMode(uint viewMode);
        void GetFolder(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object? shellFolder);
        void Item(int index, out IntPtr pidl);
        void ItemCount(uint flags, out int count);
        void Items(uint flags, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object? items);
        void GetSelectionMarkedItem(out int item);
        void GetFocusedItem(out int item);
        void GetItemPosition(IntPtr pidl, out Interop.NativeMethods.POINT pt);
        void GetSpacing(out Interop.NativeMethods.POINT pt);
        void GetDefaultSpacing(out Interop.NativeMethods.POINT pt);
        void GetAutoArrange();
        void SelectItem(int item, uint flags);
        void SelectAndPositionItems(uint cidl, IntPtr apidl, IntPtr apt, uint flags);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHGetNameFromIDList(IntPtr pidl, int sigdnName, out IntPtr ppszName);

    [DllImport("shell32.dll")]
    public static extern IntPtr ILCombine(IntPtr pidl1, IntPtr pidl2);

    [DllImport("shell32.dll")]
    public static extern void ILFree(IntPtr pidl);

    [DllImport("shell32.dll")]
    public static extern int SHGetFolderLocation(IntPtr hwndOwner, int nFolder, IntPtr hToken, uint dwFlags, out IntPtr ppidl);

    [DllImport("user32.dll")]
    public static extern bool ClientToScreen(IntPtr hWnd, ref Interop.NativeMethods.POINT lpPoint);
}
