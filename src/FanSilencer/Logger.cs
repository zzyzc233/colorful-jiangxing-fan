using System;
using System.IO;

namespace FanSilencer;

internal static class Logger
{
    private static readonly object Lock = new();
    public static string LogDir { get; private set; } = ".";

    public static void Init(string dir)
    {
        LogDir = dir;
        try { Directory.CreateDirectory(dir); } catch { }
    }

    public static void Info(string msg) => Write("INFO", msg);
    public static void Error(string msg) => Write("ERR ", msg);

    private static void Write(string level, string msg)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {msg}";
        try
        {
            lock (Lock)
            {
                Console.WriteLine(line);
                File.AppendAllText(Path.Combine(LogDir, "fansilencer.log"), line + Environment.NewLine);
            }
        }
        catch { }
    }
}
