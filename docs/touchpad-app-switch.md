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
| v2.1 | 守卫门控修复 | 发现 v1.12 负坐标守卫放在共用方法 `TryMouseRepositioning` 里，开关 OFF 时仍侵入 Alt+Tab 路径（跨屏失灵）；改为仅在 `EnableTouchpadAppSwitch=true` 时生效（commit `e964f03`）。详见"共用路径污染事件"章节 |
| v2.2 | 物理隔离重构 | 触控板实验代码抽为 partial class `MainWindowVM.Touchpad.cs`；守卫从"开关门控"改为**路径（trigger）门控**——开关 ON 时键盘路径也不再被守卫拦截；新增"实验特性开发约束"与"平台风险特性开发流程"两个治理章节 |
| **v3.0** | **根因翻案** | 崩溃根因 = **未持有的 WinEventProc 委托**（GC 回收 → 下一事件跳入已释放 thunk → 0xC000041D），两行修复（commit `126685c`）。Stage 1-5 分阶段二分实证（见"v3.0 翻案"章节）。v2.0 的"平台不可行"归因**作废**：SetCursorPos / 负坐标 / WPF Dispatcher 全部清白。清理：拆诊断键盘钩子与从不触发的 SWITCH hook、双重触发消重、档位默认全功能（commit `0d98967`）|
| **v3.1** | **消重误杀修复** | 手势在 Win+Shift+方向键后永久失效：消重条件引用被手势路径污染的粘性标志 → 永久锁定。剔除泄漏标志 + 源头堵泄漏（commit `fa98a0d`）。烤机期重新起算；新增触控板回归清单；记录双实例风险与 master 预存 Left 键优先级 bug |
| v3.2 | 已知限制 + 治理 | UIPI 提权应用盲区实证（A/B/C 三步实验）并决策接受现状；新增 `docs/TODO.md` 存放提权运行 / uiAccess 两条后续研究路线；单实例 Mutex（`f16c351`）与 Left 键优先级修复（`8c979c1`）入库 |
| v3.3 | UX 优化 | 点击抑制：窗口关闭/最小化按钮、任务栏图标等**点击引起**的前台切换不再移动光标（`WH_MOUSE_LL` 时间戳 + 500ms 窗口，commit `bb5033a`）；备选方案 B（消失检测）留档未实施 |
| v3.4 | UX 优化 | 键盘近因抑制：Win 键/Win+E 等**键盘动作引起**的前台切换不再移动光标（被动 `WH_KEYBOARD_LL` 时间戳 + 1000ms 窗口，commit `2a71801`）；前台路径语义收拢为"仅响应手势"，新增触发判定全景表 |
| v3.5 | 可观测性 | 决策点证据日志：skip/trigger 行携带输入身份+距今时间+阈值（commit `e784a36`）；新增"人工验证 → 日志对照表"，人工验证结果机械可核对 |
| v3.6 | UX 优化 | 光标静止门：通知 churn 等**光标移动中**的前台变更不再拽走光标（`WM_MOUSEMOVE` 时间戳 + 250ms 窗口，commit `e3ec6f8`）；前台路径四道闸成型（消重/点击/键盘近因/静止门） |
| v3.7 | 观测 + 治理 | 前台事件入口日志携带 `class=`（纯观测零行为变化）；确立边缘场景**批处理策略**——shell 瞬态 UI 场景共享根因（`CoreWindow` + shell 宿主进程），新场景只登记入 `docs/TODO.md` TODO-3 收集桶，不逐个改代码 |
| v3.8 | UX 优化 | **TODO-3 批处理执行**：shell 瞬态 UI 过滤器（类名 + 宿主进程，入口层），一次解决 Task View/切换器/toast/跳转列表/托盘飞出/桌面整类；指纹集合见 `ShellTransientClasses`/`ShellHostProcesses` |
| v3.9 | 决策 | 慢弹窗跟随确认为可接受的涌现行为（点击门 500ms 边界，不改代码）；特性化方案（新窗口诞生跟随）存档 TODO-4 |

## 控制实验 — v1.3 键盘路径 vs 触控板 hook 路径

> ⚠️ **v3.0 更新**：本实验的 A/B 现象观察是正确的（hook 路径崩、键盘路径稳），但**归因错误**——崩溃不是"Win11/WPF 平台内部不可达 bug"，而是我们传给 `SetWinEventHook` 的回调委托**未被持有、被 GC 回收**所致。已修复并实证。详见"v3.0 翻案"章节。

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

## 复盘 — "想当然"的两层假设

事后复盘，当初把触控板功能合并进键盘路径时，隐含了两层假设，均被实证推翻：

### 假设 1：机制等价 → 行为等价（不成立）

> "触控板手势和 Alt+Tab 最终都是'前台窗口变了 → SetCursorPos 居中'，走同一条路径，行为必然相同。"

实测：

| 路径 | 到达 SetCursorPos 的路线 | 结果 |
|------|--------------------------|------|
| Alt+Tab | 键盘钩子线程 → 同步直接调 | 4+ 分钟零崩溃 |
| 触控板 | Win32 事件钩子 → `Dispatcher.BeginInvoke` 封送回 UI 线程 → 再调 | 手势后 9-10 秒必崩 |

**终点相同，路线不同。** 崩溃恰好发生在 WPF 自己的消息泵内部（`PushFrame → GetMessageW`），是**路线上**的差异、不是终点上的差异。"机制相同"只在我们代码层成立；在 WPF/Win32 运行时层不成立。

### 假设 2：共用路径 → 一处修复惠及所有（反向成立）

> "既然共用 `TryMouseRepositioning`，在这里加防御（负坐标守卫）能同时保护两条路。"

实测反过来：**实验路径的防御污染了稳定路径**。守卫无条件放在共用方法里，开关 OFF 时 Alt+Tab 到负坐标显示器（左侧/上方副屏）也被静默跳过——原有功能被破坏。

### 隐藏问题：开关 ON 时双重触发

开关 ON 时按 Alt+Tab，**两条路同时 fire**：

1. 键盘钩子捕获 Alt+Tab → 移鼠标
2. 前台事件钩子同样捕获（Alt+Tab 也触发 `EVENT_SYSTEM_FOREGROUND`）→ Dispatcher 封送 → 再移一次

同一次用户动作、两趟执行，这种叠加可能正是搞坏 WPF 内部状态的诱因之一（未完全证实，但与"合并"设计直接相关）。

## 共用路径污染事件（负坐标守卫侵入键盘路径）

**现象**：开关 OFF 后，Alt+Tab 切换到多显示器上的窗口，鼠标居中失灵。

**根因**：v1.12 负坐标守卫写在共用方法 `TryMouseRepositioning` 里，**无门控**。开关 OFF 时这段守卫照常执行，负坐标显示器上的窗口被静默跳过 `SetCursorPos`。

**验证方法**：日志搜 `skipping move, foreground window on monitor with negative coords` 可直接确认。

**修复（commit `e964f03`）**：守卫加开关门控——

```csharp
// 改前（无条件侵入）
if (windowRect.left < 0 || windowRect.top < 0)

// 改后（仅实验路径受守卫保护）
if (EnableTouchpadAppSwitch && (windowRect.left < 0 || windowRect.top < 0))
```

修复后行为矩阵：

| 开关 | 路径 | 负坐标窗口 | 正坐标窗口 |
|------|------|-----------|-----------|
| OFF（默认）| 键盘三组合 | ✅ 正常移动（= master 行为）| ✅ 正常 |
| OFF | 触控板 hook | 不注册，不存在 | 不注册，不存在 |
| ON（实验）| 键盘三组合 | ⚠️ 跳过（守卫生效）| ✅ 正常 |
| ON（实验）| 触控板 hook | ⚠️ 跳过（守卫生效）| ✅ 正常（但 9-10s 后仍崩，见控制实验）|

## 教训（沉淀为设计原则）

| 设计时的想法 | 实证结论 |
|-------------|---------|
| 共用代码路径，优雅复用 | 实验特性必须与稳定路径**物理隔离**，不是逻辑隔离 |
| 终点相同 = 行为相同 | 到达终点的**路线**决定稳定性 |
| 防御放在共用层最安全 | 防御必须**只罩住它要保护的那条路** |
| 开关 OFF = 回到旧行为 | 开关只能门控"挂载点"，门控不了**写进共用方法里的代码**——每一处对共用路径的修改都要单独问一句：开关 OFF 时它还在吗？ |
| 异常崩不动就先怀疑平台 | 先自查经典陷阱（**P/Invoke 回调委托持有**、句柄/句柄生命周期、GC 时序）——"平台 bug"应该是最后才配下的结论 |
| 反复叠加防御层碰运气 | 用**分阶段开关二分定位**，一次只放行一层，让证据指认元凶（Stage 1-5 一轮就锁定了根因）|
| 防御/消重逻辑天然安全 | **防御性代码同样需要隔离与回归验证**——消重误杀事件证明：一行无门控的防御就能让功能永久失效，且症状（"忽然不工作了"）与真凶（两个粘性标志的组合）之间毫无表面关联 |
| 布尔标志便宜好用 | **粘性标志是状态机异味**：置位点与清除点不成对的标志（`IsManualMousePositioningTriggered` 被手势路径置位、只能被 Ctrl 清除）迟早组成死锁。新加标志先问：谁置、谁清、什么路径会读到它 |

## v3.0 翻案 — 根因与分阶段二分定位

**v2.0 的归因是错的。** 控制实验的 A/B 现象观察没问题（hook 路径确实崩、键盘路径确实稳），但把根因归给"Win11/WPF 平台内部不可达的 native bug"是错的。**真凶是我们自己代码里的一个经典 P/Invoke 陷阱。**

### 根因：未持有的 WinEventProc 委托

```csharp
// 原代码（f2f31ce 起就存在）：
SetWinEventHook(..., ForegroundEventCallback, ...);
//                  >>> 方法组 → 临时委托实例，没有任何字段持有它 <<<
```

传给 native 的回调委托必须保持引用。GC 在某个时刻回收这个无根委托 → 下一次 WinEvent 到达时，native 代码跳进**已释放的 thunk** → AccessViolation → `0xC000041D`（STATUS_FATAL_USER_CALLBACK_EXCEPTION）。

**修复**：实例字段持有委托，注册前赋值。两行。

### 为什么这个根因能解释 v1.3–v2.0 的全部观测

| 观测 | 解释 |
|------|------|
| 崩溃延迟 9~14 秒且不固定 | 需要先等 GC 回收委托，再等下一个事件触发；GC 时机不确定 |
| 键盘路径从不崩 | FMUtils 库正确持有它的 hook 委托 |
| try-catch 全包无效 | 异常不在我们的托管代码里，在 CLR 的已释放委托桩里 |
| `DispatcherUnhandledException.e.Handled=true` 无效 | 同上，根本走不到 dispatcher 异常通道 |
| 负坐标守卫无效 | 跳过 SetCursorPos 照样崩（v2.0 已记录）——崩溃与 SetCursorPos 无关 |
| 只在开关 ON 时崩 | 只有这条路径注册了未持有委托的 hook |
| 崩溃栈在 `Dispatcher.PushFrame → GetMessageW` | WinEvent 回调由消息泵分发；死的正是消息泵里那个调用帧 |

### 分阶段二分定位法（Stage 1-5）

引入 `TouchpadExperimentStage` 内部开关（注册表，无 UI），把可疑链路切成五段逐段放行，让证据指认元凶：

| Stage | 放行范围 | 结果 |
|-------|---------|------|
| 1 ObserveOnly | 回调只记日志，不碰 Dispatcher | 修复前：**照样崩**（二分一步见底）；修复后：21 分钟零崩 |
| 2 MarshalOnly | + Dispatcher.BeginInvoke 往返 | 稳定（v2.0 怀疑的 PushFrame 层清白）|
| 3 NoCursorMove | + 完整管线，抑制 SetCursorPos | 稳定 |
| 4 Full+守卫 | 全功能，负坐标守卫生效 | 稳定；但守卫拦掉合法移动（左/上副屏）——**守卫的存在理由已随根因修复消失** |
| 5 Full 无守卫 | 完整行为 | **手势/键盘跨屏全部正常，负坐标 SetCursorPos 反复执行零崩** |

### 附带实证结论

1. **手势不注入合成击键**：诊断键盘钩子（`LLKHF_INJECTED`）全程观测——手势时段零键盘事件；物理 Alt+Tab 有真实扫描码且 `injected=False`。手势由 shell 直接驱动切换器，**完全绕过键盘栈**。（诊断钩子使命完成后已拆除，见 git 历史）
2. **Win11 从不发 `EVENT_SYSTEM_SWITCHSTART/END`**：全部日志零出现。v1.1 扩展的 hook 范围是死代码，已拆（注册 1/1）。
3. **双重触发劫持**：开关 ON 时 Alt+Tab 必然同时触发前台事件，事件回调把进行中序列的 trigger 改写为 ForegroundHook（Stage 3 时因此误压了键盘移动）。已加消重：键盘路径重定位进行中时，前台事件直接跳过，Alt+Tab 回归 100% 键盘路径。
4. **手势事件线程指纹**：手势产生的前台事件全部来自同一个 shell 线程；键盘 Alt+Tab 的事件来自各目标应用自己的线程。

## 消重误杀事件（v3.1）— 手势在 Win+Shift+方向键后永久失效

> 时间：2026-09-24 下午；修复 commit：`fa98a0d`。**这是消重逻辑（`0d98967` 引入）自身的回归**——防御性代码也会咬人。

### 现象

`Win+Shift+方向键` 移动窗口到另一屏后，三指手势**永久失效**（应用照常切换，鼠标不跟随）；Alt+Tab 完全正常；按一次 Ctrl 后手势自动复活。

### 证据（日志现场）

```
15:26:18  手势 → TriggerManualMousePositioning(ForegroundHook)  ← ① 污染发生
15:26:19  Win+Shift+← → ActiveTrigger=Keyboard；移动完成        ← ② 组合武装
15:26:21  手势 → ForegroundEvent: skipped (keyboard-path repositioning in flight)
15:26:24/27/30  手势 ×8 → 全部 skipped                         ← ③ 永久锁定
```

### 根因：两个粘性标志的组合死锁

消重条件为 `ActiveTrigger == Keyboard && (timer.Enabled || IsMousePositioningTriggered || IsManualMousePositioningTriggered)`：

1. `IsManualMousePositioningTriggered` 本是键盘双击 Ctrl 专用标志，但 `TriggerManualMousePositioning` **无条件置位**它，而触控板回调也走此方法 → 一次手势就污染该标志；清除它的只有 Ctrl 键路径 → 不按 Ctrl 永远粘着
2. `ActiveTrigger` 是"最后触发者"粘性标记，Win+方向键后停在 Keyboard

①②叠加 → 消重条件恒真 → 手势事件被全部跳过。Alt+Tab 不查消重条件故免疫。Ctrl 键清掉①故复活。

### 修复（`fa98a0d`）

- 消重条件剔除 `IsManualMousePositioningTriggered`：Alt+Tab/Win+方向键的 keydown→keyup 窗口由 `IsMousePositioningTriggered`（仅键盘路径置/清）罩住，keyup 后重试窗口由 `timer.Enabled` 罩住——被剔除的标志在这两个场景本就冗余
- `TriggerManualMousePositioning` 仅键盘路径置位该标志（堵泄漏源头；顺带消除"手势后单点 Ctrl 误触发重定位"的潜伏副作用）

### 验证

Win+Shift+方向键后连续手势正常跨屏跟随（零 skipped）；Alt+Tab 序列期重复事件**正确**消重（skipped 精确聚集在 Alt+Tab 的 ±2s 内）；Alt+Tab 后手势正常。

### 附带发现（均已修复）

- **应用无单实例防护**（曾致双实例并行：双份钩子、日志分叉、启动日志丢失）→ 已加会话级 Mutex，第二实例静默退出（commit `f16c351`）
- **master 预存运算符优先级 bug**（裸按左方向键误触发重定位序列）→ 已修，两个方向键均要求 Win（commit `8c979c1`）

## 已知限制：提权应用（UIPI 边界）

> 2026-09-24 实测确认；**master 原始程序行为一致，非本分支引入的回归**。

### 现象

以管理员权限运行的应用（任务管理器、Everything 等）不受本应用影响：

- 触控板手势切**到**提权应用 → 鼠标不跟随
- 提权应用在**前台时**按键（Alt+Tab / Win+方向键 / 2x Ctrl）→ 全部无效

### 实证数据

1. 任务管理器（两个 hwnd，当天重启过）与 Everything 的窗口句柄在全天 `ForegroundEventCallback` 日志中出现 **0 次**；普通应用句柄出现几十至上百次
2. 三步对照实验（2026-09-24 16:21-16:25）：

| 实验 | 操作 | 日志 | 结论 |
|------|------|------|------|
| A | TM 前台 → 2x Ctrl | 零记录 | 提权前台期间键盘钩子被压制 |
| B | 普通应用 → Alt+Tab → TM | keydown 正常，**移动成功执行** | 切向提权应用：键盘路径可用（快按快放） |
| C | TM 前台 → Alt+Tab 离开 | 零记录 | 从提权前台出发：键盘路径全盲 |

### 机制（UIPI，Vista 起的安全设计）

- **WinEvent 事件**：涉提权窗口的事件不投递给普通权限进程的 hook → 触控板路径对提权窗口全盲
- **WH_KEYBOARD_LL**：提权应用前台期间回调被系统压制 → 键盘路径"身在提权前台"时全盲（AutoHotkey 等非提权工具有相同已知限制）
- **GetWindowRect / SetCursorPos**：跨权限可用 → 因此"Alt+Tab 快按快放切向提权应用"能工作（keydown/keyup 都在普通前台处理完毕，后续管线直接读矩形/移光标）。**长按 Alt 浏览切换器再松手**的场景松手时提权窗口已是前台，keyup 被吞 → 该路径不稳定

### 决策

**接受现状，不改代码**（2026-09-24，选项 A）。提权窗口在日常切换流中占比低，且与同类非提权工具行为一致。

若未来需要支持：整体提权运行 / uiAccess 两条路线的研究项见 `docs/TODO.md`（TODO-1 / TODO-2）。

## UX 优化：点击抑制（v3.3）

> 2026-09-28 实施（commit `bb5033a`）。来源：烤机期真实使用反馈——"点击某些窗口（关闭、最小化）后鼠标就跳了"。

### 问题机制

点击窗口的**关闭/最小化**按钮 → 窗口消失 → OS 把前台交给 Z 序下一窗口 → `EVENT_SYSTEM_FOREGROUND` 照常触发。事件本身**不携带"是什么原因导致的切换"**，回调无法区分"用户主动手势切换"与"窗口在用户光标下消失"：

- 新前台窗口恰好覆盖光标位置（如最大化窗口）→ 不跳（所以只表现为"某些窗口"跳）
- 不覆盖 → 光标跳到新窗口中心；极端情况新前台在**另一块屏** → 跨屏跳变
- 同理：点任务栏图标切窗口，光标在任务栏上（不在新窗口内）→ 也跳

### 修复（方案 A：点击抑制）

语义：**手在鼠标上时，光标已在用户想要的位置，永远不要动它。**

- 新增 `WH_MOUSE_LL` 被动观察钩子（`MouseButtonHookProc`），只记录最近一次鼠标按键的时间戳，永远 `CallNextHookEx`
- 前台事件到达 UI 线程后：若 500ms（`MouseClickSuppressMs`）内有按键 → 跳过
- 三指滑动手势**不产生任何按键事件**（早前已实证），手势路径不受影响
- 隔离边界：全部代码在 `MainWindowVM.Touchpad.cs`；随前台钩子同门控注册/卸载；开关 OFF 零影响；键盘路径零影响；委托用字段持有（应用 v3.0 的根因教训）

### 已知折衷

点击后 500ms 窗口期内的**手势**会被一并跳过（场景罕见，下次手势自愈；日志 `skipped (recent mouse click)` 可诊断）。

未实施的备选（方案 B：检测上前台窗口是否已关闭/最小化，`IsWindow`/`IsIconic` 判定）留在本节备查——若验证后仍有残留跳变场景（如键盘 Alt+F4 关窗后跳变），可叠加。

## UX 优化：输入近因过滤（v3.4）——前台路径收拢为"仅响应手势"

> 2026-09-28 实施（commit `2a71801`）。来源：点击抑制验证后的剩余跳变反馈。

### 问题

两个键盘场景仍跳变：

1. **按 Win 键** → 开始菜单成为前台 → 光标跳到开始菜单所在屏
2. **Win+E** → 新资源管理器窗口成为前台 → 光标跳到新窗口

两道既有防线都拦不住：消重只覆盖"我们自己处理的键盘触发"（Alt+Tab/Win+方向键）；点击抑制只覆盖鼠标按键。

### 修复：输入近因过滤

关键实证前提（早前已验证）：**三指滑动手势是唯一既不产生键盘事件、也不产生鼠标按键事件的切换途径**。因此：

> 前台事件到达时，若近期（键盘 1000ms / 鼠标按键 500ms）内**有任何输入** → 跳过。
> 净效果：前台路径**只响应手势**引起的切换。

实现：新增被动 `WH_KEYBOARD_LL` 钩子只记时间戳（`KeyboardActivityHookProc`，无逐键日志），与鼠标按键钩子同门控注册/卸载。

### 已知折衷

- 打字/按键后 1s 内的首次手势会被跳过（下次手势自愈）
- 启动较慢的新窗口（按键后 >1s 才成为前台）仍可能触发跳变——近因窗口无法覆盖任意长的启动延迟
- 无输入的自发前台变更（通知弹窗、安装程序完成自弹）仍会触发移动——v1 设计即接受

### 触发判定全景（v3.8 起）

| 切换来源 | 前台路径行为 | 拦截层 |
|---------|-------------|--------|
| **shell 瞬态 UI**（任务视图/切换器/toast/跳转列表/托盘飞出/任务栏/桌面） | 跳过 | 类名+宿主进程过滤（v3.8，入口层） |
| 三指滑动手势 | **移动光标**（唯一放行） | — |
| Alt+Tab / Win+方向键 | 跳过（键盘路径自行处理） | 消重 + 键盘近因（双保险） |
| Win 键 / Win+E / 其他键盘动作 | 跳过 | 键盘近因（1s） |
| 点击关闭/最小化/任务栏/其他窗口 | 跳过 | 点击抑制（0.5s） |
| 无输入的自发前台变更 | 移动 | 无法区分，接受 |
| **光标移动中**的任何前台变更（通知 churn 等） | 跳过 | 光标静止门（250ms，v3.6） |
| 点击后 **>500ms 才诞生**的新窗口 | 跟随（涌现行为，v3.9 确认保留） | 逃逸点击门 |

## 可观测性：决策点证据日志（v3.5）

> 2026-09-28 实施（commit `e784a36`）。来源：用户要求"我的验证结果应该能从日志中看出来"。

**原则：不记录输入流，只记录决策依据。** 逐键日志已证不可交付（已拆除过）；而前台事件的 skip/trigger 决策点很稀有，在决策点上携带证据零噪音。

实现：两个被动钩子除时间戳外只**记录**（不打日志）最近输入身份（`vkCode` / 按钮消息）；决策点日志携带身份 + 距今时间 + 阈值。

### 人工验证 → 日志对照表（机械可核对）

| 用户动作 | 日志签名 |
|---------|---------|
| 按 Win 键 | `skipped (recent keyboard input: vk=0x5B XXms ago < 1000ms)` |
| Win+E | `skipped (recent keyboard input: vk=0x45 ...)`（E 的 vk=0x45） |
| 点关闭/最小化按钮 | `skipped (recent mouse click: msg=0x0201 XXms ago < 500ms)`（左键=0x0201） |
| 点任务栏图标 | 同上 |
| Alt+Tab | `Alt+Tab` + `skipped (keyboard-path repositioning in flight)` |
| 三指手势 | `triggering mouse positioning (..., lastKey=XXms, lastClick=XXms, lastMove=XXms)` —— **三者均超阈值（或 never）即证明放行正确** |
| 手势被误杀（bug 上报时） | skip 行的时间差直接区分：真刚输入过（正确拦截）vs 时间戳异常（bug） |
| 移动鼠标途中发生前台变更 | `skipped (cursor in motion: XXms ago < 250ms)` |
| 三指上滑开任务视图 / 下滑显示桌面 | `skipped (shell transient UI)` |

## UX 优化：光标静止门（v3.6）

> 2026-09-28 实施（commit `e3ec6f8`）。来源：托盘通知闪烁场景的"乱跳"反馈。

### 问题现场（日志实证）

Win11 托盘图标闪烁（消息通知）+ toast 弹窗引发**前台自发 churn**：toast（瞬态小窗，用后销毁）/ `Shell_TrayWnd` / 托盘溢出窗口 / 正常应用窗口在数秒内反复易主。用户把光标**移向**托盘途中，每次易主都触发一次重定位，光标被反复拽走（含一次左屏→顶屏的跨屏大跳）。

关键困难：此类 churn **不产生任何键盘/鼠标按键输入**，在输入近因过滤器眼里与手势不可区分（两者都是"零输入"）。

### 修复：光标静止门

剩余唯一区分器是**光标本身**：三指滑动的物理过程（300~600ms）光标必然静止；"正在挪动光标"则持续产生 `WM_MOUSEMOVE`。

> 前台事件到达时，若最近 250ms（`MouseMoveSuppressMs`）内有任何鼠标移动 → 跳过。
> 语义：**光标正被用户亲手操控时，任何路径都无权抢它。**

实现：现有鼠标钩子顺带为 `WM_MOUSEMOVE` 打时间戳（只写字段，不打日志）；判定行携带 `lastMove=` 证据。注意：自己的 `SetCursorPos` 也会刷新该时间戳，效果等同于跳跃后 250ms 内的重复事件自然去重。

### 否决的替代方案（留档）

| 方案 | 为什么拦不住本次场景 |
|------|---------------------|
| 前台稳定防抖（等 N ms 再动） | churn 后焦点可在正常窗口保持数秒（Chrome 保持了 4.7s），稳定后照样跳 |
| 瞬态窗口类过滤（toast 类名黑名单） | 拦不住"弹回正常窗口"的那一跳 |
| 限流（每 N 秒最多一次） | 拦不住第一跳 |

### 已知折衷

- 触控板"先单指挪位、<250ms 内紧接甩手势"的首次手势被吞，再滑自愈
- 光标完全静止时的自发前台变更仍会跳（v1 设计即接受，本次不动）

## UX 优化：shell 瞬态 UI 过滤器（v3.8）—— TODO-3 批处理执行

> 2026-09-29 实施。触发：场景 #3（三指上滑开 Task View，光标从左屏被拽到主屏中心）满足 TODO-3 的批处理条件。

### 问题

三指**上滑**（Task View）/ 下滑（显示桌面）在四道闸眼里与左右滑完全同构（无输入、光标静止）→ 全放行。Task View 的开关编排产生 `ForegroundStaging` → `XamlExplorerHostIslandWindow` 事件链，每条都触发重定位 → "乱跳"，含跨屏拽走。

### 修复：类名 + 宿主进程过滤（回调入口处，marshal 之前）

- **无条件过滤**（必是 shell 表面）：`XamlExplorerHostIslandWindow` / `ForegroundStaging` / `TopLevelWindowForOverflowXamlIsland` / `NotifyIconOverflowWindow` / `Shell_TrayWnd` / `Shell_SecondaryTrayWnd` / `Progman` / `WorkerW`
- **宿主进程过滤**：`Windows.UI.Core.CoreWindow` 仅当宿主进程 ∈ {explorer, ShellExperienceHost, StartMenuExperienceHost, SearchHost, TextInputHost}（保护正常 UWP 应用——其顶层窗口通常是 `ApplicationFrameWindow`，双保险）
- 过滤器在 hwnd 去重**之前**执行：shell 表面永远不会成为 `LastForegroundHwnd`，不污染手势切换的去重状态

对既有路径零影响：Alt+Tab 走键盘路径；左右滑手势的目标是正常应用窗口。

## 涌现行为确认：慢弹窗跟随（v3.9）

> 2026-09-30 用户反馈确认，决策：**保留现状不改代码**（选项 A）。

### 现象与机制

点击按钮后，应用创建新窗口可能耗时 0.5~3s——新窗口在任意屏幕弹出并成为前台时，点击门（500ms）已过期 → 放行 → 光标跟随到新窗口（实证：Qt 应用按钮点击后 976ms，新窗在左屏弹出，光标从主屏跨屏跟随）。

### 决策理由

- 这是点击门时间预算的**已知边界**，非闸门缺陷
- 用户实测反馈该行为"有意思和方便"——新窗口诞生时注意力目标本就是它，光标送达符合应用初衷（前台变更 → 光标就位）
- 已知瑕疵：**不一致**——同一操作弹得快（<500ms）不跟、弹得慢（>500ms）就跟随机决定。用户接受此瑕疵
- 若未来想收编为一致的特性，设计草图见 `docs/TODO.md` TODO-4

## 现状与建议（v3.1 更新）

| 项 | 决定 |
|----|------|
| 崩溃根因 | **已修复**（委托持有，`126685c`）|
| 消重误杀 | **已修复**（`fa98a0d`）；**7 天烤机期从该构建重新起算**（此前烤机构建含此 bug）|
| `EnableTouchpadAppSwitch` 默认值 | 仍为 OFF——烤机全绿后翻转 ON |
| `TouchpadExperimentStage` | 调试仪器保留；注册表缺失 = Stage 5 全功能 |
| 键盘路径 | 稳定运行；开关 ON 时经消重保持 100% 键盘路径（master 行为）|
| 触控板 hook 路径 | **复活**：手势跨屏跟随可用 |
| 烤机期通过后 | 翻转 `EnableTouchpadAppSwitch` 默认 ON；评估 Stage 开关去留 |

## 实验特性开发约束（防止再次污染稳定路径）

背景：共用路径污染事件证明"开关 OFF = 回到旧行为"**不能靠自觉保证**。git 管版本，管不了运行时耦合——污染是合法提交进共用方法的一行代码，diff 干净、review 看不出毛病。约束必须建立在**架构边界**上。

### 第一层：代码结构（物理隔离）— 已落地（v2.2）

- 实验代码全部集中在 `MainWindowVM.Touchpad.cs`（partial class）：hook 注册/卸载、回调、开关属性、守卫判定。文件头部注释写死了四条约束。
- 主文件 `MainWindowVM.cs` 相对 master **只允许纯增量**：
  - `class` → `partial class`
  - `OnLoaded` / `OnClosing` 各一个挂载点调用（`RegisterForegroundEventHooks()` / `UnhookForegroundEventHooks()`）
  - `Trigger*` 方法的 trigger 标记（默认参数，键盘调用点零改动）
  - `TryMouseRepositioning` 里唯一触点：`if (ShouldSkipMoveForForegroundHookPath(...)) return true;`
- 开关只出现在**两处**：hook 注册（挂载点）+ 回调入口（封送后复查）。
- 守卫按**路径**（`RepositionTrigger`）门控，不按全局开关状态：开关 ON 时键盘路径也不被拦。

### 第二层：可验证的检查（每次提交前，不是口头约定）

```powershell
# 检查 1：共用方法是否被改过（应只有 + 行和签名兼容改动，无既有行被修改）
git diff master -- MousePointerReposition/MainWindowVM.cs

# 检查 2：开关 OFF 跑键盘回归后，日志里不得出现：
#   "ForegroundEvent" / "hooks registered" / "skipping move"
```

**键盘回归清单（开关 OFF，每次合并前必跑）：**

1. Alt+Tab → 主屏窗口，居中
2. Alt+Tab → **左/上副屏窗口**，居中（污染事件的失灵场景）
3. Win+←/→ 跨屏，居中
4. 双击 Ctrl，居中
5. 连续 5 分钟混合操作，无崩溃

**触控板回归清单（开关 ON，每次合并前必跑）：**

1. 三指滑动切换应用，鼠标跟随（含跨屏负坐标）
2. **手势 → Win+Shift+方向键 → 立即连续手势**：手势不得失效（v3.1 消重误杀场景）
3. Alt+Tab → 鼠标走键盘路径移动；**紧接手势**：手势不得失效
4. 手势后按一下 Ctrl：不得误触发重定位（`fa98a0d` 顺带修复的泄漏副作用）
5. **点击窗口关闭/最小化按钮、点任务栏图标：光标不得跳变**（v3.3 点击抑制）；点击 0.5s 后手势恢复正常
6. **按 Win 键开开始菜单、Win+E 开新窗口：光标不得跳变**（v3.4 键盘近因抑制）；打字 1s 后手势恢复正常
7. **移动光标途中（如移向闪烁的托盘图标）前台变更：光标不得被拽走**（v3.6 静止门）；静止 0.25s 后手势恢复正常
8. **三指上滑开任务视图、下滑显示桌面、Win+Tab：光标不得跳变**（v3.8 shell 过滤器）；任务视图里点选窗口正常工作；手势切换到正常 UWP 应用（设置/计算器等）仍应跟随

### 第三层：流程纪律（隔离优先，合并靠后）

| 阶段 | 允许 | 禁止 |
|------|------|------|
| 实验期（未走通） | `MainWindowVM.Touchpad.cs` 内随便改，容忍冗余 | 改共用方法；以"顺手优化"为名动键盘路径 |
| 走通标准 | 连续 7 天日常使用零崩溃 + 键盘回归全绿 | — |
| 合并期 | **单独一个 commit** 做冗余合并（不带行为变化），合并后重跑回归 | 把"合并"和"修 bug"混在一个 commit 里 |

## 平台风险特性开发流程（Spike 先行）

### 为什么这类特性必须先做可行性研究

不是所有功能都需要。需要与否取决于风险的**不可推理性和不可防御性**：

| 风险特性 | 含义 | 后果 |
|------|------|------|
| 平台依赖 | 崩溃在 Win11/WPF 原生代码里，读文档、看 API 签名推不出来 | 设计评审发现不了 |
| 延迟失败 | 手势后 9-10 秒才崩，不是调用即崩 | 冒烟测试"跑通一次" ≠ 可行 |
| 不可捕获 | 0xC000041D 是 OS 强杀，try-catch 够不着 | 写再多防御代码也救不了 |

三者叠加意味着：**唯一能回答"可不可行"的方法是长时间真实环境实证**。

本项目实际顺序是反的：v1.0–v1.2 先完整实现（UI/注册表/文档/测试计划），v1.3–v1.13 十一个补丁试图挽救，v2.0 控制实验才是真正的可行性研究——**做晚了**。第一天就做 spike，~50 行扔掉式代码 + 一天观察即可得出同样结论。

### 正确顺序

**阶段 0 · 可行性 Spike（扔掉式）**

- 最小验证程序：只含最危险的那条链路（本例 = `SetWinEventHook(FOREGROUND)` → Dispatcher 封送 → `SetCursorPos`）
- 不要 Settings、不要注册表、不要 UI、不要文档
- **判定标准事先写死**："连续 N 天真实使用零崩溃"——不是"成功移动了一次"
- 失败 → 在 spike 里换方案（hook 参数 / SendInput / 其他触发源），迭代成本极低；全失败 → 存档结论，结束
- **spike 代码默认扔掉**，不做最终代码的地基——避免沉没成本陷阱（v1.3–v1.13 就是"都写这么多了，补补就能用"）

**阶段 1 · 完整实现**

- 以 spike 验证过的链路为核心
- 按"实验特性开发约束"做隔离
- 此时才配 Settings / UX / 文档

### 什么时候不需要 spike

纯应用层功能（如"开机自启"开关）：逻辑在自己代码里、行为可推理、异常可捕获、测试可覆盖——直接实现即可，避免过度工程。
