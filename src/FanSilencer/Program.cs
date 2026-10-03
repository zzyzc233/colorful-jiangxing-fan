using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace FanSilencer;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // CLI 子命令：probe oem / probe oem-mode N
        if (args.Length > 0 && args[0] == "probe")
        {
            Logger.Init(Path.Combine(AppContext.BaseDirectory, "logs"));
            Native.AttachParentConsole();
            try
            {
                return ProbeCommands.Run(args);
            }
            catch (Exception ex)
            {
                Logger.Error("probe 未捕获异常: " + ex);
                return 3;
            }
            finally
            {
                Native.DetachConsole();
            }
        }

        Logger.Init(Path.Combine(AppContext.BaseDirectory, "logs"));

        using var mutex = new Mutex(true, @"Local\FanSilencer", out bool fresh);
        if (!fresh)
        {
            MessageBox.Show("将星风扇管家已在运行（见任务栏托盘图标）。", "提示");
            return 0;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }
}
