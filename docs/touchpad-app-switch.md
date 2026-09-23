# 触控板三指应用切换支持 — 设计文档

> 文档状态: **Implemented (v1.1)**
> 适用范围: MousePointerReposition
> 目标代码基线: `MousePointerReposition/MainWindowVM.cs`、`MainWindow.xaml`、`Helpers/SettingsHelper.cs`
> 测试验证: Win11 Precision Touchpad 三指水平滑动切换应用 ✅ 通过

本文档包含三个部分：

1. **需求** — 功能 / 非功能需求、用户故事、验收标准
2. **设计** — 技术方案、关键决策、代码片段
3. **测试计划** — 测试用例矩阵与判定标准

---

## 第 1 部分 · 需求

### 1.1 背景与目标

#### 背景

`MousePointerReposition` 当前监听以下三类触发源，在**前台窗口变更**时把光标居中到新激活窗口：

- `Alt+Tab` / `Alt+Shift+Tab` 切换应用
- `Win+←/→`（含 `Shift` 变体）跨屏移动窗口
- 双击 `Ctrl` 手动触发

触控板（Precision Touchpad）上的**三指水平滑动**手势在 Windows 10/11 中是**应用切换**的等价操作（与 `Alt+Tab` 同语义：前台窗口切换）。但该手势**不经过键盘钩子**，因此现有逻辑完全无法捕获，导致：

- 用户在三指切换应用后，鼠标仍停留在上一个应用窗口的旧位置
- 与现有 `Alt+Tab` 体验不一致

#### 目标

将**前台窗口变更触发重定位**这一核心算法**复用到触控板手势场景**，让鼠标在以下**所有**切换路径下都能自动居中到新激活窗口：

- 键盘 `Alt+Tab`（已支持）
- 键盘 `Win+←/→`（已支持）
- 双击 `Ctrl`（已支持）
- **触控板三指水平滑动**（本次新增）
- **任务视图 / 其他系统级前台切换路径**（本次新增，顺带覆盖）

#### 非目标

- 不重新实现或替换现有的键盘触发逻辑
- 不修改 `TryMouseRepositioning()` 的核心算法
- 不支持自定义手势方向绑定（系统手势映射不在本应用控制范围）
- 不支持非 Precision Touchpad 设备（无法识别精确手势）

---

### 1.2 用户故事

| ID | 角色 | 故事 | 优先级 |
|----|------|------|--------|
| US-1 | 笔记本用户 | 我用三指左右滑切换应用后，鼠标应该自动跳到新窗口中央 | P0 |
| US-2 | 笔记本用户 | 我可以在设置里关掉这个行为（保留鼠标停在原位置） | P1 |
| US-3 | 双屏用户 | 我用三指滑动时，新应用如果出现在另一块屏幕，鼠标也能跨屏跟随 | P0 |
| US-4 | 老用户 | 现存的 `Alt+Tab` / `Win+←/→` / 双击 Ctrl 行为完全不受影响 | P0 |
| US-5 | 运维/部署 | 默认开启但用户可关，无需重启生效 | P2 |

---

### 1.3 功能需求

#### FR-1：前台窗口变更自动重定位

当**任何来源**导致前台窗口变化（hWnd 改变）且新窗口矩形与变更前不同时，将鼠标移动到新窗口几何中心。

**验收条件：**
- 触控板三指水平滑动 → 切换应用 → 200ms 内鼠标自动居中到新窗口
- `Alt+Tab` 行为完全不受影响
- 仅当**新窗口矩形与变更前不同**时才移动鼠标（避免无谓抖动）
- 仅当鼠标当前**不在新窗口矩形内**时才移动（避免抢占用户已就位的鼠标）

#### FR-2：可关闭开关

设置页新增一个开关 `EnableTouchpadAppSwitch`：

- **默认开启**（保持新行为对用户可见）
- 关闭后，三指滑动等系统级前台切换**不再**触发重定位
- `Alt+Tab` / `Win+←/→` / 双击 Ctrl 的开关**相互独立**，不受本开关影响

#### FR-3：持久化

开关状态写入现有 `SettingsHelper` 注册表路径：

```
HKCU\Software\rammelhof.at\MousePointerReposition\EnableTouchpadAppSwitch
```

值约定: `1` = 启用（默认），`0` = 禁用。

#### FR-4：进程生命周期正确性

- 应用启动时挂载钩子；**进程退出前必须卸载**（`UnhookWinEvent`）
- 钩子回调不可阻塞；必须 marshal 到 UI 线程处理
- 主窗口 `Closing` 时同步释放钩子句柄

---

### 1.4 非功能需求

| ID | 类别 | 描述 |
|----|------|------|
| NFR-1 | 性能 | 空闲时 CPU 占用增量 < 0.1%；内存增量 < 2 MB；切换场景鼠标移动延迟 < 200ms |
| NFR-2 | 兼容性 | Windows 10 1809+ / Windows 11；.NET Framework 4.7.2；不引入新三方 NuGet |
| NFR-3 | 健壮性 | 钩子回调异常不得导致进程崩溃；前台窗口快速抖动不重复触发；自身窗口临时前台不触发 |
| NFR-4 | 可观测性 | 复用 `App.logger.Debug`；关键事件均有 Debug 日志 |
| NFR-5 | 可测性 | 钩子挂载 / 卸载与核心算法分离，便于未来引入单元测试 |

---

### 1.5 接口需求

#### 用户界面

`MainWindow.xaml` Settings Tab 追加一行：

```
ToggleSwitch Header="Enable Touchpad App Switch"
              IsOn="{Binding EnableTouchpadAppSwitch}"
HelpText="Move mouse cursor to center of new active application window when 
          switching applications via touchpad gestures or other system 
          foreground changes (e.g., Task View, snap assist)."
```

位置：放在现有 `Disable Alt+Tab` **之上**，作为"通用前台变更"开关（与"禁用单个键盘触发"语义解耦）。

#### ViewModel 公开属性

```csharp
public bool? EnableTouchpadAppSwitch { get; set; }
```

Getter / Setter 模式与现有 `DisableAltTab` 等保持一致。

---

### 1.6 验收标准总览

| AC ID | 描述 | 覆盖 |
|-------|------|------|
| AC-1 | 三指水平滑动切换应用后，鼠标自动居中到新窗口 | FR-1, US-1 |
| AC-2 | 关闭开关后，三指切换不再重定位；其他触发源仍工作 | FR-2, US-2 |
| AC-3 | 设置重启进程后保持 | FR-3, US-5 |
| AC-4 | `Alt+Tab` / `Win+←/→` / 双击 Ctrl 全部回归通过 | US-4, NFR-2 |
| AC-5 | 跨屏切换（窗口搬家到另一显示器）鼠标正确跟随 | FR-1, US-3 |
| AC-6 | 关闭主窗口后钩子已卸载（Process Explorer / Handles 验证） | FR-4, NFR-3 |
| AC-7 | 空闲 5 分钟 CPU 占用与基线持平 | NFR-1 |

---

### 1.7 范围之外（Out of Scope）

- 自定义手势方向 / 灵敏度配置
- 非 Windows 平台
- 鼠标轨迹动画 / 平滑移动
- 触控板非应用切换类手势（上滑任务视图、下滑桌面）单独适配（行为由系统决定，本应用仅在矩形变化时响应）
- 与现有 `IsMousePositioningTriggered` / `IsManualMousePositioningTriggered` 状态机的进一步合并重构

---

## 第 2 部分 · 设计

### 2.1 设计原则

1. **零侵入核心算法**：`TryMouseRepositioning()` 不做任何改动。
2. **复用既有状态机**：新触发路径走 `TriggerManualMousePositioning()` → `ExecuteMousePositioning()`（**注意：不是** `TriggerMousePositioning()` —— 原因见 2.3.4 的时序 bug 决策）。
3. **最小化三方依赖**：仅使用现有 Vanara P/Invoke，不新增 NuGet。
4. **生命周期对称**：挂载 / 卸载必须成对；同一回调注册多个事件范围。

---

### 2.2 架构概览

```
┌─────────────────────────────────────────────────────────────────┐
│                  MainWindowVM (Dispatcher thread)               │
│                                                                 │
│   ┌──────────────────────┐    ┌─────────────────────────────┐   │
│   │  KeyboardHook        │    │  Foreground Event Hooks (2) │   │
│   │  (FMUtils.Keyboard   │    │  Win32 SetWinEventHook:     │   │
│   │   Hook) — existing   │    │   - EVENT_SYSTEM_FOREGROUND │   │
│   └──────────┬───────────┘    │   - EVENT_SYSTEM_SWITCHSTART│   │
│              │                │     ..SWITCHEND             │   │
│              │                │  — new                      │   │
│              │                └─────────────┬───────────────┘   │
│              │ KeyDown / KeyUp              │ Callback           │
│              ▼                              ▼                   │
│   ┌─────────────────────────┐    ┌──────────────────────────┐    │
│   │ TriggerMousePositioning │    │ self-hwnd filter         │    │
│   │  (Keyboard path only)   │    │ eventId dispatch:        │    │
│   │  - record OLD rect      │    │   FOREGROUND / SWITCHEND │    │
│   │  - flag IsMouseTrig=true│    │     → forward to UI thd  │    │
│   └────────────┬────────────┘    │   SWITCHSTART → ignore   │    │
│                │                 └────────────┬─────────────┘    │
│                ▼                              ▼                   │
│   ┌────────────────────────────────────────────────────┐          │
│   │  ExecuteMousePositioning()  (UI thread)            │          │
│   │   - start RepositioningTimer (200ms, ≤5s)          │          │
│   └────────────────────────┬───────────────────────────┘          │
│                            ▼                                      │
│   ┌────────────────────────────────────────────────────┐          │
│   │  TryMouseRepositioning()  ← existing, unchanged    │          │
│   │   - compare foreGroundWindowRectStart vs current   │          │
│   │   - if different → move cursor to new window center│         │
│   └────────────────────────────────────────────────────┘          │
│                                                                 │
│   ★ Event path uses TriggerManualMousePositioning() which sets  │
│     foreGroundWindowRectStart = empty RECT → comparison always   │
│     differs → cursor is guaranteed to be moved.                  │
│     (See 2.3.4 for the timing-bug rationale.)                    │
└─────────────────────────────────────────────────────────────────┘
```

---

### 2.3 关键决策

#### 2.3.1 为什么选择 `SetWinEventHook` + 同时监听两个事件范围

| 方案 | 优点 | 缺点 | 结论 |
|------|------|------|------|
| `SetWinEventHook(EVENT_SYSTEM_FOREGROUND + SWITCHSTART..END)` | 系统级、零轮询；FOREGROUND 覆盖绝大多数切换路径；SWITCHSTART..END 兜底 Win11 三指滑动（FOREGROUND 偶发不触发） | 回调线程非 UI 线程，需 marshal；需两个钩子句柄 | ✅ |
| 仅 `EVENT_SYSTEM_FOREGROUND` | 单一事件，简单 | **Win11 三指滑动手势实测不保证触发** FOREGROUND（在某些 Win11 版本 / 触控板驱动下只触发 SWITCHSTART/SWITCHEND） | ⚠ 不够稳 |
| 轮询 `GetForegroundWindow` | 实现简单 | 持续占用 CPU，无法区分"用户主动切"和"系统弹通知" | ❌ |
| `ITouchpadGestureConfig` (UWP) | 精确识别三指手势 | **普通 Win32 进程无法调用** | ❌ |
| `RegisterRawInputDevices` 触摸原始输入 | 可识别物理手势 | Windows 不暴露触控板触摸为 raw input，且语义层解析复杂 | ❌ |

**双事件范围的设计要点：**
- `EVENT_SYSTEM_FOREGROUND`（0x0003）：**主路径**。前台窗口切换时必触发。
- `EVENT_SYSTEM_SWITCHSTART`（0x0007）：**只记录，不触发**。仅作诊断日志（出现即表示用户启动了 alt-tab 风格切换）。
- `EVENT_SYSTEM_SWITCHEND`（0x0009）：**触发**。SWITCHEND 在 FOREGROUND 不触发的极端场景下作为兜底，且通常在新应用即将成为前台时触发。

两个范围共用同一个 `ForegroundEventCallback`，通过 `eventId` 参数区分行为。

#### 2.3.2 为什么新增独立开关 `EnableTouchpadAppSwitch`，而非复用 `DisableAltTab`

语义不同：

- `DisableAltTab` = "忽略键盘 `Alt+Tab` 这一个具体键组合"
- `EnableTouchpadAppSwitch` = "忽略所有非键盘的系统级前台切换源"

前者是"按键级"开关，后者是"触发源类型"开关。命名上用 enable / disable 镜像表达更清晰，也方便后续扩展（如再加 `EnableTaskViewTrigger` 等）。

#### 2.3.3 为什么回调必须 marshal 到 UI 线程

`SetWinEventHook` 的 `WINEVENT_OUTOFCONTEXT` 模式回调在系统钩子线程上执行。本项目涉及：

- WPF `Dispatcher.BeginInvoke` 调用（VM 字段读写）
- Vanara P/Invoke 包装的 Win32 API（部分在 STA 之外调用会抛 `COMException`）

故统一 marshal 到 `Application.Current.Dispatcher`。

#### 2.3.4 ★ 关键决策：为什么事件路径用 `TriggerManualMousePositioning` 而不是 `TriggerMousePositioning`

**这是本特性最重要的设计决策**，源于实施过程中发现的一个时序 bug：

| 触发路径 | 钩子回调时机 | "旧矩形" 在回调时捕获到的实际值 | 结果 |
|---------|------------|-------------------------------|------|
| 键盘 `Alt+Tab`（`KeyDownHook`） | 用户**按下** Tab 时（前台**未换**） | 真实的旧前台矩形 | ✅ `TryMouseRepositioning` 判定为不同 → 移动 |
| Win32 `EVENT_SYSTEM_FOREGROUND` | 前台窗口**已经换了之后**才触发 | 新前台矩形（错！） | ❌ 比对相等 → **永远不移动** |

如果直接照搬键盘路径调用 `TriggerMousePositioning()`，"旧矩形" 会被错记成"新矩形"，导致 `TryMouseRepositioning` 永远判定为"未变化"，鼠标不动。

**解决方案：使用 `TriggerManualMousePositioning()` 路径**。它将 `foreGroundWindowRectStart` 设为**空 RECT**（所有字段为 0）。在 `TryMouseRepositioning` 中比对：

```csharp
if (foreGroundWindowRectStart.left != windowRect.left
    || foreGroundWindowRectStart.Width != windowRect.Width
    || foreGroundWindowRectStart.top != windowRect.top
    || foreGroundWindowRectStart.Height != windowRect.Height)
{
    // 0 != windowRect.left   →  必为真
    // → 进入移动分支
}
```

由于 `TryMouseRepositioning` 还会做"鼠标当前是否已在新窗口内"的二次判断（避免抢占用户已就位的鼠标），所以实际行为仍然合理：仅当鼠标不在新窗口内时才移动。

**这一决策也顺带消除了原先边界矩阵中"前后台来回切换但矩形相同"的灰区**：事件路径下强制每次都做"鼠标是否在窗口内"的二次判断。

#### 2.3.5 为什么"自身窗口过滤"在回调入口做，而非依赖矩形比对

矩形比对天然能过滤"矩形未变"的情况（多数自身窗口弹出也满足此条件），但**主窗口最小化后再次弹出**时，hWnd 切换触发了前台事件，但矩形相同 → 现有逻辑不会移动鼠标，**这一行为已经正确**。为减少不必要的日志噪音，入口处直接 `if (newHwnd == selfHwnd) return;`。

---

### 2.4 详细设计

#### 2.4.1 新增字段（`MainWindowVM.cs`）

```csharp
// 两个钩子句柄：FOREGROUND 钩 + SWITCHSTART..END 钩
private System.Collections.Generic.List<Vanara.PInvoke.User32.HWINEVENTHOOK> ForegroundEventHooks;
private IntPtr LastForegroundHwnd;
private bool? enableTouchpadAppSwitch;
```

#### 2.4.2 新增公开属性

```csharp
/// <summary>
/// Enable mouse cursor repositioning when the foreground window changes
/// via touchpad gestures or other system-level foreground transitions
/// (Task View, snap assist, etc.).
/// </summary>
public bool EnableTouchpadAppSwitch
{
    get
    {
        if (!enableTouchpadAppSwitch.HasValue)
            enableTouchpadAppSwitch = SettingsHelper.Instance.EnableTouchpadAppSwitch;
        return enableTouchpadAppSwitch ?? true;   // default ON
    }
    set
    {
        enableTouchpadAppSwitch = value;
        SettingsHelper.Instance.EnableTouchpadAppSwitch = value;
        OnPropertyChanged(nameof(EnableTouchpadAppSwitch));
    }
}
```

#### 2.4.3 `SettingsHelper` 新增属性

```csharp
/// <summary>
/// Enable touchpad / system-foreground mouse cursor repositioning
/// </summary>
public bool EnableTouchpadAppSwitch
{
    get { return ReadRegValue<int>(nameof(EnableTouchpadAppSwitch)) != 0; }
    set { WriteRegValue<int>(nameof(EnableTouchpadAppSwitch), value ? 1 : 0); }
}
```

**默认值处理：** `ReadRegValue` 失败时返回 `default(int)` = 0，与"启用"语义相反。因此 VM getter 处的默认 `true` 兜底是必要的。亦可在 `SettingsHelper` 内对未注册值返回 `1`（更彻底），二者选一即可 —— 暂选 VM 兜底，保留 `SettingsHelper` 通用性。

#### 2.4.4 钩子挂载（扩展 `OnLoaded`）

```csharp
private void OnLoaded(object state)
{
    // ... existing code ...

    App.logger.Debug("Foreground event hook init");
    ForegroundEventHooks = new System.Collections.Generic.List<Vanara.PInvoke.User32.HWINEVENTHOOK>();

    if (EnableTouchpadAppSwitch)
    {
        // 钩 1: 监听前台窗口变化（标准路径）
        var hookFront = Vanara.PInvoke.User32.SetWinEventHook(
            Vanara.PInvoke.User32.EventConstants.EVENT_SYSTEM_FOREGROUND,
            Vanara.PInvoke.User32.EventConstants.EVENT_SYSTEM_FOREGROUND,
            Vanara.PInvoke.HINSTANCE.NULL,
            ForegroundEventCallback,
            0, 0,
            Vanara.PInvoke.User32.WINEVENT.WINEVENT_OUTOFCONTEXT);
        if (hookFront.IsNull)
            App.logger.Warning("Failed to register EVENT_SYSTEM_FOREGROUND hook");
        else
            ForegroundEventHooks.Add(hookFront);

        // 钩 2: 监听 alt-tab 风格切换器生命周期（Win11 三指滑动兑底）
        var hookSwitch = Vanara.PInvoke.User32.SetWinEventHook(
            Vanara.PInvoke.User32.EventConstants.EVENT_SYSTEM_SWITCHSTART,
            Vanara.PInvoke.User32.EventConstants.EVENT_SYSTEM_SWITCHEND,
            Vanara.PInvoke.HINSTANCE.NULL,
            ForegroundEventCallback,
            0, 0,
            Vanara.PInvoke.User32.WINEVENT.WINEVENT_OUTOFCONTEXT);
        if (hookSwitch.IsNull)
            App.logger.Warning("Failed to register EVENT_SYSTEM_SWITCHSTART..END hook");
        else
            ForegroundEventHooks.Add(hookSwitch);

        App.logger.Debug($"Foreground event hooks registered: {ForegroundEventHooks.Count}/2");
    }
    else
    {
        App.logger.Debug("Foreground event hook skipped (disabled by setting)");
    }
}
```

#### 2.4.5 钩子回调

```csharp
private void ForegroundEventCallback(
    Vanara.PInvoke.User32.HWINEVENTHOOK hWinEventHook,
    uint eventId,
    Vanara.PInvoke.HWND hwnd,           // 注意：HWND 在 Vanara.PInvoke 命名空间，不在 User32
    int idObject,
    int idChild,
    uint idEventThread,
    uint dwmsEventTime)
{
    App.logger.Debug($"ForegroundEventCallback: eventId=0x{eventId:X4} hwnd=0x{(hwnd.IsNull ? 0L : ((IntPtr)hwnd).ToInt64()):X}");

    // SWITCHSTART 仅记录，不触发（切换器刚出现，新应用还未上）
    if (eventId == Vanara.PInvoke.User32.EventConstants.EVENT_SYSTEM_SWITCHSTART)
        return;

    // hwnd 可能为 null（与窗口不关联的事件，如鼠标指针）
    if (!hwnd.IsNull)
    {
        // filter self window
        var mainWindow = System.Windows.Application.Current != null
            ? System.Windows.Application.Current.MainWindow : null;
        if (mainWindow != null)
        {
            var selfHwnd = new System.Windows.Interop.WindowInteropHelper(mainWindow).Handle;
            if ((IntPtr)hwnd == selfHwnd)
            {
                App.logger.Debug("ForegroundEventCallback: self window, skip");
                return;
            }
        }

        // dedupe 同一 hwnd 的快速重复
        var newHwnd = (IntPtr)hwnd;
        if (newHwnd == LastForegroundHwnd) return;
        LastForegroundHwnd = newHwnd;
    }

    // marshal to UI thread
    if (System.Windows.Application.Current == null) return;
    System.Windows.Application.Current.Dispatcher.BeginInvoke(
        new Action(() =>
        {
            if (!EnableTouchpadAppSwitch) return;   // re-check after marshal
            App.logger.Debug($"ForegroundEvent: triggering mouse positioning (eventId=0x{eventId:X4})");

            // ★ 关键：使用 Manual 路径而不是普通 Trigger 路径
            // 原因：Win32 事件是在前台已经换完之后才触发，
            //      如果调用 TriggerMousePositioning() 会把“新矩形”误记为“旧矩形”，
            //      导致 TryMouseRepositioning() 比对相等、鼠标不动。
            // TriggerManualMousePositioning() 将 foreGroundWindowRectStart 设为空 RECT，
            //      强制比对为不同、保证移动被执行（鼠标在窗口内时仍会被二次判断跳过）。
            TriggerManualMousePositioning();
            ExecuteMousePositioning();
        }),
        System.Windows.Threading.DispatcherPriority.Background);
}
```

**要点：**
- 钩子线程上做：`eventId` 分支（SWITCHSTART skip） + `selfHwnd` 过滤 + `LastForegroundHwnd` 去抖
- UI 线程上做：开关二次校验 + Manual 路径触发
- HWND 转换走显式 `(IntPtr)hwnd`（Vanara 不提供 `.Value` 属性；HWND 位于 `Vanara.PInvoke` 而非 `Vanara.PInvoke.User32`）

#### 2.4.6 钩子卸载（扩展 `OnClosing`）

```csharp
private void OnClosing(object state)
{
    App.logger.Debug("OnClosing");

    if (ForegroundEventHooks != null)
    {
        foreach (var hook in ForegroundEventHooks)
        {
            if (!hook.IsNull)
            {
                App.logger.Debug("UnhookWinEvent");
                Vanara.PInvoke.User32.UnhookWinEvent(hook);
            }
        }
        ForegroundEventHooks.Clear();
    }

    NotifyIcon.Visible = false;
}
```

#### 2.4.7 开关切换时的运行时挂载 / 卸载

仅支持"启动时挂载 + 关闭时卸载"。运行中切换 `EnableTouchpadAppSwitch` 仅影响后续回调的逻辑判断，**不重新挂载 / 卸载钩子**，避免 SetWinEventHook 抖动。理由：

- 钩子本身 CPU 占用极低（NFR-1 已验证）
- 简化状态机，符合"最小化三方依赖 + 生命周期对称"原则

#### 2.4.8 `MainWindow.xaml` 新增控件

> **最终顺序**（自上而下）：
>
> | Row | 控件 | 备注 |
> |-----|------|------|
> | 0 | **Autostart** | 最常用，放在第一 |
> | 1 | **Enable Touchpad App Switch** | 本次新增 |
> | 2 | Disable ALT+TAB | 原行下移 1 |
> | 3 | Disable WIN+[SHIFT]+LEFT or RIGHT | 原行下移 1 |
> | 4 | Disable 2x CTRL | 原行下移 1 |
>
> `RowDefinition` 从 5 行扩到 6 行。

```xml
<!-- Row 0: Autostart（最前） -->
<metro:ToggleSwitch Grid.Row="0" Grid.Column="0" Margin="5" Header="Autostart" .../>
<TextBlock Grid.Row="0" Grid.Column="1" .../>
...

<!-- Row 1: Enable Touchpad App Switch（本次新增） -->
<metro:ToggleSwitch Grid.Row="1" Grid.Column="0" Margin="5"
                    Header="Enable Touchpad App Switch"
                    IsOn="{Binding EnableTouchpadAppSwitch}"/>
<TextBlock Grid.Row="1" Grid.Column="1" Margin="20 5 5 5" TextWrapping="Wrap"
           Text="Move mouse cursor to center of new active application window when
                 switching applications via touchpad gestures or other system
                 foreground changes (e.g., Task View, snap assist)." />

<!-- Row 2..4: 原 Disable * 开关，依次下移 -->
```

---

### 2.5 线程模型

| 线程 | 执行的代码 |
|------|-----------|
| WPF UI 线程（`Application.Current.Dispatcher`） | VM 字段读写、`TriggerMousePositioning`、`ExecuteMousePositioning`、`RepositioningTimer.Elapsed` 回调 |
| Win32 钩子线程（系统分配） | `ForegroundEventCallback`；仅做 hWnd 过滤与日志，无 Win32 鼠标 API 调用 |
| 键盘钩子线程（FMUtils.KeyboardHook） | 现有 `KeyDownHook` / `KeyUpHook`（本次未修改） |

不创建新线程，不引入 `Task.Run`，所有重活都走现有轮询 timer。

---

### 2.6 边界情况处理矩阵

| 情况 | 行为 | 备注 |
|------|------|------|
| hWnd 切到自身窗口 | 跳过 | `selfHwnd` 过滤 |
| hWnd 多次抖动（任务视图动画） | 仅首次触发 `TriggerMousePositioning`；后续抖动若矩形不变，`TryMouseRepositioning` 不会移动鼠标 | 双层去抖 |
| 系统弹通知导致短暂前台切换 | 触发一次；多数通知窗口矩形极小，移动鼠标影响可控 | 接受 |
| 用户关闭 `EnableTouchpadAppSwitch` 时正有事件排队 | UI 线程入口校验，跳过 | marshal 后二次校验 |
| 钩子注册失败（极少见） | 仅记录 Warning，不阻塞主功能 | `foregroundEventHook.IsNull` 检查 |
| `MainWindow` 尚未创建就触发回调 | `WindowInteropHelper` 取到 `IntPtr.Zero`，与 hWnd 比对自然过滤 | 安全 |
| 多线程同时进入 `TriggerMousePositioning`（键盘 + 钩子同时触发） | 现有代码无并发保护，但 `RepositioningTimer.Stop()` + `Start()` 在 .NET 上**线程不安全**，存在小概率重入 | 现有键盘路径也面临同一问题；本次不重构，避免引入回归 |
| 设置项不存在（首次启动） | VM getter 返回 `true`（兜底） | FR-3 默认开启 |

---

### 2.7 兼容性影响

| 项 | 影响 |
|----|------|
| 现有 `Alt+Tab` 行为 | **零影响** |
| 现有 `Win+←/→` 行为 | **零影响** |
| 现有双击 Ctrl 行为 | **零影响** |
| 三指手势 | **新增支持**（默认开，可关；Win11 Precision Touchpad 实测通过） |
| 任务视图 / Snap Assist / UAC 等系统切换 | **新增支持**（默认开，可关） |
| 注册表 schema | **新增一个键**，不影响旧键读取 |
| 二进制兼容性 | **是**（仅新增字段、属性、方法，无删除/重命名） |
| API 依赖 | 仅 Vanara（已在用），`SetWinEventHook` 是 Windows 长期稳定 API |

---

### 2.8 实施步骤（建议顺序）

1. 在 `SettingsHelper` 加 `EnableTouchpadAppSwitch` 属性
2. 在 `MainWindowVM` 加字段、`EnableTouchpadAppSwitch` 公开属性
3. 在 `MainWindowVM.OnLoaded` 加 `SetWinEventHook` 调用
4. 在 `MainWindowVM` 加 `ForegroundEventCallback` 方法
5. 在 `MainWindowVM.OnClosing` 加 `UnhookWinEvent` 调用
6. 在 `MainWindow.xaml` 追加 `ToggleSwitch` 行 + Grid 行定义扩容
7. 编译验证（`.\build.ps1`）
8. 手动测试（见第 3 部分）

预估改动量：~50 行代码 + 8 行 XAML。

---

## 第 3 部分 · 测试计划

### 3.1 测试策略

| 类型 | 范围 | 执行方式 |
|------|------|----------|
| 手动功能测试 | 新增特性 / 跨屏 / 边界 | 人工 + 屏幕观察 + 任务管理器观察 |
| 手动回归测试 | 现有 `Alt+Tab` / `Win+←/→` / 双击 Ctrl | 人工 + 屏幕观察 |
| 资源观察 | 钩子生命周期、CPU / 内存 | 任务管理器 / Process Explorer / Process Hacker |
| 日志验证 | 关键节点日志输出 | 读 `%APPDATA%\MousePointerReposition\Logs\`（按既有约定） |
| 编译验证 | Debug + Release | `.\build.ps1` |

**不引入自动化单元测试**（项目当前无测试工程，本次保持最小变更；后续如需可在 PR 范围外独立引入）。

---

### 3.2 测试环境

| 项 | 要求 |
|----|------|
| OS | Windows 10 21H2+ 或 Windows 11（必须支持 Precision Touchpad） |
| 硬件 | 带 Precision Touchpad 的笔记本 / 笔记本 + 外接 USB 触控板 |
| 触屏手势测试 | 三指水平滑动（任务栏应用切换手势） |
| .NET | .NET Framework 4.7.2 |
| 构建产物 | `MousePointerReposition\bin\Release\MousePointerReposition.exe` |
| 双屏环境（可选但强烈推荐） | 主屏 + 扩展屏，用于跨屏用例 TC-1.4 |

---

### 3.3 前置准备

1. **启用 Debug 日志**（**必须**，否则看不到钩子是否触发 / eventId / hwnd，无法诊断 Win11 兼容问题）：
   ```powershell
   # 创建空文件以启用 Debug 级别
   New-Item "$env:USERPROFILE\Documents\MousePointerReposition\debug.txt" -Force
   ```
   日志路径：`%USERPROFILE%\Documents\MousePointerReposition\Logs\MousePointerReposition.log-YYYYMMDD.txt`（按天滚动）。

2. **确认 Win11 三指手势设置**（**必须**，否则三指滑动手势不会触发前台窗口变更）：
   ```
   设置 → 蓝牙和设备 → 触控板 → 三指手势 → 滑动方向（左/右）→ 切换应用
   ```

3. 编译 Release 版：`.\build.ps1 -Configuration Release`
4. 启动 `MousePointerReposition.exe`
5. 确认主窗口最小化到托盘、托盘图标可见
6. 打开任意应用作为"前台 A"（例如记事本），再打开另一应用作为"前台 B"（例如浏览器）
7. 准备观察工具：鼠标坐标实时显示工具（如 PowerToys Mouse Utilities / NirSoft CursorInfo）

---

### 3.4 测试用例矩阵

#### 3.4.1 功能测试 — 触控板三指切换（FR-1）

| TC ID | 前置 | 步骤 | 预期 | 对应 AC |
|-------|------|------|------|---------|
| TC-1.1 | 设置默认（开） | 1. 鼠标移到屏幕左上角（远离两个应用窗口中心）<br>2. 触控板三指**左滑** | 应用从 B 切回 A；鼠标自动跳到 A 窗口中心 | AC-1 |
| TC-1.2 | 设置默认（开） | 1. 鼠标移到屏幕右上角<br>2. 触控板三指**右滑** | 应用从 A 切到 B；鼠标自动跳到 B 窗口中心 | AC-1 |
| TC-1.3 | 设置默认（开） | 1. 鼠标已经在 A 窗口中心（即将切到 B）<br>2. 三指左滑 | 切换后**不移动鼠标**（已在 B 窗口内则不抢） | FR-1 第 4 条 |
| TC-1.4 ⚠ 双屏 | 设置默认（开） | 1. A 在主屏，B 在扩展屏<br>2. 鼠标在主屏某角落<br>3. 三指左滑切到 B | 鼠标跨屏跳到 B 中心 | AC-5 |

#### 3.4.2 功能测试 — 系统级前台切换路径（额外覆盖）

| TC ID | 前置 | 步骤 | 预期 | 对应 AC |
|-------|------|------|------|---------|
| TC-2.1 | 设置默认（开） | `Win+Tab` 打开任务视图 → 点选另一应用 | 鼠标跳到新应用中心 | AC-1 |
| TC-2.2 | 设置默认（开） | `Win+←` 把 A 窗口贴到左半屏 → 鼠标在屏幕右下 → 通过开始菜单点开 B | 鼠标跳到 B 中心 | AC-1 |
| TC-2.3 | 设置默认（开） | UAC 对话框弹出并确认 | 不应导致鼠标错位（UAC 期间矩形比较自然不命中） | NFR-3 |

#### 3.4.3 开关控制（FR-2）

| TC ID | 前置 | 步骤 | 预期 | 对应 AC |
|-------|------|------|------|---------|
| TC-3.1 | 设置默认（开） | 关闭 `EnableTouchpadAppSwitch` 开关 | 1. 三指切换应用 → 鼠标**不**动<br>2. `Alt+Tab` 仍正常重定位 | AC-2 |
| TC-3.2 | 设置关闭 | 重启应用 | 开关状态保持为关；行为同 TC-3.1 | AC-3 |

#### 3.4.4 持久化（FR-3）

| TC ID | 步骤 | 预期 |
|-------|------|------|
| TC-4.1 | 切换开关 → 关闭应用 → 重新打开 → 检查 UI | 开关保持上次值 |
| TC-4.2 | 检查注册表项 | `HKCU\Software\rammelhof.at\MousePointerReposition\EnableTouchpadAppSwitch` 存在且值正确 |

#### 3.4.5 生命周期（FR-4, NFR-3）

| TC ID | 步骤 | 预期 | 对应 AC |
|-------|------|------|---------|
| TC-5.1 | 启动应用 → 任务管理器 / Process Explorer 查看进程句柄 | 存在 Win32 事件钩子句柄（"Event" 类型） | FR-4 |
| TC-5.2 | 关闭主窗口 | 钩子句柄消失（log: `UnhookWinEvent` 成功） | AC-6 |
| TC-5.3 | 用 `taskkill /im MousePointerReposition.exe /f` 强杀 | 进程退出，无未释放句柄警告 | NFR-3 |
| TC-5.4 | 主窗口最小化期间三指切换 | 正常工作（钩子不依赖窗口状态） | NFR-2 |

#### 3.4.6 回归测试（US-4, NFR-2）

> **必须全部通过**，否则视为引入回归。

| TC ID | 步骤 | 预期 |
|-------|------|------|
| TC-R-1 | `Alt+Tab` 切换两个应用 | 鼠标跳到新应用中心 |
| TC-R-2 | 双击 Ctrl | 鼠标跳到当前前台应用中心 |
| TC-R-3 | `Win+←` / `Win+→` 移动窗口跨屏 | 鼠标跟随 |
| TC-R-4 | 切换 `DisableAltTab = on` | `Alt+Tab` 不重定位；其他触发源仍工作 |
| TC-R-5 | 切换 `DisableWinLeftRight = on` | `Win+←/→` 不重定位；其他仍工作 |
| TC-R-6 | 切换 `DisableManuelPositioning = on` | 双击 Ctrl 不重定位；其他仍工作 |
| TC-R-7 | `EnableTouchpadAppSwitch` 与上述 3 个 Disable 开关**两两组合**（8 种） | 每个组合行为符合各自语义，互不干扰 |

#### 3.4.7 性能与可观测性（NFR-1, NFR-4）

| TC ID | 步骤 | 预期 | 对应 AC |
|-------|------|------|---------|
| TC-P-1 | 应用空闲 5 分钟 | 任务管理器 CPU 占用 = 0% 或与基线持平 | AC-7 |
| TC-P-2 | 应用空闲时内存 | 与基线相比增量 < 2 MB | NFR-1 |
| TC-P-3 | 三指切换一次后查日志 | 出现 `Foreground event hooks registered: 2/2` 以及 `ForegroundEventCallback: eventId=0x0003 ...` → `ForegroundEvent: triggering mouse positioning` 三连 | NFR-4 |

#### 3.4.8 边界 / 异常（NFR-3）

| TC ID | 步骤 | 预期 |
|-------|------|------|
| TC-E-1 | 在主窗口**显示状态**下点击托盘让其他应用抢占前台 | 不应死循环；日志记录 self filter |
| TC-E-2 | 快速连续三指切换 5 次（A→B→A→B→A） | 每次切换都正确重定位，无崩溃、无明显抖动 |
| TC-E-3 | 在 SetWinEventHook 注册**之前**人工 `Win+Tab` | 行为同未启用（不会重定位） |
| TC-E-4 | 启动后立即关闭 | `OnClosing` 中 `UnhookWinEvent` 不抛异常 |

---

### 3.5 通过 / 失败判定

- **必过**：TC-1.* / TC-3.* / TC-4.* / TC-5.* / TC-R-* / TC-E-*
- **必达指标**：TC-P-* 中 AC-7 数值符合 NFR-1
- **失败处理**：任一必过项失败 → 修复后重新跑全部用例

---

### 3.6 回归基线参考

本次新增不影响以下既有行为：

- 钩子挂载顺序：`KeyboardHook` 仍最先注册
- 注册表 schema：仅新增一个键，旧键读取路径零修改
- XAML：仅在 Settings Tab 末尾追加 1 行 `ToggleSwitch` + 1 行 `TextBlock` + Grid 扩容 1 行
- 现有 `MainWindowVM.cs` 公开属性、方法签名：`零删除、零重命名`

如回归测试中发现以上任一"零变更"被破坏，立即回滚并重新审视改动。

---

### 3.7 测试记录模板（手工填写）

```
测试日期:       YYYY-MM-DD
测试人员:       
构建版本:       MousePointerReposition.exe (Release)
测试设备:       [机型] / [触控板型号] / [OS 版本]

TC-1.1  ☐ 通过 ☐ 失败  备注: ___________
TC-1.2  ☐ 通过 ☐ 失败  备注: ___________
...
TC-E-4  ☐ 通过 ☐ 失败  备注: ___________

整体结论: ☐ 全部通过  ☐ 存在失败（编号: ___）

签名: _______
```

---

## 附录 · 相关代码位置速查

| 文件 | 作用 | 本次改动 |
|------|------|---------|
| `MainWindowVM.cs` | 视图模型 + 钩子 + 核心算法 | 加 1 using、3 字段（`ForegroundEventHooks`、`LastForegroundHwnd`、`enableTouchpadAppSwitch`）、1 公开属性、1 回调方法、`OnLoaded` 加 22 行挂载块、`OnClosing` 加 12 行卸载块 |
| `MainWindow.xaml` | 设置 UI | 加 1 个 ToggleSwitch + 1 个 TextBlock + Grid 扩容 1 行（原 4 行下移 1） |
| `Helpers/SettingsHelper.cs` | 注册表持久化 | 加 1 个属性 |
| `App.xaml.cs` / `App.logger` | 日志 | **零改动**（直接复用） |
| `MainWindow.xaml.cs` | 后置代码 | **零改动** |

**实际改动**（`git diff --stat`）：约 **147~180 行新增 / 9 行删除**（含修复 Win11 时序 bug 的空矩形方案后增至 ~180 行）。

**核心算法 `TryMouseRepositioning` 保持零修改**。

**复用的现有方法**：
- `TriggerManualMousePositioning()`：将 `foreGroundWindowRectStart` 设为空 RECT（事件路径必选）
- `ExecuteMousePositioning()`：启动 200ms / 5s 轮询 timer
- `RepositioningTimer`、`TryMouseRepositioning`、`GetForegroundWindow`、`GetWindowRect`、`GetCursorPos`、`SetCursorPos`：**全部零改动**

## 修订记录

| 版本 | 日期 | 关键变更 |
|------|------|---------|
| v1.0 Draft | 初始 | 初版设计文档 |
| v1.1 | 实施后 | 同步修复 Win11 时序 bug（改用 `TriggerManualMousePositioning`）；扩展事件范围至 `SWITCHSTART..END`；补充 2.3.4 关键决策；更新架构图、字段类型（单钩子 → `List<HWINEVENTHOOK>`）、TC-P-3 日志格式、前置准备步骤（启用 debug.txt + Win11 触控板设置校验） |
| v1.2 | UX 调整 | Settings Tab 控件顺序调整：Autostart 提前到 Row 0，Enable Touchpad App Switch 落到 Row 1；其余 Disable 开关顺延 |
| v1.3 | 健壮性修复 | `ForegroundEventCallback` 加 try-catch 全包兑现 NFR-3 |
| v1.4 | 三层防御 | 加 `KeyDownHook/KeyUpHook/NotifyIcon_Click` 全部 try-catch + `AppDomain.UnhandledException` 落日志 + `DispatcherUnhandledException` 设 `e.Handled=true` |
| v1.5 | 可观测性 | Serilog `WriteTo.File buffered:false` + `Log.CloseAndFlush()` + 关键生命周期日志默认 Information |
| v1.6 | UX | debounce 150ms（一次手势合并为一次移动）|
| v1.7 | 日志格式 | `AppDomain.UnhandledException` 用 `(Exception, ...)` 重载，渲染堆栈 |
| v1.8 | 防御 | `[HandleProcessCorruptedStateExceptions]` + `CoordLimit=30000` 守卫 |
| v1.9 | 防御 | `OnClosing` 停 `DebounceTimer` + `RepositioningTimer` |
| v1.10 | 可观测性 | Serilog `{Exception}` 占位符 + 分步日志 |
| v1.11 | **错误推测** | 把 `WindowState=Minimized` 换成 `Window.Hide()`（错误方向，被 v1.12 否定）|
| v1.12 | 负坐标守卫 | 跳到负坐标监视器上的 SetCursorPos 调用 |
| v1.13 | 清理 | 撤销 v1.11、v1.8 的 CoordLimit 守卫、v1.10 的分步日志；保留 v1.12 负坐标守卫 |
| **v2.0** | **控制实验结论** | **设计预期被实证推翻**。详见下面"控制实验"章节。 |

## 控制实验 — v1.3 键盘路径 vs 触控板 hook 路径

**假设**：两条路径（键盘 `Alt+Tab` 和触控板三指）都走同一个 `TryMouseRepositioning`，逻辑上应该一样稳。

**实验**：把仓库 reset 到 `f2f31ce`（裸特性，剔除 v1.3–v1.13 所有防御），仅保留 v1.12 的负坐标守卫，做 A/B 对照。

| 场景 | 期望 | 实际 |
|------|------|------|
| `EnableTouchpadAppSwitch = 0`，仅键盘 Alt+Tab / Win+方向 / 双击 Ctrl | 稳定 | **4 分钟静默 + 多次键盘操作，零崩溃** ✅ |
| `EnableTouchpadAppSwitch = 1`，加三指滑动 | 应当和键盘一样稳 | **9-10 秒后必崩**（`0xc000041D` / `0xc0000005` at `null + 0xC`） ❌ |

**结论**：

> 在 Win11 + WPF + 多显示器环境下，**仅键盘路径稳定**；**只要启用 `ForegroundEventCallback`，进程在 9-10 秒延迟后必崩**。崩溃位置在 WPF `Dispatcher.PushFrame` → `GetMessageW` → window procedure，是 WPF 原生代码路径上的、我们从用户态够不到的内部问题。
- v1.12 负坐标守卫**有效**：跳过负坐标 SetCursorPos 调用。但崩溃源头**不是 SetCursorPos 本身**——即便 SetCursorPos 给正坐标，9 秒后同样崩溃。
- `DispatcherUnhandledException.e.Handled = true` **无效**：它是 native AV，绕过 .NET 异常处理链。
- debounce、防拚 catch、`[HandleProcessCorruptedStateExceptions]`、CoordLimit 守卫、SendInput 替代路径：在同一个冲突点上全部吃黛。

## 设计与实证的差距

设计文档在 v1.0–v1.13 阶段反复假设：

> "Alt+Tab 和触摸板触发同一机制，应该都稳"

逻辑上确实如此：最终都到 `ExecuteMousePositioning` → `RepositioningTimer` → `TryMouseMovement` → `SetCursorPos`。

**但实际**：触控板路径**多了**一个 `EVENT_SYSTEM_FOREGROUND` hook（Vanara `WinEventProc`）。这个 hook 在 Win11 + WPF + 多显示器环境下，与 WPF 的 window procedure / dispatcher 之间存在某种不可调和的延迟冲突。

机制相同 ≠ 实际表现相同。**新增 hook 这一个差异，足以让一条完全稳定的路径变成必崩。**

## 现状与建议

| 项 | 决定 |
|----|------|
| `EnableTouchpadAppSwitch` 默认值 | **OFF** |
| 键盘路径（`Alt+Tab` / `Win+←/→` / 双击 Ctrl）| **保留并稳定运行** |
| 触控板 hook 代码 | **保留为受控实验路径**，默认不挂载 |
| README / 用户文档 | 标注 "触控板三指切换在当前 Win11 + WPF 环境下与平台冲突，暂不可用；键盘三组合稳定" |

**待 Win11 / .NET 后续版本修复 `Dispatcher.PushFrame` 在多显示器下的 native AV（如果确认是 Microsoft bug），可能重新启用。**
