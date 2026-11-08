using System.Runtime.InteropServices;
using DeskTidy.Core;
using DeskTidy.Interop;
using DeskTidy.Shell;
using static DeskTidy.Interop.NativeMethods;

namespace DeskTidy.Shell;

internal sealed record DesktopItem(
    int Index,
    string Name,
    string ParsePath,
    POINT ViewPoint,
    POINT ScreenPoint,
    RECT MonitorRect,
    bool OnPrimaryWorkArea);

/// <summary>
/// 只读桌面视图。坐标链路：
///   IFolderView2::GetItemPosition  →  视图客户区坐标（Explorer 的坐标系）
///   ClientToScreen(视图窗口)        →  屏幕物理坐标（让 Windows 自己算，不猜）
///   减去画布窗口原点               →  画布坐标
/// 全程没有 DPI 乘法，也没有网格推算。
/// </summary>
internal sealed class DesktopViewReader : IDisposable
{
    private ShellInterop.IFolderView? _view;
    private IntPtr _viewHwnd;
    private IntPtr _desktopPidl;

    public IntPtr ViewWindow => _viewHwnd;
    public string ViewClass { get; private set; } = "";
    public RECT ViewRect { get; private set; }

    public bool IsOpen => _view is not null;

    public static DesktopViewReader? TryOpen(out string error)
    {
        var r = new DesktopViewReader();
        try
        {
            r.OpenCore();
            error = "";
            return r;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            AppLog.Exception("DesktopViewReader.TryOpen", ex);
            r.Dispose();
            return null;
        }
    }

    private void OpenCore()
    {
        var type = Type.GetTypeFromCLSID(ShellInterop.CLSID_ShellWindows, throwOnError: true)!;
        var shellWindows = (ShellInterop.IShellWindows)Activator.CreateInstance(type)!;
        AppLog.Info($"ShellWindows 已创建 count={SafeCount(shellWindows)}");

        object? dispatch = null;
        IntPtr var1 = IntPtr.Zero, var2 = IntPtr.Zero;
        int desktopHwnd = 0;
        try
        {
            // VARIANT 必须是真的指针（VT_EMPTY），传 NULL 会得到 RPC_X_NULL_REF_POINTER
            var1 = Marshal.AllocCoTaskMem(24);
            var2 = Marshal.AllocCoTaskMem(24);
            for (int i = 0; i < 24; i++) { Marshal.WriteByte(var1, i, 0); Marshal.WriteByte(var2, i, 0); }

            dispatch = shellWindows.FindWindowSW(var1, var2,
                ShellInterop.SWC_DESKTOP, out desktopHwnd, ShellInterop.SWFO_NEEDDISPATCH);
            AppLog.Info($"FindWindowSW(SWC_DESKTOP) OK hwnd=0x{desktopHwnd:X} dispatch={(dispatch?.GetType().Name ?? "null")}");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"FindWindowSW(SWC_DESKTOP) 失败 hr=0x{ex.HResult:X8} {ex.Message}");
            dispatch = null;
        }
        finally
        {
            if (var1 != IntPtr.Zero) Marshal.FreeCoTaskMem(var1);
            if (var2 != IntPtr.Zero) Marshal.FreeCoTaskMem(var2);
        }

        if (dispatch is null)
        {
            AppLog.Warn("改用枚举 IShellWindows 兜底");
            dispatch = FindDesktopByEnumeration(shellWindows);
        }

        if (dispatch is null)
            throw new InvalidOperationException("拿不到桌面 shell 窗口的 IDispatch");

        LogSupportedInterfaces("dispatch", dispatch);

        var provider = dispatch as ShellInterop.IServiceProvider
            ?? throw new InvalidOperationException("桌面窗口不支持 IServiceProvider");

        var sid = ShellInterop.SID_STopLevelBrowser;
        var iid = ShellInterop.IID_IShellBrowser;
        int hr = provider.QueryService(ref sid, ref iid, out var browserObj);
        if (hr < 0 || browserObj is null)
            throw new MarshalException($"QueryService(SID_STopLevelBrowser) 失败 hr=0x{hr:X8}");

        LogSupportedInterfaces("browser", browserObj);
        var browser = (ShellInterop.IShellBrowser)browserObj;

        browser.QueryActiveShellView(out var viewObj);
        if (viewObj is null)
            throw new InvalidOperationException("QueryActiveShellView 返回空");

        LogSupportedInterfaces("view", viewObj);

        if (viewObj is ShellInterop.IOleWindow ole)
        {
            _viewHwnd = ole.GetWindow();
            AppLog.Info($"视图窗口（IOleWindow）= 0x{_viewHwnd:X}");
        }

        // 跨进程的 IShellView 是 proxy 对象，通常不暴露 IOleWindow。
        // 退一步用 FindWindowSW 给的桌面顶层窗口，找它的 SHELLDLL_DefView 子窗口做坐标系基准。
        if (_viewHwnd == IntPtr.Zero)
        {
            _viewHwnd = FindDefView(desktopHwnd != 0 ? new IntPtr(desktopHwnd) : IntPtr.Zero);
            AppLog.Info($"视图窗口（DefView 兜底）= 0x{_viewHwnd:X}");
        }

        _view = viewObj as ShellInterop.IFolderView
            ?? throw new InvalidOperationException("活动视图不支持 IFolderView");

        if (_viewHwnd != IntPtr.Zero)
        {
            ViewClass = ClassNameOf(_viewHwnd);
            GetWindowRect(_viewHwnd, out var vr);
            ViewRect = vr;
        }

        ShellInterop.SHGetFolderLocation(IntPtr.Zero, ShellInterop.CSIDL_DESKTOP, IntPtr.Zero, 0, out _desktopPidl);
        AppLog.Info($"桌面视图就绪 hwnd=0x{_viewHwnd:X} class={ViewClass} rect={ViewRect.Left},{ViewRect.Top},{ViewRect.Width}x{ViewRect.Height}");
    }

    /// <summary>
    /// 找承载桌面图标的 SHELLDLL_DefView 窗口（只按类名枚举，不发任何消息、不碰 SysListView32）。
    /// 坐标换算以它为准：ClientToScreen(DefView, itemPoint)。
    /// </summary>
    private static IntPtr FindDefView(IntPtr host)
    {
        if (host != IntPtr.Zero)
        {
            var child = FindWindowExW(host, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (child != IntPtr.Zero) return child;
        }

        // 桌面有时挂在某个 WorkerW 下：遍历顶层窗口（只读枚举）
        var top = GetTopWindow(IntPtr.Zero);
        int guard = 0;
        while (top != IntPtr.Zero && guard++ < 4096)
        {
            var cls = ClassNameOf(top);
            if (cls == "WorkerW" || cls == "Progman")
            {
                var child = FindWindowExW(top, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (child != IntPtr.Zero) return child;
            }
            top = GetWindow(top, GW_HWNDNEXT);
        }
        return IntPtr.Zero;
    }

    private static int SafeCount(ShellInterop.IShellWindows sw)
    {
        try { return sw.Count; } catch { return -1; }
    }

    /// <summary>打印这个 COM 对象到底支持哪些接口——定位"拿到错对象"的问题。</summary>
    private static void LogSupportedInterfaces(string tag, object comObject)
    {
        IntPtr unk = IntPtr.Zero;
        try
        {
            unk = Marshal.GetIUnknownForObject(comObject);
        }
        catch
        {
            AppLog.Warn($"{tag}: 不是 COM 对象（{comObject.GetType().FullName}）");
            return;
        }

        var checks = new (string Name, Guid Iid)[]
        {
            ("IServiceProvider", new("6D5140C1-7436-11CE-8034-00AA006009FA")),
            ("IShellBrowser", new("000214E2-0000-0000-C000-000000000046")),
            ("IShellView", new("000214E3-0000-0000-C000-000000000046")),
            ("IOleWindow", new("00000114-0000-0000-C000-000000000046")),
            ("IFolderView", new("CDE725B0-CCC9-4519-917E-325D72FAB4CE")),
            ("IFolderView2", new("1AF3A467-214F-4298-908E-06B03E0B39F9")),
            ("IWebBrowser2", new("D30C1661-CDAF-11D0-8A3E-00C04FC9E26E")),
        };

        var ok = new List<string>();
        foreach (var (name, iid) in checks)
        {
            var g = iid;
            int hr = Marshal.QueryInterface(unk, ref g, out IntPtr p);
            if (hr == 0)
            {
                ok.Add(name);
                Marshal.Release(p);
            }
        }
        AppLog.Info($"{tag}: 支持 [{string.Join(", ", ok)}]");
    }

    /// <summary>兜底：遍历 IShellWindows，找那个能 QI 出 IFolderView 且 PIDL 是桌面的窗口。</summary>
    private static object? FindDesktopByEnumeration(ShellInterop.IShellWindows shellWindows)
    {
        int count = shellWindows.Count;
        AppLog.Info($"枚举 shell 窗口 count={count}");
        for (int i = 0; i < count; i++)
        {
            try
            {
                var item = shellWindows.Item(i);
                if (item is null) continue;
                if (item is not ShellInterop.IServiceProvider p) continue;
                var sid = ShellInterop.SID_STopLevelBrowser;
                var iid = ShellInterop.IID_IShellBrowser;
                if (p.QueryService(ref sid, ref iid, out var b) < 0 || b is null) continue;
                var browser = (ShellInterop.IShellBrowser)b;
                browser.QueryActiveShellView(out var v);
                if (v is ShellInterop.IFolderView) return item;
            }
            catch (Exception ex)
            {
                AppLog.Warn($"枚举第 {i} 个窗口失败：{ex.Message}");
            }
        }
        return null;
    }

    /// <summary>
    /// 桌面图标边长（物理像素）。这是资源管理器自己写进 Bags 的值，也是它绘制时用的值，
    /// 所以直接读它，不用拿屏幕去"量"。注意它不随 DPI 缩放：200% 下仍然是 48。
    /// </summary>
    public static int ReadIconSizePx()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\Shell\Bags\1\Desktop");
            if (key?.GetValue("IconSize") is int v && v >= 16 && v <= 256) return v;
        }
        catch { }
        return 48;
    }

    public POINT GetSpacing()
    {
        var pt = new POINT();
        try { _view?.GetSpacing(out pt); } catch { }
        if (pt.X <= 0 || pt.Y <= 0) { pt.X = 96; pt.Y = 96; }
        return pt;
    }

    public List<DesktopItem> ReadItems(RECT canvasOrigin)
    {
        var list = new List<DesktopItem>();
        if (_view is null) return list;

        _view.ItemCount(ShellInterop.SVGIO_ALLVIEW, out int count);
        AppLog.Info($"桌面条目数={count}");

        int hrItem = 0, hrPos = 0, hrName = 0;

        for (int i = 0; i < count; i++)
        {
            IntPtr pidl = IntPtr.Zero;
            try
            {
                try
                {
                    _view.Item(i, out pidl);
                }
                catch (Exception ex)
                {
                    hrItem = ex.HResult;
                    AppLog.Warn($"Item({i}) 失败 hr=0x{ex.HResult:X8} {ex.Message}");
                    continue;
                }
                if (pidl == IntPtr.Zero)
                {
                    AppLog.Warn($"Item({i}) 返回空 pidl");
                    continue;
                }

                Interop.NativeMethods.POINT viewPt;
                try
                {
                    _view.GetItemPosition(pidl, out viewPt);
                }
                catch (Exception ex)
                {
                    hrPos = ex.HResult;
                    AppLog.Warn($"GetItemPosition({i}) 失败 hr=0x{ex.HResult:X8} {ex.Message}");
                    continue;
                }

                var screenPt = viewPt;
                if (_viewHwnd != IntPtr.Zero)
                    ShellInterop.ClientToScreen(_viewHwnd, ref screenPt);

                string name;
                try { name = GetName(pidl); }
                catch (Exception ex) { hrName = ex.HResult; name = $"<0x{ex.HResult:X8}>"; }

                string path = GetPath(pidl);

                var mon = MonitorFromPoint(screenPt, 0 /* MONITOR_DEFAULTTONULL */);
                var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
                var monRect = new RECT();
                if (mon != IntPtr.Zero && GetMonitorInfoW(mon, ref mi)) monRect = mi.rcMonitor;

                bool onCanvas = screenPt.X >= canvasOrigin.Left && screenPt.X < canvasOrigin.Right
                                && screenPt.Y >= canvasOrigin.Top && screenPt.Y < canvasOrigin.Bottom;

                list.Add(new DesktopItem(i, name, path, viewPt, screenPt, monRect, onCanvas));
            }
            finally
            {
                if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl);
            }
        }

        AppLog.Info($"读取完成 {list.Count}/{count}  hrItem=0x{hrItem:X8} hrPos=0x{hrPos:X8} hrName=0x{hrName:X8}");
        return list;
    }

    /// <summary>
    /// 把若干条目摆到指定坐标（按完整路径匹配）。用 IFolderView::SelectAndPositionItems +
    /// SVSI_POSITIONITEM —— shell 官方 API，不是改 listview 内存。
    /// 必须按路径匹配：资源管理器默认隐藏已知扩展名，显示名和文件名对不上。
    /// </summary>
    public int SetItemPositions(IReadOnlyDictionary<string, POINT> byPath)
    {
        if (_view is null || byPath.Count == 0) return 0;

        var matched = new List<(IntPtr Pidl, POINT Pt)>();
        try
        {
            _view.ItemCount(ShellInterop.SVGIO_ALLVIEW, out int count);
            for (int i = 0; i < count; i++)
            {
                IntPtr pidl = IntPtr.Zero;
                _view.Item(i, out pidl);
                if (pidl == IntPtr.Zero) continue;

                var path = GetPath(pidl);
                if (!string.IsNullOrEmpty(path) && byPath.TryGetValue(path, out var pt))
                    matched.Add((pidl, pt));
                else
                    Marshal.FreeCoTaskMem(pidl);
            }

            if (matched.Count == 0) return 0;

            int n = matched.Count;
            int ptSize = Marshal.SizeOf<Interop.NativeMethods.POINT>();
            IntPtr pidlArr = Marshal.AllocHGlobal(IntPtr.Size * n);
            IntPtr ptArr = Marshal.AllocHGlobal(ptSize * n);
            try
            {
                for (int i = 0; i < n; i++)
                {
                    Marshal.WriteIntPtr(pidlArr, i * IntPtr.Size, matched[i].Pidl);
                    Marshal.StructureToPtr(matched[i].Pt, ptArr + i * ptSize, false);
                }
                const uint SVSI_POSITIONITEM = 0x00000080;
                _view.SelectAndPositionItems((uint)n, pidlArr, ptArr, SVSI_POSITIONITEM);
                AppLog.Info($"SetItemPositions：已摆位 {n} 个条目");
                return n;
            }
            finally
            {
                Marshal.FreeHGlobal(pidlArr);
                Marshal.FreeHGlobal(ptArr);
            }
        }
        catch (Exception ex)
        {
            AppLog.Exception("SetItemPositions", ex);
            return 0;
        }
        finally
        {
            foreach (var m in matched) Marshal.FreeCoTaskMem(m.Pidl);
        }
    }

    private string GetName(IntPtr childPidl)
    {
        if (_desktopPidl == IntPtr.Zero) return "";
        IntPtr abs = IntPtr.Zero;
        IntPtr namePtr = IntPtr.Zero;
        try
        {
            abs = ShellInterop.ILCombine(_desktopPidl, childPidl);
            if (abs == IntPtr.Zero) return "";
            if (ShellInterop.SHGetNameFromIDList(abs, ShellInterop.SIGDN_NORMALDISPLAY, out namePtr) < 0)
                return "";
            return Marshal.PtrToStringUni(namePtr) ?? "";
        }
        finally
        {
            if (namePtr != IntPtr.Zero) Marshal.FreeCoTaskMem(namePtr);
            if (abs != IntPtr.Zero) ShellInterop.ILFree(abs);
        }
    }

    private string GetPath(IntPtr childPidl)
    {
        if (_desktopPidl == IntPtr.Zero) return "";
        IntPtr abs = IntPtr.Zero;
        try
        {
            abs = ShellInterop.ILCombine(_desktopPidl, childPidl);
            if (abs == IntPtr.Zero) return "";
            var sb = new System.Text.StringBuilder(1024);
            return SHGetPathFromIDListW(abs, sb) ? sb.ToString() : "";
        }
        finally
        {
            if (abs != IntPtr.Zero) ShellInterop.ILFree(abs);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SHGetPathFromIDListW(IntPtr pidl, System.Text.StringBuilder pszPath);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    public void Dispose()
    {
        if (_desktopPidl != IntPtr.Zero)
        {
            ShellInterop.ILFree(_desktopPidl);
            _desktopPidl = IntPtr.Zero;
        }
        if (_view is not null && Marshal.IsComObject(_view)) Marshal.ReleaseComObject(_view);
        _view = null;
    }
}

internal sealed class MarshalException(string message) : Exception(message);
