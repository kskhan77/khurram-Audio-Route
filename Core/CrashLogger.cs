using System;
using System.IO;

namespace KhurramAudioRoute.Core
{
    /// <summary>
    /// Append-only file logger that survives a hard process crash. Used to
    /// triage native access violations and pinpoint which step in the audio
    /// pipeline failed when the process disappeared without a managed
    /// exception reaching the UI.
    /// </summary>
    public static class CrashLogger
    {
        private static readonly object _gate = new();
        private static readonly string _path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KhurramAudioRoute",
            "diagnostics.log");

        static CrashLogger()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                Log($"=== Session start — pid {Environment.ProcessId} ===");
            }
            catch { }
        }

        public static string LogPath => _path;

        public static void Log(string message)
        {
            try
            {
                lock (_gate)
                {
                    File.AppendAllText(_path,
                        $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
                }
            }
            catch { }
        }

        public static void LogException(string context, Exception ex)
        {
            Log($"{context}: {ex.GetType().Name} — {ex.Message}");
            Log(ex.StackTrace ?? "(no stack)");
            if (ex.InnerException != null)
                LogException(context + " (inner)", ex.InnerException);
        }
    }
}
