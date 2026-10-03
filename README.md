# 将星风扇管家 (JiangXing Fan Silencer)

七彩虹将星系列笔记本的风扇曲线管理工具。通过 Control Center 3.0 自带的官方驱动通道（InsydeDCHU / AcpiBridge）接管风扇调度，支持任意多点的自定义风扇曲线，全程不做底层端口 IO。

![界面截图](docs/screenshot.png)

## 功能特性

- **多点风扇曲线**：CPU / GPU 各自独立，最多 8 个可调节点 —— 拖动调整、双击空白加点、右键删点
- **温度来源讲道理**：GPU 温度走显卡驱动官方接口（NVML/ADLX，与游戏加加同源）；CPU 温度用 EC（与风扇固件实际依据一致，待机时与游戏加加基本吻合）；另有单帧毛刺滤波 + 15-110°C 物理范围校验 + 开机 90 秒预热保护
- **分区换挡写入**：EC 固件风扇表只接受 2 个可调点，程序按当前温度所在区间动态换挡写入，等效实现平滑的多点曲线
- **三层安全兜底**：
  - 启动时快照 CC3.0 当前的风扇调度，退出时原样复原
  - CPU/GPU ≥ 97°C 自动切全速散热，回落 4 秒自动恢复曲线
  - 曲线末端固定 100°C → 100%，即使程序异常退出，风扇仍会随温度升到全速
- **温度毛刺滤波**：剔除 EC 遥测偶发的单帧离谱读数（曾见瞬时 119°C 假值），避免误触发与显示惊吓
- 三种模式：静音曲线（自定义）/ 固件静音 / 自动（原厂）
- 开机自启（计划任务）、托盘常驻、日志记录

## 适用范围

| 项目 | 要求 |
| --- | --- |
| 机型 | 七彩虹将星 X15 / X16 / X17 等随机预装 Control Center 3.0 的机型 |
| 系统 | Windows 10 / 11 64 位 |
| 依赖 | Control Center 3.0（提供 `InsydeDCHU.dll` 与 `AcpiBridge.sys` 驱动） |

其他品牌 / 型号未经验证，出问题请点「还原原厂默认」或重启即可完全恢复。

## 使用

1. 从 [Releases](../../releases) 下载 zip，解压到任意文件夹
2. 双击 `FanSilencer.exe`，UAC / SmartScreen 提示选「是 / 仍要运行」（需要管理员权限访问风扇通道）
3. 启动后自动快照并接管风扇调度；退出程序自动复原
4. 曲线编辑器：拖动黄点调整、双击空白加点、右键删点，改完点「应用曲线」
5. 「开机自启」通过计划任务在登录时自动启动（管理员权限）

> 请勿与 Control Center 的风扇调节同时使用——本工具会自动顶回外部改动（3 秒内），但别互相打架。

## 工作原理（简述）

- 调用系统里已安装的签名 `InsydeDCHU.dll`（AcpiBridge.sys → ACPI `_DSM` → EC），P/Invoke 签名与官方 FanSpeedSetting 反编译源码 1:1 对齐
- `package 14` 写 EC 运行时风扇表（T2/D2/T3/D3 + 三段百分比斜率），`121/1` 切自定义模式 6；AppSettings 页 4 回写应用侧数据
- 多点曲线按温度取「所在区间的两个节点」写成 EC 两点窗口，温度跨区间时换挡重写（偏差 ≥3% 才写，避免频繁刷表）
- 监护循环 1Hz：模式字节检查（3s 内顶回外部变更）+ 30s 无条件重申 + 过热兜底（3 帧去抖触发 / 4 帧确认恢复）

详见 `src/FanSilencer/CurveManager.cs` 与 `FanController.cs` 内注释。

## 构建

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)：

```bash
# 多文件本地部署
dotnet publish src/FanSilencer -c Release -r win-x64 --self-contained true

# 单文件发行包（对方机器无需安装 .NET）
dotnet publish src/FanSilencer -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true
```

构建产物需要放在含 `config.json` / `curve.json` 的目录中运行；`tools/` 下有本机维护用的辅助脚本（升级重启、打包等），路径按需修改。

## 免责声明

本项目为社区工具，与七彩虹官方无关。修改风扇策略存在一定风险，请自行评估；若出现异常，使用「还原原厂默认」或重启电脑即可恢复出厂风扇策略。作者不对任何硬件损失负责。

## License

[MIT](LICENSE)
