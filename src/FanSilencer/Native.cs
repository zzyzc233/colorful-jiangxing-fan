using System.Runtime.InteropServices;

namespace FanSilencer;

/// <summary>WinExe 下执行 CLI 子命令时把输出挂回父控制台。</summary>
internal static class Native
{
    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    public static void AttachParentConsole()
    {
        try { AttachConsole(AttachParentProcess); } catch { }
    }

    public static void DetachConsole()
    {
        try { FreeConsole(); } catch { }
    }
}
