using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace Dota2NetWorth.Services
{
    /// <summary>
    /// 应用配置（JSON 格式）。兼容老 config.txt 的逗号格式。
    /// </summary>
    internal sealed class AppConfig
    {
        public double Left { get; set; }
        public double Top { get; set; }
        public bool IsLocked { get; set; }
        public string DotaPath { get; set; }
        /// <summary>是否随系统启动。默认 true 保持原行为；用户可改为 false。</summary>
        public bool AutoStart { get; set; }
        /// <summary>详细日志（DEBUG 级别 + GSI 原始数据 + 计算细节）。默认 true 便于排查问题。</summary>
        public bool VerboseLog { get; set; }

        public AppConfig()
        {
            Left = 200;
            Top = 60;
            IsLocked = true;
            AutoStart = true;
            VerboseLog = true;
        }

        public static AppConfig Load()
        {
            try
            {
                if (File.Exists(PathProvider.ConfigJson))
                {
                    string txt = File.ReadAllText(PathProvider.ConfigJson, Encoding.UTF8);
                    var jss = new JavaScriptSerializer();
                    var cfg = jss.Deserialize<AppConfig>(txt);
                    if (cfg != null) return Clamp(cfg);
                }
            }
            catch (Exception ex) { Logger.Warn("config.json 读取失败: " + ex.Message); }

            try
            {
                if (File.Exists(PathProvider.LegacyConfig))
                {
                    var parts = File.ReadAllText(PathProvider.LegacyConfig).Split(',');
                    var cfg = new AppConfig();
                    double l, t;
                    bool locked;
                    if (parts.Length >= 1 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out l)) cfg.Left = l;
                    if (parts.Length >= 2 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out t)) cfg.Top = t;
                    if (parts.Length >= 3 && bool.TryParse(parts[2], out locked)) cfg.IsLocked = locked;
                    if (parts.Length >= 7) cfg.DotaPath = parts[6];
                    else if (parts.Length >= 4) cfg.DotaPath = parts[3];
                    Logger.Info("已从老 config.txt 迁移配置。");
                    return Clamp(cfg);
                }
            }
            catch (Exception ex) { Logger.Warn("config.txt 兼容读取失败: " + ex.Message); }

            return new AppConfig();
        }

        private static AppConfig Clamp(AppConfig cfg)
        {
            if (cfg.Left < 0 || cfg.Left > System.Windows.SystemParameters.VirtualScreenWidth - 100) cfg.Left = 200;
            if (cfg.Top < 0 || cfg.Top > System.Windows.SystemParameters.VirtualScreenHeight - 40) cfg.Top = 60;
            return cfg;
        }

        public void Save()
        {
            try
            {
                var jss = new JavaScriptSerializer();
                File.WriteAllText(PathProvider.ConfigJson, jss.Serialize(this), Encoding.UTF8);
            }
            catch (Exception ex) { Logger.Warn("config.json 写入失败: " + ex.Message); }
        }
    }
}
