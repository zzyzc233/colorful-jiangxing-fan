$src = @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class PostQuitTool
{
    [StructLayout(LayoutKind.Sequential)]
    public struct THREADENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ThreadID;
        public uint th32OwnerProcessID;
        public int tpBasePri;
        public int tpDeltaPri;
        public uint dwFlags;
    }
    [DllImport("kernel32.dll")] public static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll")] public static extern bool Thread32First(IntPtr h, ref THREADENTRY32 e);
    [DllImport("kernel32.dll")] public static extern bool Thread32Next(IntPtr h, ref THREADENTRY32 e);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
    [DllImport("user32.dll")] public static extern bool PostThreadMessage(uint tid, uint msg, IntPtr w, IntPtr l);

    public static int PostQuitTo(uint targetPid)
    {
        const uint TH32CS_SNAPTHREAD = 0x4;
        var tids = new List<uint>();
        IntPtr h = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
        if (h == IntPtr.Zero || h == (IntPtr)(-1)) return -1;
        var e = new THREADENTRY32 { dwSize = (uint)Marshal.SizeOf(typeof(THREADENTRY32)) };
        if (Thread32First(h, ref e))
        {
            do
            {
                if (e.th32OwnerProcessID == targetPid) tids.Add(e.th32ThreadID);
                e.dwSize = (uint)Marshal.SizeOf(typeof(THREADENTRY32));
            } while (Thread32Next(h, ref e));
        }
        CloseHandle(h);
        int n = 0;
        foreach (var tid in tids)
        {
            // WM_QUIT = 0x0012：让 WinForms 消息循环正常退出，走 Dispose/快照复原
            if (PostThreadMessage(tid, 0x0012, IntPtr.Zero, IntPtr.Zero)) n++;
        }
        return n;
    }
}
'@
Add-Type -TypeDefinition $src -Language CSharp
$pid2 = [uint32]$args[0]
$n = [PostQuitTool]::PostQuitTo($pid2)
Write-Output "posted WM_QUIT to $n thread(s) of pid $pid2"
