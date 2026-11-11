using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;

namespace bluetooth_turnon
{
    /// <summary>
    /// Thread-safe, non-blocking background logger with automatic log rotation at 1 MB
    /// and regex-based MAC address privacy masking.
    /// </summary>
    internal static class AsyncFileLogger
    {
        public enum LogLevel
        {
            Debug,
            Info,
            Warning,
            Error
        }

        private const long MaxLogFileSizeBytes = 1024 * 1024; // 1 MB
        private static readonly string _logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Connectify", "app_log.txt");
        private static readonly BlockingCollection<string> _logQueue = new BlockingCollection<string>(new ConcurrentQueue<string>(), 2000);
        private static readonly Thread _logWorkerThread;
        private static volatile bool _logWriterRunning = true;
        private static readonly Regex _macRegex = new Regex(@"(?<!-[0-9A-Fa-f]{4}-)(?<=^|[^0-9A-Fa-f]|BluetoothLE)(?:([0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}|[0-9A-Fa-f]{12})(?=[^0-9A-Fa-f]|$)", RegexOptions.Compiled);

        static AsyncFileLogger()
        {
            _logWorkerThread = new Thread(ProcessLogQueue)
            {
                IsBackground = true,
                Name = "ConnectifyLogWriter",
                Priority = ThreadPriority.BelowNormal
            };
            _logWorkerThread.Start();
            AppDomain.CurrentDomain.ProcessExit += (s, e) => Shutdown();
        }

        public static string MaskAddress(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            return _macRegex.Replace(input, m =>
            {
                string val = m.Value;
                if (val.Length == 12)
                {
                    // Mask first 8 hex characters: ********ABCD
                    return "********" + val.Substring(8);
                }
                else if (val.Length == 17)
                {
                    char sep = val[2];
                    return $"**{sep}**{sep}**{sep}**{sep}" + val.Substring(12);
                }
                return val;
            });
        }

        public static void Log(string msg) => Log(LogLevel.Info, msg);

        public static void Log(LogLevel level, string msg)
        {
            if (string.IsNullOrEmpty(msg) || !_logWriterRunning) return;
            string masked = MaskAddress(msg);
            string entry = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {masked}";
            _logQueue.TryAdd(entry);
        }

        public static void Log(BluetoothManager.LogLevel level, string msg)
        {
            Log((LogLevel)level, msg);
        }

        private static void ProcessLogQueue()
        {
            var batch = new List<string>(64);
            while (_logWriterRunning || _logQueue.Count > 0)
            {
                try
                {
                    string first;
                    if (_logQueue.TryTake(out first, 500))
                    {
                        batch.Add(first);
                        while (batch.Count < 100 && _logQueue.TryTake(out string next))
                        {
                            batch.Add(next);
                        }

                        WriteLogBatch(batch);
                        batch.Clear();
                    }
                }
                catch { }
            }
        }

        private static void WriteLogBatch(List<string> entries)
        {
            if (entries == null || entries.Count == 0) return;
            try
            {
                string dir = Path.GetDirectoryName(_logPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                if (File.Exists(_logPath))
                {
                    var fileInfo = new FileInfo(_logPath);
                    if (fileInfo.Length >= MaxLogFileSizeBytes)
                    {
                        try
                        {
                            string backupPath = Path.Combine(dir, "app_log.old.txt");
                            if (File.Exists(backupPath)) File.Delete(backupPath);
                            File.Move(_logPath, backupPath);
                        }
                        catch
                        {
                            File.WriteAllText(_logPath, string.Empty);
                        }
                    }
                }

                File.AppendAllLines(_logPath, entries);
            }
            catch { }
        }

        public static void Shutdown()
        {
            _logWriterRunning = false;
            try
            {
                _logQueue.CompleteAdding();
                _logWorkerThread?.Join(500);
            }
            catch { }
        }
    }
}
