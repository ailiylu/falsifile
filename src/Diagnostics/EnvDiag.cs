using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using DeskTidy.Core;
using DeskTidy.Interop;

namespace DeskTidy.Diagnostics;

/// <summary>
/// 进程运行环境诊断：只回答一个问题——"为什么这个进程创建不了 Shell COM 对象"。
/// </summary>
internal static class EnvDiag
{
    [DllImport("ole32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void CoCreateInstance(ref Guid clsid, IntPtr outer, uint clsctx, ref Guid iid, out IntPtr ppv);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetCurrentProcess();

    [DllImport("user32.dll")]
    private static extern IntPtr GetThreadDesktop(uint threadId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetUserObjectInformationW(IntPtr hObj, int nIndex, StringBuilder pvInfo, int nLength, out int needed);

    public static int Run()
    {
        var sb = new StringBuilder();
        void Line(string s) { sb.AppendLine(s); AppLog.Info("[diag] " + s); }

        try
        {
            var id = WindowsIdentity.GetCurrent();
            Line($"identity={id.Name}  isSystem={id.IsSystem}  authType={id.AuthenticationType}");
            Line($"process={Environment.ProcessPath}  x64={Environment.Is64BitProcess}  session={System.Diagnostics.Process.GetCurrentProcess().SessionId}");
            Line($"os={Environment.OSVersion.VersionString}  clr={Environment.Version}");
            Line($"apartment={Thread.CurrentThread.GetApartmentState()}");

            try
            {
                using var self = System.Diagnostics.Process.GetCurrentProcess();
                IsProcessInJob(self.Handle, IntPtr.Zero, out bool inJob);
                Line($"inJob={inJob}");
            }
            catch (Exception ex) { Line($"inJob=? {ex.Message}"); }

            try
            {
                var desk = GetThreadDesktop(GetCurrentThreadId());
                var name = new StringBuilder(256);
                if (GetUserObjectInformationW(desk, 2 /* UOI_NAME */, name, 512, out _))
                    Line($"desktop={name}");
            }
            catch (Exception ex) { Line($"desktop=? {ex.Message}"); }

            var token = id.Token;
            Line($"tokenHandle=0x{token:X}");

            Line("--- CoCreateInstance 直接调用 ---");
            TryClsid(Line, "ShellWindows", new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"));
            TryClsid(Line, "Shell.Application", new Guid("13709620-C279-11CE-A49E-444553540000"));
            TryClsid(Line, "WScript.Shell", new Guid("72C24DD5-D70A-438B-8A42-98424B88AFB8"));
            TryClsid(Line, "FileOpenDialog", new Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7"));

            Line("--- .NET Type.GetTypeFromCLSID ---");
            foreach (var (name, clsid) in new (string, Guid)[]
                     {
                         ("ShellWindows", new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39")),
                         ("Shell.Application", new Guid("13709620-C279-11CE-A49E-444553540000")),
                     })
            {
                try
                {
                    var t = Type.GetTypeFromCLSID(clsid, true)!;
                    var o = Activator.CreateInstance(t);
                    Line($"{name}: OK {o?.GetType().FullName}");
                    if (o is not null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o);
                }
                catch (Exception ex)
                {
                    Line($"{name}: FAIL {ex.GetType().Name} hr=0x{ex.HResult:X8} {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Exception("EnvDiag", ex);
            sb.AppendLine("EXC: " + ex);
        }

        try
        {
            var p = Path.Combine(AppPaths.DataDir, "envdiag.txt");
            File.WriteAllText(p, sb.ToString(), new UTF8Encoding(true));
            Console.Out.Write(sb.ToString());
            Console.Out.Flush();
        }
        catch { }

        return 0;
    }

    private static void TryClsid(Action<string> line, string name, Guid clsid)
    {
        var iidUnk = new Guid("00000000-0000-0000-C000-000000000046");
        var clsidLocal = clsid;
        try
        {
            CoCreateInstance(ref clsidLocal, IntPtr.Zero, 1 /* CLSCTX_INPROC_SERVER */, ref iidUnk, out var ptr);
            line($"{name}: OK ptr=0x{ptr:X} (inproc)");
            Marshal.Release(ptr);
        }
        catch (Exception ex)
        {
            line($"{name}: FAIL(inproc) hr=0x{ex.HResult:X8} {ex.Message}");
        }

        try
        {
            var clsid2 = clsid;
            CoCreateInstance(ref clsid2, IntPtr.Zero, 4 /* CLSCTX_LOCAL_SERVER */, ref iidUnk, out var ptr2);
            line($"{name}: OK ptr=0x{ptr2:X} (local)");
            Marshal.Release(ptr2);
        }
        catch (Exception ex)
        {
            line($"{name}: FAIL(local) hr=0x{ex.HResult:X8} {ex.Message}");
        }
    }
}
