# TODO / 长期研究项

> 用途：记录**已识别但不计划立即实施**的研究方向。每项都应包含：背景、要回答的问题、已知代价、触发条件（什么情况出现时才值得捡起来）。
> 决策原则：捡起来之前先重读"背景"与当时的实证数据，避免重复考古。

---

## TODO-1 · 应用提权运行方案（requireAdministrator）

**关联**：`docs/touchpad-app-switch.md` → "已知限制：提权应用（UIPI 边界）"

**背景**：提权进程（任务管理器、Everything 等以管理员运行的程序）处于 UIPI 墙的高侧，本应用对它们双向失明（WinEvent 不投递 + 提权前台期间 LL 键盘钩子被压制）。让本应用自身提权运行可以整体跨过这堵墙。

**要研究的问题**：

- [ ] manifest 改 `requireAdministrator` 后，普通双击启动的 UAC 弹窗体验是否可接受
- [ ] **开机自启如何迁移**：`HKCU\...\Run` 键对提权程序**静默失效**（Windows 跳过），需改用任务计划程序（Task Scheduler，"使用最高权限运行"+ 登录触发器）。评估 `RefreshAutostartState` / `Autostart` 属性改为读写任务计划的改造成本
- [ ] Store / MSIX 打包发布是否允许 requireAdministrator（预期：不允许或受限——若如此，此方案与 Store 渠道互斥，需要双发行渠道决策）
- [ ] 提权后 FMUtils 键盘钩子在所有场景的行为回归（理论上权限更高只会更通，但需实证）
- [ ] 提权后应用本身成为攻击面：评估是否引入新的安全审查义务

**预期收益**：所有触发路径对提权窗口全通（键盘双向 + 触控板双向）。

**触发条件**：日常使用中提权窗口切换占比明显升高，或有用户就此投诉。

---

## TODO-2 · uiAccess 辅助功能特权方案

**关联**：同上。

**背景**：`uiAccess="true"` 是为屏幕阅读器等辅助工具设计的中间档特权：**不完全提权**，但可以接收提权窗口的 UI Automation / WinEvent 事件、向提权窗口发送输入。若能落地，可同时解决 UIPI 的两个方向而无需整管理员权限。

**硬性前置条件（微软官方要求，缺一不可）**：

- [ ] 程序必须有 **Authenticode 数字签名**（自签证书在默认系统策略下无效，需受信 CA 或预先安装根证书）
- [ ] 程序必须安装在**受信路径**（`Program Files` / `Program Files (x86)` / `System32`）——绿色便携目录运行不生效

**要研究的问题**：

- [ ] 签名成本与渠道：自购代码签名证书 vs 测试用途自签（需手动信任根证书，仅适合自用）
- [ ] uiAccess 是否真的放行 `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` 对提权窗口的投递（官方文档措辞针对 UI Automation；WinEvent 是否同等待遇需要实测 spike）
- [ ] uiAccess 下 LL 键盘钩子在提权前台时是否恢复回调（同上，需实测）
- [ ] Store / MSIX 渠道下 uiAccess 的支持情况
- [ ] 与本应用现有的"非提权也能用"的分发方式如何共存（双构建？安装器？）

**预期收益**：理论上可解决 UIPI 双向盲区且无需 UAC 弹窗；但不确定性最高（两条"需实测"），建议先做最小 spike 验证事件投递，再谈其他。

**触发条件**：TODO-1 被否决（UAC/自启/Store 代价不可接受）但需求仍然强烈时。

---

## TODO-3 · 前台路径 UX 边缘场景（收集桶，待批处理）

**关联**：`docs/touchpad-app-switch.md` → v3.3-v3.6 四道闸章节

**背景**：前台路径现有四道闸（消重 / 点击抑制 500ms / 键盘近因 1000ms / 光标静止门 250ms）全是时间近因启发式。Win11 shell 瞬态 UI（toast、跳转列表、托盘飞出、开始菜单）抢前台的场景**共享同一根因**：窗口类多为 `Windows.UI.Core.CoreWindow`、宿主进程多为 explorer/ShellExperienceHost 系。逐个调阈值是打地鼠且持续侵蚀合法手势——**新场景只登记，不改代码**，攒批后一次设计。

**批处理设计方向（已识别，实施时复核）**：

- 按"宿主进程 + 窗口类"过滤：事件 hwnd → `GetWindowThreadProcessId` → 宿主 ∈ {explorer, ShellExperienceHost, StartMenuExperienceHost, SearchHost, TextInputHost} 且 class = `Windows.UI.Core.CoreWindow` → 跳过
- **坑**：`CoreWindow` 也是正常 UWP 应用主窗口的类名，**不能只按类名过滤**，必须结合宿主进程
- 证据采集已就位：前台事件入口日志自 2026-09-28 起携带 `class=`（无需复现考古）；如需 pid 可届时再加

**触发条件**：桶内 ≥3 个未解决场景，或出现 1 个高严重度（跨屏拽走级）场景。

### 场景登记

| # | 日期 | 场景 | 严重度 | 日志证据 | 指纹 |
|---|------|------|--------|---------|------|
| 1 | 2026-09-28 | 任务栏图标右键开跳转列表：菜单本体在点击后 575ms 才成为前台，越过 500ms 点击窗 → 跳到菜单中心 | 低（短跳，目标即注意力所在） | 17:47:58.54 右键拦截 ✓ → 17:47:59.11 `triggering (lastClick=586ms)` → New window (72,513 479×915) | `Windows.UI.Core.CoreWindow`（hwnd 0xA123E） |
| 2 | 2026-09-28 | 托盘通知闪烁 churn（已由 v3.6 静止门解决，作为同类根因参考入档） | 高（已解决） | toast/`Shell_TrayWnd`/托盘溢出窗口 20ms 级互切 | `CoreWindow` + `TopLevelWindowForOverflowXamlIsland` |

---

## 使用约定

- 完成或作废的条目：删除并在此文件顶部"已关闭"区留一行去向（commit / 弃用原因）
- 新增条目：从模板复制四段式（背景 / 要研究的问题 / 预期收益 / 触发条件）
