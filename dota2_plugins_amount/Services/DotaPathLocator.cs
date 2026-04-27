using System;
using System.IO;
using Microsoft.Win32;

namespace Dota2NetWorth.Services
{
    /// <summary>定位 Dota 2 安装路径并写入 GSI cfg（幂等）。</summary>
    internal static class DotaPathLocator
    {
        public const string GsiCfgFileName = "gamestate_integration_networth.cfg";
        public const string GsiCfgContent = @"""Dota 2 Net Worth""
{
    ""uri""           ""http://127.0.0.1:3000/""
    ""timeout""       ""5.0""
    ""buffer""        ""0.1""
    ""throttle""      ""0.1""
    ""heartbeat""     ""10.0""
    ""data""
    {
        ""map""       ""1""
        ""items""     ""1""
        ""player""    ""1""
        ""hero""      ""1""
    }
}
";

        public static string AutoDetect()
        {
            try
            {
                string steamPath = null;
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    if (key != null) steamPath = key.GetValue("SteamPath") as string;
                }

                if (!string.IsNullOrEmpty(steamPath))
                {
                    steamPath = steamPath.Replace("/", "\\");
                    string defaultLib = Path.Combine(steamPath, "steamapps", "common", "dota 2 beta");
                    if (Directory.Exists(defaultLib)) return defaultLib;

                    string vdfPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
                    if (File.Exists(vdfPath))
                    {
                        string currentLib = null;
                        foreach (string line in File.ReadAllLines(vdfPath))
                        {
                            if (line.Contains("\"path\""))
                            {
                                int start = line.IndexOf("\"path\"") + 6;
                                int q1 = line.IndexOf('"', start);
                                int q2 = line.IndexOf('"', q1 + 1);
                                if (q1 != -1 && q2 != -1)
                                    currentLib = line.Substring(q1 + 1, q2 - q1 - 1).Replace("\\\\", "\\");
                            }
                            if (line.Contains("\"570\"") && !string.IsNullOrEmpty(currentLib))
                            {
                                string maybe = Path.Combine(currentLib, "steamapps", "common", "dota 2 beta");
                                if (Directory.Exists(maybe)) return maybe;
                            }
                        }
                    }
                }

                const string regPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 570";
                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    using (var bk = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (var sub = bk.OpenSubKey(regPath))
                    {
                        var p = sub == null ? null : sub.GetValue("InstallLocation") as string;
                        if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) return p;
                    }
                }
            }
            catch (Exception ex) { Logger.Warn("AutoDetect Dota 路径失败: " + ex.Message); }
            return null;
        }

        /// <summary>
        /// 在指定 Dota 路径下创建/校验 GSI cfg。
        /// 幂等：若文件已存在且内容与目标一致，则不重写。
        /// </summary>
        public static bool EnsureGsiCfg(string path, out string outCfgPath, out string outCfgContent)
        {
            string gsiFolder = ResolveGsiFolder(path);
            outCfgPath = Path.Combine(gsiFolder, GsiCfgFileName);
            outCfgContent = GsiCfgContent;

            try
            {
                if (File.Exists(outCfgPath))
                {
                    string existing = File.ReadAllText(outCfgPath);
                    if (NormalizeCfg(existing) == NormalizeCfg(GsiCfgContent))
                    {
                        Logger.Info("GSI cfg 已存在且内容一致，跳过写入: " + outCfgPath);
                        return true;
                    }
                    Logger.Info("GSI cfg 内容已变化，覆盖写入: " + outCfgPath);
                }

                if (!Directory.Exists(gsiFolder)) Directory.CreateDirectory(gsiFolder);
                File.WriteAllText(outCfgPath, GsiCfgContent);
                Logger.Info("已写入 GSI cfg: " + outCfgPath);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error("写入 GSI cfg 失败（多为权限问题）", ex);
                return false;
            }
        }

        private static string ResolveGsiFolder(string path)
        {
            string gsiFolder = Path.Combine(path, "game", "dota", "cfg", "gamestate_integration");
            if (path.EndsWith("dota", StringComparison.OrdinalIgnoreCase) && Directory.Exists(Path.Combine(path, "cfg")))
                gsiFolder = Path.Combine(path, "cfg", "gamestate_integration");
            else if (path.EndsWith("game", StringComparison.OrdinalIgnoreCase) && Directory.Exists(Path.Combine(path, "dota", "cfg")))
                gsiFolder = Path.Combine(path, "dota", "cfg", "gamestate_integration");
            return gsiFolder;
        }

        /// <summary>归一化 cfg 内容用于比较：忽略行尾差异、空白行。</summary>
        private static string NormalizeCfg(string s)
        {
            if (s == null) return string.Empty;
            return s.Replace("\r\n", "\n").Replace("\r", "\n").Trim();
        }

        /// <summary>用户手动选 dota2.exe，向上回溯找 dota 2 beta 根目录。</summary>
        public static string ManuallyPick()
        {
            var ofd = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Dota 2 可执行文件 (dota2.exe)|dota2.exe|所有文件 (*.*)|*.*",
                Title = "请找到并选择 dota2.exe (通常位于 steamapps\\common\\dota 2 beta\\game\\bin\\win64\\)"
            };
            if (ofd.ShowDialog() != true) return null;

            string dir = Path.GetDirectoryName(ofd.FileName);
            var di = new DirectoryInfo(dir);
            while (di != null)
            {
                if (di.Name.Equals("dota 2 beta", StringComparison.OrdinalIgnoreCase) ||
                    (di.Name.Equals("game", StringComparison.OrdinalIgnoreCase) &&
                     Directory.Exists(Path.Combine(di.FullName, "dota", "cfg"))))
                {
                    return di.Name.Equals("game", StringComparison.OrdinalIgnoreCase) ? di.Parent.FullName : di.FullName;
                }
                di = di.Parent;
            }
            return dir;
        }
    }
}
