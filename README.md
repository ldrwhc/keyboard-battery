# V98 电量

面向 Windows 10/11 的 VGN V98 Pro V2 **2.4G 模式**电量托盘程序。单个 EXE，约 30 KB，使用 Windows 自带的 .NET Framework 4.x，无第三方运行库，无主窗口。

## 下载

前往 [最新 Release](https://github.com/ldrwhc/keyboard-battery/releases/latest)，下载 `V98Battery.exe` 或包含中文说明的 ZIP 压缩包。将程序放在固定目录后运行。

## 使用

下载后双击 `V98Battery.exe`；从源码构建则运行 `dist\V98Battery.exe`。首次启动自动启用当前用户登录时自启动，无需管理员权限。程序已运行时再次打开不会重复启动。

- 托盘显示左上键盘轮廓与右下叠加的数码管电量数字；鼠标悬停查看百分比、充电状态和更新时间。
- 每分钟查询一次，双击图标或右键「立即刷新」可手动查询。
- 监听 Windows HID 设备拔插通知，仅响应目标接收器。拔出时立即显示未知，插回后自动读取，并短暂重试以等待接口就绪。键盘休眠、关机但接收器仍插着时，仍由每分钟电量查询判断。
- 接收器拔插时显示托盘通知，同次拔插的多个接口事件仅提示一次。Windows 勿扰模式或系统通知设置可能影响提示显示。
- 数码管数字颜色从 100% 的绿色，经 60% 的黄色，渐变到 20% 的红色；低于 20% 时红色闪烁（亮灭各 0.6 秒）。充电时同样按实际电量显示颜色。
- 20% 及以下在本次低电量周期提醒一次。
- 休眠、拔出接收器或读取失败显示 `?`，唤醒键盘后会自动恢复。
- 右键可关闭「开机自启动」、复制诊断信息或退出。

Windows 可能把新图标放进任务栏的隐藏图标区域，可将它拖到托盘常显区域。

启用自启动后请保持 EXE 路径不变。移动后先在新位置启动，再关闭、重新打开自启动选项。删除前关闭自启动并退出即可。

## 自启动与常见问题

自启动写入当前用户注册表 `HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run`，名称为 `V98ProV2Battery`，在用户登录 Windows 后启动，不会在“启动”文件夹创建快捷方式。首次启动注册后仍需通过重新登录验证系统是否实际执行，系统的启动应用设置也可能影响执行。

- **图标显示 `?`**：确认使用 2.4G 模式、接收器已插入，按键唤醒键盘后右键立即刷新。官方驱动等软件占用接口时也可能读取失败。
- **没有拔插弹窗**：检查 Windows 通知设置和勿扰模式。
- **键盘关机后没有立即显示断开**：USB 接收器仍连接在电脑上，程序将在下一次查询时判断电量是否可读。
- **需要排查故障**：右键复制诊断信息，或运行下方的 `--diagnose` 命令。诊断文本可能含设备路径，分享前请检查。

## 适配与验证

仅向 `VID 320F / PID 5088` 的厂商自定义 HID 接口发送电量查询，不修改灯光、按键或固件。识别该接收器的报告长度后发送 Report ID 4 的 `00 00 1A 06 00 00 00` 查询，校验响应命令、电量范围和充电标志。不通过电压推算电量。

2026-10-17 在本机的 `VGN V98pro V2 2.4G Dongle` 上实测成功：返回 `04 00 00 1A 06 00 00 00 60 00 ...`，解析为 **96%，未充电**。其他硬件版本尚未验证。运行时内存与 EXE 文件体积不同。

## 构建与检查

在 PowerShell 中运行：

```powershell
.\build.ps1
Start-Process .\dist\V98Battery.exe -ArgumentList '--self-test' -Wait
Start-Process .\dist\V98Battery.exe -ArgumentList '--diagnose' -Wait
Get-Content .\dist\diagnostics.txt
```

`--self-test` 验证响应解析，成功退出码为 0。`--diagnose` 枚举目标接收器接口并查询电量，写入 EXE 同目录的 `diagnostics.txt`，不启动托盘或设置自启动。可用第二个参数指定诊断输出路径。诊断信息包含设备路径，分享前可自行检查。

## 协议来源

电量查询协议参考 [JKWTCN/BatteryMonitor 的 VgnHidProvider.cpp](https://github.com/JKWTCN/BatteryMonitor/blob/master/src/providers/hid/VgnHidProvider.cpp) 中的 Weisheng 实现，并在本机 V98 Pro V2 上验证。相关 MIT 许可见 `THIRD-PARTY-NOTICES.txt`。
