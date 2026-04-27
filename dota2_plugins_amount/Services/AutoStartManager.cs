using System;
using Microsoft.Win32;

namespace Dota2NetWorth.Services
{
    /// <summary>
    /// 自启管理。提供开关，不再无条件强写注册表。
    /// </summary>
    internal static class AutoStartManager
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "Dota2NetWorth";

        public static void Apply(bool enabled, string exePath)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (key == null) return;
                    string existing = key.GetValue(ValueName) as string;
                    if (enabled)
                    {
                        if (!string.Equals(existing, exePath, StringComparison.OrdinalIgnoreCase))
                        {
                            key.SetValue(ValueName, exePath);
                            Logger.Info("自启已启用：" + exePath);
                        }
                    }
                    else
                    {
                        if (existing != null)
                        {
                            key.DeleteValue(ValueName, false);
                            Logger.Info("自启已关闭。");
                        }
                    }
                }
            }
            catch (Exception ex) { Logger.Warn("AutoStart 设置失败: " + ex.Message); }
        }
    }
}
