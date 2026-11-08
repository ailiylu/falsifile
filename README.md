# DeskTidy（falsfile）

一个 Windows 桌面整理小工具。

双击桌面上的 **falsfile** 图标，把桌面上除了文件夹、快捷方式之外的一切杂物收进一个指定的文件夹；
**再双击一次**，文件原样回到桌面上原来的格子。

没有悬浮窗、没有动画、没有后台常驻进程——它就是一个普通快捷方式，占一个普通图标位。

---

## 它做什么

| 处理 | 对象 |
|---|---|
| **收纳** | 桌面上的普通文件（jpg / png / pdf / doc / xlsx / py / zip …） |
| **保留** | 所有文件夹、`.lnk` / `.url` / `.appref-ms` 快捷方式、隐藏与系统属性文件、`desktop.ini` |
| **永不触碰** | `C:\Users\Public\Desktop`（公共桌面）、程序自身 |

收纳目标目录：`<锚点文件夹>\收纳_yyyy-MM-dd_HHmm\`。
每收一次写一份 `journal.json`，还原时按记录精确回推，不扫目录、不覆盖同名文件。

## 怎么用

1. 把 `DeskTidy.exe` 放到任意目录（建议 `%LOCALAPPDATA%\Programs\DeskTidy\`）；
2. 生成桌面快捷方式：
   ```
   DeskTidy.exe --install
   DeskTidy.exe --install --anchor "C:\Users\你\Desktop\收纳盒"
   ```
   会在桌面创建 `falsfile.lnk`，图标是透明底的文件夹（多尺寸 ico，16–256）。
3. **双击 falsfile** → 收纳；**再双击** → 还原。

命令行也可以直接用：

```
DeskTidy.exe --toggle                      # 收纳 / 还原（快捷方式走的就是这条）
DeskTidy.exe --clean-dryrun                # 只打印"会保留什么、会收走什么"，不动文件
DeskTidy.exe --clean-now                   # 立即收纳
DeskTidy.exe --restore-now                 # 立即还原
```

## 怎么实现的

- **坐标**：`IFolderView2::GetItemPosition` 读每个桌面项的真实位置，`ClientToScreen` 换算成屏幕物理像素。
  不推算网格、不乘 DPI 系数、不抓屏。
- **还原位置**：`IFolderView::SelectAndPositionItems` + `SVSI_POSITIONITEM`，让 shell 把图标摆回记录的格子。
- **移动文件**：`File.Move`（同盘 rename，毫秒级，不触发桌面重绘）。
- **图标**：`IShellItemImageFactory.GetImage` 取系统真实图标；快捷方式图标在程序内实时渲染成多尺寸 `.ico`。

## 为什么没有动画（设计取舍）

最初做过"磨砂玻璃图标 + 文件被吸入"的动画版本，结论是**不值得**：

1. 桌面图标由 Explorer 绘制，动画层只能是一个叠加窗口，必须精确对齐图标矩形；
   图标尺寸、DPI、排版一变就要重算，很容易差几像素。
2. 叠加窗口在首帧显示、Z 序调整时会出现单帧闪烁，在深色桌面上尤其明显。
3. 而"一个普通快捷方式占一个格子"由 Explorer 自己排版，零窗口、零闪烁、零常驻内存。

所以最终版本砍掉了全部 UI 代码，只保留文件操作。

## 构建

需要 .NET 8 SDK：

```
dotnet publish -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

产物是单文件 exe（约 66 MB，自带运行时），目标机器不需要装 .NET。

依赖：`Vortice.Direct3D11 / DXGI / Direct2D1 / DirectComposition`（早期渲染版本遗留，当前代码路径未使用）、`System.Drawing.Common`（生成 ico）。

## 配置与日志

- 配置：`%LOCALAPPDATA%\DeskTidy\config.json`
  ```json
  { "AnchorPath": "C:\\Users\\你\\Desktop\\收纳盒" }
  ```
- 日志：`%LOCALAPPDATA%\DeskTidy\logs\desktidy.log`
- 撤销记录：`%LOCALAPPDATA%\DeskTidy\journal.json`

## 已知限制

- **多屏未实测**（开发机只有一块屏）。坐标链路按每块屏各自的 `DefView` 处理，但没有验证过。
- 还原位置依赖 `IFolderView2`，资源管理器重启后若拿不到视图会退化为"回到桌面但位置由系统决定"。
- 不特殊处理 OneDrive / 云占位文件，也不处理被独占锁定的文件（会跳过并记日志）。
- 只清理当前用户桌面，公共桌面完全不动。

## 安全

- 移动前先在 `journal.json` 里写好"从哪到哪"，崩溃后可手工恢复；
- 同名冲突自动加 `(2)` 后缀，绝不覆盖；
- 程序自身所在目录被硬编码排除，不会把自己 move 掉。

## License

未指定。
