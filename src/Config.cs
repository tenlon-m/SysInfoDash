using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace SysInfoDash
{
    /// <summary>程序配置，持久化到 %APPDATA%\SysInfoDash\config.json。</summary>
    public class AppConfig
    {
        // 显示项 ID 常量
        public const string ItemCpu = "cpu";
        public const string ItemCpuSpeed = "cpumhz";
        public const string ItemCpuTemp = "cputemp";
        public const string ItemMem = "mem";
        public const string ItemGpu = "gpu";
        public const string ItemGpuMem = "gpuram";
        public const string ItemNet = "net";
        public const string ItemDisk = "disk";
        public const string ItemDiskIo = "diskio";

        // 显示模式
        public const string ModeNumber = "number";
        public const string ModeBar = "bar";
        public const string ModeBoth = "both";

        public string Theme = "dark";          // dark / light
        public string Unit = "Mbps";           // Mbps / MBps / GBps
        public float FontSize = 9f;            // 字号（基准 9，窗口随字号整体缩放）
        public bool AutoStart = false;
        public bool StartHidden = false;     // 开机启动后是否直接隐藏到托盘
        public bool TopMost = true;          // 是否置顶显示（默认总是置顶）
        public double Opacity = 1.0;         // 窗口不透明度 0.2~1.0
        public bool ClickThrough = false;    // 鼠标穿透：点击穿透到下层窗口（桌面挂件模式）
        public string[] DockMetrics = new string[] { "cpu", "netdown" };   // 靠边把手上显示的指标（最多 2 个）
        public int PosX = -1;
        public int PosY = -1;

        public Dictionary<string, bool> Show = new Dictionary<string, bool>();
        public Dictionary<string, string> Mode = new Dictionary<string, string>();

        public AppConfig()
        {
            Show[ItemCpu] = true;
            Show[ItemCpuSpeed] = true;
            Show[ItemCpuTemp] = true;
            Show[ItemMem] = true;
            Show[ItemGpu] = true;
            Show[ItemGpuMem] = true;
            Show[ItemNet] = true;
            Show[ItemDisk] = true;
            Show[ItemDiskIo] = true;

            Mode[ItemCpu] = ModeBoth;
            Mode[ItemCpuSpeed] = ModeBoth;
            Mode[ItemCpuTemp] = ModeBoth;
            Mode[ItemMem] = ModeBoth;
            Mode[ItemGpu] = ModeBoth;
            Mode[ItemGpuMem] = ModeBoth;
            Mode[ItemNet] = ModeBoth;
            Mode[ItemDisk] = ModeBar;   // 磁盘按需求默认横向条形图
            Mode[ItemDiskIo] = ModeBoth;
        }

        public static string ConfigPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "SysInfoDash", "config.json");
            }
        }

        public static AppConfig Load()
        {
            AppConfig c = new AppConfig();
            try
            {
                if (File.Exists(ConfigPath))
                {
                    JavaScriptSerializer ser = new JavaScriptSerializer();
                    AppConfig loaded = ser.Deserialize<AppConfig>(File.ReadAllText(ConfigPath, Encoding.UTF8));
                    if (loaded != null)
                    {
                        if (!String.IsNullOrEmpty(loaded.Theme)) c.Theme = loaded.Theme;
                        if (!String.IsNullOrEmpty(loaded.Unit)) c.Unit = loaded.Unit;
                        if (loaded.FontSize > 0) c.FontSize = loaded.FontSize;
                        c.AutoStart = loaded.AutoStart;
                        c.StartHidden = loaded.StartHidden;
                        c.TopMost = loaded.TopMost;
                        if (loaded.Opacity > 0.05 && loaded.Opacity <= 1.0) c.Opacity = loaded.Opacity;
                        c.ClickThrough = loaded.ClickThrough;
                        if (loaded.DockMetrics != null && loaded.DockMetrics.Length > 0)
                            c.DockMetrics = loaded.DockMetrics;
                        c.PosX = loaded.PosX;
                        c.PosY = loaded.PosY;
                        if (loaded.Show != null)
                            foreach (KeyValuePair<string, bool> kv in loaded.Show)
                                c.Show[kv.Key] = kv.Value;
                        if (loaded.Mode != null)
                            foreach (KeyValuePair<string, string> kv in loaded.Mode)
                                c.Mode[kv.Key] = kv.Value;
                    }
                }
            }
            catch
            {
                // 配置损坏时退回默认值
            }
            return c;
        }

        public void Save()
        {
            try
            {
                string dir = Path.GetDirectoryName(ConfigPath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                JavaScriptSerializer ser = new JavaScriptSerializer();
                File.WriteAllText(ConfigPath, ser.Serialize(this), Encoding.UTF8);
            }
            catch
            {
            }
        }
    }
}
