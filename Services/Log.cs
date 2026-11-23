using System.IO;

namespace NotchApp.Services;

/// <summary>Tiny debug log, only written when NOTCHAPP_LOG=1 is set.</summary>
public static class Log
{
    static readonly bool Enabled = Environment.GetEnvironmentVariable("NOTCHAPP_LOG") == "1";
    static readonly string PathName = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Lagoon.log");

    public static void Write(string message)
    {
        if (!Enabled) return;
        try { File.AppendAllText(PathName, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}"); } catch { }
    }
}
