using System;
using System.IO;
using System.Reflection;

namespace Dota2NetWorth.Services
{
    /// <summary>
    /// 解决「开机自启时工作目录非 exe 目录」的问题。
    /// 所有相对路径资源（item_price.json / config / 日志）都通过本类拼接绝对路径。
    /// </summary>
    internal static class PathProvider
    {
        public static readonly string BaseDir = ResolveBaseDir();

        public static string ItemPriceJson => Path.Combine(BaseDir, "item_price.json");
        public static string ConfigJson    => Path.Combine(BaseDir, "config.json");
        public static string LegacyConfig  => Path.Combine(BaseDir, "config.txt");

        private static string ResolveBaseDir()
        {
            try
            {
                var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
                string loc = asm.Location;
                if (!string.IsNullOrEmpty(loc)) return Path.GetDirectoryName(loc);
            }
            catch { }
            return AppDomain.CurrentDomain.BaseDirectory;
        }
    }
}
