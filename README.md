# SysInfoDash — 轻量实时系统信息桌面小窗

Windows 桌面悬浮窗系统监控工具（C# WinForms，单文件 exe，**无第三方依赖**）。窗口仅约 268px 宽，常驻内存约 60MB，可吸附到屏幕边缘、可贴边隐藏成把手、可置顶 / 半透明 / 鼠标穿透，是理想的“桌面挂件”。

## 功能一览

- **CPU**：利用率、当前频率、温度（依赖硬件传感器，部分平台显示 `N/A`）
- **内存**：使用量 / 总量
- **GPU / 显存**：GPU 利用率（核显与独显均支持）；专用显存与共享显存（当前 / 总量）
- **网络**：实时上传 / 下载速率，支持 `Mbps / MBps / GBps` 单位，条状图随流量实时伸缩（低速自动切换 `Kb/s` 显示）
- **磁盘**：每块固定磁盘一条「已用 / 总量」条形图，另有读写指示灯 + 实时读写速率
- **显示模式**：每个项目可独立切换「数字 / 条形 / 数字+条形」
- **显示项目开关**：右键菜单勾选显示项，窗口自动伸缩高度
- **主题**：深色 / 浅色
- **贴边吸附 + 把手**：拖到屏幕边缘吸附成细把手；把手上可显示你指定的**最多 2 个指标**（CPU / 内存 / GPU / 显存 / 下载 / 上传）的动态条，从中间向两端延伸
- **置顶显示**：可开关窗口是否总在最前
- **透明度**：`100% / 80% / 60% / 40% / 20%` 五档
- **鼠标穿透**：开启后窗口照常显示，但点击穿透到下层窗口（桌面挂件模式）
- **字号**：`6 ~ 12` 七档，窗口整体随字号缩放
- **托盘**：关闭窗口仅最小化到托盘；托盘图标右键可恢复 / 退出；**悬浮托盘图标显示实时系统信息摘要**
- **开机自启**：写 `HKCU\Run`；可选「开机启动后隐藏到托盘」
- 单实例运行、全局异常日志（`%APPDATA%\SysInfoDash\error.log`）

## 下载

- 最新可执行文件（单文件 `SysInfoDash.exe`，免安装，直接双击运行）：👉 [GitHub Releases](https://github.com/tenlon-m/SysInfoDash/releases/latest)
- 源码：本仓库 `src/` 目录，编译方法见下方「构建」。

## 文件说明

| 文件 | 说明 |
|---|---|
| `SysInfoDash.exe` | 最终可执行文件（由 `build.ps1` 编译生成，建议从 Releases 下载） |
| `build.ps1` | 一键编译脚本 |
| `src\Program.cs` | 入口：单实例互斥、DPI 感知、全局异常处理 |
| `src\MainForm.cs` | 主窗口：自绘面板、贴边 / 把手、托盘、右键菜单、主题、置顶 / 透明 / 穿透 |
| `src\SysInfo.cs` | 后台采集线程：CPU / 内存 / GPU / 显存 / 网络 / 磁盘全部指标 |
| `src\Config.cs` | 配置持久化（JSON，写入 `%APPDATA%\SysInfoDash\config.json`） |
| `src\NativeMethods.cs` | P/Invoke（DPI、内存等） |
| `app.manifest` | dpiAware 清单（编译时嵌入 exe） |
| `icon.ico` | 程序图标 |
| `tools\` | 开发辅助脚本（重新生成图标、抓图） |

## 构建

环境要求：Windows 自带的 .NET Framework 4.x（含 C# 编译器 `csc.exe`），**无需安装任何 SDK / 依赖**。

```powershell
# 1. 若 SysInfoDash 正在运行，先退出
Stop-Process -Name SysInfoDash -Force

# 2. 一键编译（输出 SysInfoDash.exe，约 56KB）
& "D:\MyApp\sysinfo\build.ps1"
```

编译引用的全部程序集（System / System.Windows.Forms 等）均为系统自带，exe 可直接拷到其他 Windows 10 / 11 机器运行。

## 使用

- **拖动**：按住窗口任意位置拖动；拖近屏幕边缘（≤14px）自动吸附成把手
- **吸附后显隐**：鼠标移开 → 把手收回；悬停把手 → 展开完整面板；移出 → 再次收回
- **右键菜单**（窗口或托盘图标）：
  - 显示项目 / 各项目「数字 / 条形 / 数字+条形」
  - 单位：`Mbps / MBps / GBps`
  - 主题：深色 / 浅色
  - 字号：极小(6) ~ 超大(12)
  - **把手显示**：勾选最多 2 个指标（CPU / 内存 / GPU / 显存 / 下载 / 上传），显示在贴边把手上
  - 置顶显示、鼠标穿透、透明度（100% ~ 20%）
  - 开机自启、开机启动后隐藏到托盘
  - 恢复窗口、退出
- **鼠标穿透**：开启后窗口不再接收鼠标事件，移动 / 操作窗口请改用**托盘图标右键菜单**关闭穿透
- **关闭按钮**：窗口「×」仅最小化到托盘；托盘「退出」才真正结束进程

## 配置

配置文件：`%APPDATA%\SysInfoDash\config.json`（首次运行时自动创建）。字段说明：

```json
{
  "Theme": "dark",            // dark | light
  "Unit": "MBps",             // Mbps | MBps | GBps
  "AutoStart": false,         // 开机自启
  "StartHidden": false,       // 开机启动后是否直接隐藏到托盘
  "TopMost": true,            // 是否置顶显示
  "Opacity": 1.0,             // 不透明度 0.2~1.0
  "ClickThrough": false,      // 鼠标穿透（桌面挂件模式）
  "FontSize": 9,              // 字号 6~12
  "DockMetrics": ["cpu", "netdown"],  // 贴边把手显示的指标（最多 2 个）
  "PosX": -1, "PosY": -1,     // 窗口位置（物理像素，-1 表示居中）
  "Show": { "cpu": true, ... "diskio": true },   // 各项目显示开关
  "Mode": { "cpu": "both", ... }                 // number | bar | both
}
```

## 实现要点

- **DPI 缩放**：窗口按物理像素布局（manifest dpiAware + AutoScaleMode.None）；自绘 Graphics 为 96dpi 逻辑坐标系，绘制前 `ScaleTransform(96/DpiX)` 映射，保证 125% / 150% 缩放下布局精确不裁剪。
- **显存数据**（兼容核显 / 独显）：当前使用取 WDDM 性能计数器 `GPU Adapter Memory` 的 `Dedicated Usage / Shared Usage`；总量按注册表 `HardwareInformation.qwMemorySize` → WMI `AdapterRAM` → `dxdiag /t` 解析 → 估算逐级降级；GPU 利用率 NVIDIA 用 `nvidia-smi`，其他用 `GPU Engine` 计数器按 luid 分组取最大；GPU 温度仅 NVIDIA 独显有。
- **CPU 温度**：WMI `Win32_PerfFormattedData_Counters_ThermalZoneInformation` → `MSAcpi_ThermalZoneTemperature` 两级降级；无传感器时显示 `N/A`。
- **CPU 频率**：性能计数器 `Processor Information\% Processor Performance`（当前）/ `~MHz`（基准）。
- **网络速率**：`NetworkInterface` 字节差值秒算；条状图按峰值归一化，数字按量级自适应 `B/K/M/G` 前缀。
- **磁盘读写**：`PhysicalDisk` 计数器判定活动灯 + 字节差值算速率。
- **鼠标穿透**：`user32.dll` 的 `SetWindowLong` 设置 `WS_EX_TRANSPARENT` 扩展样式。
- **单实例**：命名互斥体 `SysInfoDash_SingleInstance`。

## 已知限制

- CPU / GPU 温度取决于硬件是否暴露传感器；核显与部分主板显示 `N/A`。
- 鼠标穿透下窗口不可直接操作，需经托盘菜单解除。
- 窗口为无边框自绘，无系统圆角 / 阴影（设计如此，保持轻量）。

## 许可

[MIT License](LICENSE) © 2026 tenlon-m
