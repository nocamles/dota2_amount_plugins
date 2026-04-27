using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace Dota2NetWorth.Services
{
    /// <summary>
    /// 文件日志（线程安全）。
    /// 策略：
    ///   1. 按 5 分钟一个文件分块（文件名 app-yyyyMMdd-HHmm.log，HHmm 取 5 分钟对齐）。
    ///   2. 每次切换文件时清理 24 小时之前的旧日志。
    /// </summary>
    internal static class Logger
    {
        private static readonly object _lock = new object();
        private const int RotateMinutes = 5;
        private static readonly TimeSpan KeepDuration = TimeSpan.FromHours(24);
        private const string FilePrefix = "app-";
        private const string FileSuffix = ".log";

        /// <summary>是否记录 Debug 级别日志（GSI 原始数据 / 计算细节）。由 AppConfig.VerboseLog 控制。</summary>
        public static bool VerboseEnabled = true;

        private static string _currentPath;
        private static DateTime _currentSlotUtc = DateTime.MinValue;

        public static void Debug(string msg) { if (VerboseEnabled) Write("DEBUG", msg, null); }
        public static void Info(string msg)  { Write("INFO ", msg, null); }
        public static void Warn(string msg)  { Write("WARN ", msg, null); }
        public static void Error(string msg, Exception ex = null) { Write("ERROR", msg, ex); }

        private static void Write(string level, string msg, Exception ex)
        {
            try
            {
                lock (_lock)
                {
                    string path = GetCurrentLogPath();
                    string line = string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} [{1}] T{2,-3} {3}",
                        DateTime.Now, level, Thread.CurrentThread.ManagedThreadId, msg);
                    if (ex != null) line += Environment.NewLine + ex;
                    File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { /* 日志失败不能再抛 */ }
        }

        /// <summary>取当前日志文件路径；若进入新的 5 分钟槽位则切换并清理旧文件。</summary>
        private static string GetCurrentLogPath()
        {
            DateTime now = DateTime.Now;
            DateTime slot = new DateTime(now.Year, now.Month, now.Day, now.Hour,
                (now.Minute / RotateMinutes) * RotateMinutes, 0, DateTimeKind.Local);

            if (slot != _currentSlotUtc || _currentPath == null)
            {
                _currentSlotUtc = slot;
                _currentPath = Path.Combine(PathProvider.BaseDir,
                    FilePrefix + slot.ToString("yyyyMMdd-HHmm") + FileSuffix);
                CleanupOldLogs();
            }
            return _currentPath;
        }

        private static void CleanupOldLogs()
        {
            try
            {
                var dir = new DirectoryInfo(PathProvider.BaseDir);
                if (!dir.Exists) return;
                DateTime cutoff = DateTime.Now - KeepDuration;
                foreach (var f in dir.GetFiles(FilePrefix + "*" + FileSuffix))
                {
                    if (f.LastWriteTime < cutoff)
                    {
                        try { f.Delete(); } catch { }
                    }
                }
                // 兼容：清理重构前老 app.log/app.log.1
                foreach (var legacy in new[] { "app.log", "app.log.1" })
                {
                    string p = Path.Combine(PathProvider.BaseDir, legacy);
                    if (File.Exists(p))
                    {
                        try { File.Delete(p); } catch { }
                    }
                }
            }
            catch { }
        }
    }
}
