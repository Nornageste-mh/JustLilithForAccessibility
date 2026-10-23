# JustLilithA11y

> 《不/存在的莉莉丝》（The NOexistenceN of Lilith）的读屏无障碍补丁。
> 让盲人玩家能用屏幕阅读器玩本作。
>
> 平台层与契约层来自 [A11yFramework](https://github.com/Nornageste-mh/Framework)（MIT）。

| | |
|---|---|
| 中文名 | **不/存在的莉莉丝** |
| 英文名 | The NOexistenceN of Lilith |
| Steam | AppID **4643090** |
| 引擎 | Unity **2021.3.45f2c1** / **IL2CPP** |
| 模组运行时 | **BepInEx 6.0.0-be.788**（IL2CPP，含 Il2CppInterop 1.5.3） |
| 插件 | `JustLilithA11y.dll`，GUID `justlilith.a11y.reader`，版本 `0.1.0` |
| 平台层 | A11yFramework **0.2.4.0**（整份拷入，只增不改） |
| 命名空间 | `JustLilithA11y` |
| 游戏安装目录（本机） | `D:\Steam\steamapps\common\The NOexistenceN of Lilith` |
| 可执行文件 | `Lilith.exe`（数据目录 `Lilith_Data`） |

## 说明

这是一个 BepInEx 插件，挂在 Unity / IL2CPP 的《不/存在的莉莉丝》上，做三件事：

1. **让界面能听、能不用鼠标操作**——标题菜单 / 设置 / 画廊 / 聊天等面板用一套导航键操作，
   每移动一格都念出「它是什么、当前什么状态」；
2. **把选项读出来**，并支持数字键直接选择，不必用鼠标，也不必先进入导航模式；
3. **台词以游戏语音为准**——本作台词**全程有配音**，补丁默认**不朗读台词**
   （配置项 `[朗读] 朗读无配音剧情` 默认关），只负责判定「这句有配音」并保证
   界面提示条、对话气泡之外的浮动文字不被漏掉（可用 `[朗读] 朗读界面浮动文本` 关掉）。

**明确不做的**：不改剧情、不改数值、不跳过内容、不替玩家选分支、
不绕过任何技术保护措施、不修改游戏文件。无障碍层**绝不能扩大可达范围**——
不可用的选项故意不点，会剧透的文本有闸门（只念屏幕上真正看得见的），
任何「替你跳过」的兜底都不写（历史上写过一次，已删除）。

> ## ✅ 当前状态：0.2.0 已实机跑过（2026-10-22 / 2026-10-23）
>
> **已实机验证**（有当天日志撑腰，不是推断）：
>
> - 设置面板导航：进入导航模式后能逐项走过 **8 个 tab**
>   （其他 / 快捷键 / 我 / 莉莉丝 / 语言 / 声音 / 图像 / 控制），
>   开关行念成「游戏时不显示，开关，关，按钮」，滑条念成「对话显示速度，滑条，50%」，
>   下拉念成「简体中文 ▼，按钮」，输入框念出「你的名字，输入框，当前内容 …」，
>   图标控件念出「关闭 / 单曲 / 选择文件夹」；
> - 回车激活 tab 后会重新扫描并播报「界面已更新」；
> - 选项弹出时念「有 N 个选项。选项 1：…」，数字键能直接选中；
> - 退出导航后，游戏自带热键 `Ctrl+Alt+M` 照常打开音乐播放器——**补丁不抢键**。
>
> **已知的不足**（都在这份文档里写着，别当成没做）：
>
> - 生日行的「开关」其实是**展开日历**，`生日` 行的状态语义仍待修；
>   展开后的日历格 `DateCell` 目前念「14，按钮」，**没有日期语义**；
> - 「我」tab 里 `Btn_FolderSelect` 会被上方行标题覆盖，念成「你的名字，按钮」，
>   真实含义是「选择文件夹」（同一个控件在「声音」tab 里是对的）；
> - 导航模式内 **方向键移动 / 滑块左右调 / PageUp/PageDown 分组**走的是平台层既有逻辑，
>   本轮自动化**注入不了方向键**，只有「回车激活」是实测过的，这三项**未逐项实测**；
> - 导航中**不静默游戏自己的热键**（native 输入，没有可拦的托管入口）——这是已知取舍；
> - 开场「记忆相框 / 照片」的逐句字幕，探针对全部 `TMP_Text` 与 legacy `Text`
>   做关键字全量扫描是 **0 命中**（既不是 TMP 也不是 Text，疑似烘焙在图片或视频里），
>   所以补丁读不到它；因本作全程配音，此项不阻塞通关。
>
> 还没验证的每一条都记在 [docs/TODO-待验证.md](docs/TODO-待验证.md)，
> 复测步骤见 [测试问卷.md](测试问卷.md)，静态调研见
> [无障碍可行性验证.md](无障碍可行性验证.md)。
>
> 把未验证的事写成已验证，是本项目明令禁止的（见下「几条纪律」）。

> 发布：<https://github.com/Nornageste-mh/JustLilithForAccessibility/releases/tag/v0.2.0.0>（资产 JustLilithA11y-0.2.0.0.zip，33.2 MB / 264 条目；上一版 <https://github.com/Nornageste-mh/JustLilithForAccessibility/releases/tag/v0.1.0.0>）

> ## 🆕 v0.2.0 新增（2026-10-23）
>
> **0.2.0 已发布**（2026-10-23）；下面是这一版相对 0.1.0 加了什么。
>
> 0.2.0 是照着三条用户反馈改的：**普通状态进不了导航**（游戏菜单默认不显示）、
> **没法跟莉莉丝交互**（头/脸/胸口/身体/手/裙子/腿点不到）、**菜单存在时间太短**。
>
> 1. **打开功能菜单的快捷键 `Ctrl+Alt+O`**（配置 `[界面导航] 打开菜单快捷键` 可改）。
>    它打开的是**游戏原生的功能菜单**（置顶 / 礼物 / 玻璃 / 帽子 / 退出 / 等待说话 / 关闭 …），
>    再按一次关闭；打开、关闭都会念一句（「功能菜单已打开。」/「功能菜单已关闭。」）。
>    为什么是 `O`：游戏自带那 9 条热键用的是别的字母，`O` 空闲。
> 2. **身体部位导航**：导航模式里多出一组「莉莉丝交互」，8 项：
>    **头 / 脸 / 胸口 / 身体 / 左手 / 右手 / 裙子 / 腿**（顺序按身体从上到下）。
>    回车 = 走**游戏自己的点击链路**（先把系统光标移到该部位，再让游戏自己按下/抬起），
>    由游戏决定放音效、播动画还是说台词；补丁只负责「点到那儿」并报告有没有反应。
>    实现见 `LilithBodyNav.cs`（造一个隐藏 Canvas + 8 个透明虚拟按钮，
>    `CanvasGroup.alpha=1`、`Image.color.a=0`、`raycastTarget=true`，能被导航扫到但不挡真实 UI）。
> 3. **菜单不自动消失**：`[界面导航] 功能菜单不自动隐藏`（默认开）只在
>    「补丁打开、且玩家没主动关」期间保活，**对话一开始就停止保活**（剧情优先）；
>    `[其它] 对话气泡不自动消失`（默认开）在气泡显示期间周期性把计时器重置回去，不掐断游戏逻辑。
> 4. 导航开关键不变：`Ctrl+Alt+Plus`（主键盘加号）；`Tab` 仍是兜底。
>
> **本轮没逐项实测的**：8 个部位各自的反应是否都能听到/看到（实机只确认到
> 「部位命中区被正确派发、语音 clip 变了」，不是每个部位都会出新台词）；菜单保活放进长剧情后的表现；
> `Ctrl+Alt+O` 与游戏自带热键重叠时的先后。这些都记在 [docs/TODO-待验证.md](docs/TODO-待验证.md)。

## 安装（仅限正版玩家）

发布形态是「把 `mod\package\` 里的东西整体拷进有 `Lilith.exe` 的那一层」，
**没有安装程序，也不改动任何游戏文件**。详细步骤、目录树、卸载、每一项配置的含义，
见 [mod/package/安装说明.md](mod/package/安装说明.md)；
装不上 / 没声音 / 菜单按不动，见 [mod/package/常见问题.md](mod/package/常见问题.md)。

**请通过 Steam 购买正版后再使用本补丁。** 本补丁是玩家自制的第三方工具，
与游戏作者没有任何关系，也没有得到授权或背书。

## 主要快捷键

| 快捷键 | 功能 | 状态 |
|---|---|---|
| **`Ctrl+Alt+Plus`**（主键盘加号，即 `Ctrl+Alt+Shift+=`） | **进入 / 退出界面导航模式** | **已实机验证**；默认值，可在配置里改 |
| **`Ctrl+Alt+O`** | **打开 / 关闭游戏的功能菜单**（0.2.0 新增；不用先有可见菜单） | 已实现；**未逐项实测**（与游戏自带热键重叠时未测） |
| `Tab` | 旧的导航切换键，仍作为兜底生效 | 已实现（兜底） |
| `↑` `↓` | 上一项 / 下一项（念出它是什么、什么状态） | 平台层已实现；**本轮未逐项实测** |
| `←` `→` | 调整滑条 | 平台层已实现；**本轮未逐项实测** |
| `回车` | 激活控件（按钮、开关、tab、输入框聚焦） | **已实机验证**（设置面板 tab 切换） |
| `空格` | 激活控件 | 平台层已实现；本轮未单独实测 |
| `Home` / `End` | 第一项 / 最后一项 | 平台层已实现；本轮未逐项实测 |
| `PageUp` / `PageDown` | 切换面板组 | 平台层已实现；本轮未逐项实测 |
| `1`-`9` | 选项出现时直接选第 1–9 项 | **已实机验证** |
| `Backspace` | 重读最近朗读过的一段（选项算一段） | 平台层已实现 |
| `0` | 「沉默键」：清掉补丁自己的待选列表（本作没有限时选择，平时无效） | 已实现（保留语义） |

> **为什么默认是 `Ctrl+Alt+Plus` 而不是 `Ctrl+Alt`**：本作自带 **9 条 `Ctrl+Alt+字母` 热键**
> （`Ctrl+Alt+L` 强制点击穿透、`Ctrl+Alt+S` 静音、`Ctrl+Alt+N` 随手记、`Ctrl+Alt+B` 详细备忘、
> `Ctrl+Alt+R` 提醒事项、`Ctrl+Alt+P` 番茄钟、`Ctrl+Alt+T` 计时、`Ctrl+Alt+K` 闹钟、
> `Ctrl+Alt+M` 音乐播放器），纯 `Ctrl+Alt` 会和它们冲突；加号不在游戏热键表里，
> 而且主键盘 `=` 上的加号还要多按一个 `Shift`，误触概率最低。
> `Ctrl+Alt+O`（打开菜单，0.2.0 新增）同理：`O` 也不在那 9 条里。
>
> 实现位置：逐作层 `mod\src\JustLilithA11y\Plugin.cs:195`（`Config.Bind("界面导航", "导航开关快捷键", "Ctrl+Alt+Plus", …)`）
> 与 `Plugin.cs:535` 的 `NavTogglePressed()`；平台层 `UiNav.cs:2160-2161`
> （`navHook != null ? navHook() : KeyEdge.Pressed(KeyCode.Tab)`——钩子没填就退回 `Tab`）。
> 配置里写错会自动退回 `Tab` 并在 `LogOutput.log` 里提示。

## 已知问题 / 注意事项

- **导航模式需要「先有可见菜单」**：平台层只扫当前场景里看得见的 `Selectable`，
  游戏平时不显示功能菜单时，按 `Ctrl+Alt+Plus` 进导航会「进去了但没东西可走」。
  0.2.0 的对策是按 **`Ctrl+Alt+O`** 先把功能菜单叫出来，再进导航
  （菜单默认常驻，见上面「v0.2.0 新增」）。
- **身体部位的反应由游戏决定**：回车只是把一次真实点击送到该部位，
  放不放音效 / 播不播动画 / 说不说台词是游戏自己的逻辑。
  实机确认到「部位命中区被正确派发、语音 clip 发生了变化」，但**不是每个部位都会出新台词**，
  8 个部位也没有逐项听完；某个部位没反应时，把 `LogOutput.log` 里的 `[Lilith]` 行发给我们。
- **生日日历**：生日行的「开关」是展开器不是开关，「生日」行状态语义待修；
  展开后的日历格 `DateCell` 念「14，按钮」，没有「14 日」这类语义
  （`UiNav.Game.cs:193-202` 有按路径补语义的尝试，实机仍未达标）。
- **`Btn_FolderSelect`**：在「我」tab 里被行标题覆盖，念成「你的名字，按钮」；
  真实含义「选择文件夹」（`UiNav.Game.cs:212` 记录了这条实机证据）。
- **方向键 / 滑块 / 分组键未逐项实测**：本轮自动化只能注入「导航开关」和「回车」。
- **导航中不静默游戏热键**：本作输入走 native，没有可挂的托管入口，属已知取舍。
- **开场「记忆相框」字幕读不到**：探针全量扫 `TMP_Text` / `Text` 均 0 命中，
  疑似烘焙在图片 / 视频里；因全程配音，不阻塞。
- **台词默认不朗读**：本作全程配音，`[朗读] 朗读无配音剧情` 与 `无法判定时也朗读`
  默认都是**关**；要念无配音行才需要打开（效果未逐句核对）。
- **本作没有限时选择**：反编译实查 `DialogueOption` 只有 `text` / `nextId`，
  `DialogueNode` 只有 `duration`，没有任何倒计时字段；
  配置项 `[朗读] 限时选择时长` 是**保留字段，无效果**。
- **本作没有手机**：但有聊天 / 备忘 / 待办类面板（`ChatListWindow` / `MemoView` / …），
  目前靠导航逐控件朗读；「新消息自动播报」**未实现**。
- **游戏更新即失效**：更新会换掉 `GameAssembly.dll`，interop 与补丁点都要重新确认。
- 黑色控制台窗口不会自动隐藏（本作配置里没有这一项）。

## 构建

需要 .NET SDK（本机 `D:\dotnet\dotnet.exe`，dotnet 8）与**已安装 BepInEx 6 的游戏目录**
（编译要拿游戏目录里的 `BepInEx\core\*` 与 `BepInEx\interop\*` 当引用）。

```powershell
cd mod
.\build.ps1 -GameDir "D:\Steam\steamapps\common\The NOexistenceN of Lilith"
# 产物：mod\package\  ← 直接拷进有 Lilith.exe 的那一层

.\release.ps1 -GameDir "D:\Steam\steamapps\common\The NOexistenceN of Lilith"
# 产物：mod\dist\JustLilithA11y-<版本>.zip  ← GitHub Releases 用
```

`build.ps1` 的五个步骤：检查游戏目录（找不到 `Lilith.exe` 就 `exit 1`）→
取 BepInEx `6.0.0-be.788`（先看 `mod\dl\` 缓存，再看 `$env:A11Y_CACHE`）→
取 `nvdaControllerClient.dll`（x64）→ 编译 → 组装 `package\`。

**三条发布前守卫**（`build.ps1:121-145`，别删）：

1. `BepInPlugin` 的版本号必须能被 `[System.Version]::TryParse` 解析——
   写坏会让 BepInEx **静默跳过整个插件**，游戏看起来和没装一样；
2. `BepInPlugin` 版本必须等于 `csproj` 的 `<Version>`（现为 `0.1.0`）；
3. `dotnet build -c Release -v minimal -nowarn:MSB3277 -p:LilithGameDir="$GameDir"`
   ——`LilithGameDir` 是 `JustLilithA11y.csproj` 里所有 `HintPath` 的前缀。

**打包的两条红线**（`release.ps1:82-101` + `tools\makezip.ps1`）：

- **不要用 `Compress-Archive`**：Windows PowerShell 5.1 版的它用反斜杠生成条目名，
  Linux/macOS 解开会得到一堆名字里带反斜杠的平铺文件；`makezip.ps1` 手工建条目，
  分隔符固定 `/`，脚本末尾还会自检有没有反斜杠条目；
- **`nvdaControllerClient.dll` 要放两处**：`BepInEx\plugins\` 与包根
  （`build.ps1:170-173`；Windows 加载器会先查应用目录）。

**不分发**：`BepInEx\interop\`、`BepInEx\unity-libs\`（首次运行按本机生成）、
`BepInEx\config\*.cfg`（玩家自己的配置）、争渡的 `ZDSRAPI.dll`（专有软件）。

### 目录结构

```
justlilith_a11y\
├─ README.md                       ← 本文件
├─ 测试问卷.md                      ← 实机测试步骤（第 8 节是「不装补丁游戏是否正常」的硬要求）
├─ 无障碍可行性验证.md              ← P0 静态调研：文本在哪、补丁点在哪、还缺什么证据
├─ docs\
│   ├─ 无障碍补丁流水线.md          ← Framework 通用流水线（原样拷贝）+ 本作适配说明
│   ├─ design-notes-ui-nav.md       ← 控件命名/降级/导航键的设计笔记（写于 0.2.2 前后，部分结论已被实现取代）
│   └─ TODO-待验证.md               ← 逐条待实机验证项（需要什么证据 / 由谁验证）
├─ mod\
│   ├─ build.ps1                   ← 编译 + 组包
│   ├─ release.ps1                 ← 打发布 zip
│   ├─ tools\makezip.ps1           ← 手工构造 zip 条目（'/' 分隔）
│   ├─ licenses\                   ← 第三方许可证全文 + THIRD-PARTY-NOTICES.txt
│   ├─ package\                    ← 用户文档源（安装说明/常见问题），build 会原样保留
│   └─ src\JustLilithA11y\         ← 插件源码（平台层 + 本作逐作区），见下
├─ tools\
│   ├─ compile_check.ps1           ← Framework 仓库的平台层自检（本仓库没有 platform\，见注）
│   ├─ check-staged.ps1            ← 提交前闸门：拦版权内容、第三方二进制、无 BOM 的 .ps1
│   └─ lint_repeat.ps1             ← 断言「重读缓冲区是唯一播报出口」
└─ probe\                          ← 实机探针（不进 package）
    ├─ run_probe.ps1 / screenshot.ps1
    ├─ plugin\                     ← 探针插件（转储场景树、类型、方法）
    └─ decomp\types_c.txt          ← 已产出的类型名表（769 个类）
```

> `tools\compile_check.ps1` 是 **A11yFramework 仓库**的自检脚本：它断言平台层
> 只依赖引擎与 BepInEx、不引用 `Assembly-CSharp`。逐作骨架里平台层已经和逐作代码
> 合并在 `mod\src\JustLilithA11y\` 同一个工程里编译，**没有 `platform\` 目录**，
> 所以这个脚本在本仓库会自己报「找不到 platform\（请在 A11yFramework 仓库根目录下运行）」
> ——那是正确行为，不是坏了。逐作仓库的编译检查用 `mod\build.ps1` 第 4 步。

### 模组做了什么

| 功能 | 挂载点 | 状态 |
|---|---|---|
| 菜单 / 设置 / 聊天 / 画廊键盘导航 | 每帧扫描 uGUI `Selectable`，逐作层补标签（`UiNav.Game.cs`） | **已实机验证**（设置面板 8 个 tab） |
| 打开 / 关闭游戏功能菜单（`Ctrl+Alt+O`） | `LilithMenu.cs`：`Hotkey` 解析 → `LilithApi.OpenMenu/CloseMenu`（先 `CharacterController.ToggleSetting`，失败退 `SettingView.Show`）+ 1.2 秒「待确认可见」状态机 | 已实现；**未逐项实测** |
| 功能菜单常驻（不自动隐藏） | `LilithMenu.cs` 的 `KeepAlive()`：只在补丁打开期间重开（冷却 1.5 秒、上限 500 次），**对话一开始就停** | 已实现；长剧情下未实测 |
| 身体部位导航（8 项：头/脸/胸口/身体/左手/右手/裙子/腿） | `LilithBodyNav.cs` 隐藏 Canvas + 8 个透明虚拟 Button → `LilithApi.TriggerPart`（光标就位 + 游戏原生点击链路；1.5 秒兜底，判据=新台词或 voice clip 变化） | 已实现；**逐部位反应未逐项实测** |
| 控件朗读（按钮 / 开关 / 滑条 / 下拉 / 输入框 / 图标） | 平台层 `Describe` + 逐作层 `GameLabelOf` | **已实机验证**（读法见上「当前状态」） |
| 回车激活后重扫并播报「界面已更新」 | `UiNav.cs:2143` | **已实机验证** |
| 朗读选项 + 数字键直选 | `Choices.cs`：`DialogueNode.HasOptions` / `PlayerLineController.IsAnyOptionsShowing` | **已实机验证**（读「有 N 个选项。选项 1：…」，数字键选中） |
| 台词朗读（有配音只放语音 / 无配音 TTS） | `Reader.cs`：订阅 `OnDialogueStart/Advance/End/OptionSelected` + 每帧轮询兜底 | 已接通；**本作全程配音，默认不念台词** |
| 配音判定（有配音就不重复念） | `DialogueManager.ResolveNodeSoundId`（3979）/ `DialogueLineRepository.TryGetVoiceSoundId`（190） | 已实现（依据反编译签名，逐句效果未核对） |
| 界面浮动文本（提示条 / 开场字幕） | `ScreenText.cs`：每 0.22 秒扫可见 `TMP_Text`，去重 | 已实现；开场「记忆相框」字幕是否覆盖**未确认** |
| 剧透闸门（只念正在显示的） | 可见性 + 面板显隐 + 去重三条件 | 已实现 |
| 退出前二次确认 | `[界面导航] 退出确认对象名`（默认 `quitGameButton`） | 已实现（默认值来自反编译实查） |
| 重读一段 | 重读缓冲区 | 平台层已实现 |
| 启动播报 | `Awake` | 已实现 |
| 限时选择放宽 | — | **本作没有限时选择**，配置项为保留字段 |
| 手机 / 聊天消息自动播报 | — | **未实现**（面板内可导航逐条听） |

### 几条踩过的坑（改之前请先读）

**本作自己踩过的**：

- `new_game.py` 生成的 `JustLilithA11y.csproj` 里，XML 注释中原本写着命令行双横线参数名，
  而 XML 注释里**不允许出现连续两个减号**，MSBuild 会报 MSB4025 让新作一生成就编不了。
  该行的说明已改成不带横线的写法（`JustLilithA11y.csproj:7-9`）。
- **`NameAlias` 表是忽略大小写的**：实机曾同时写了 `closeButton` 与 `CloseButton`，
  静态构造抛 `ArgumentException`，整个 `UiNav` 类初始化失败（日志里是
  `TypeInitializationException`），导航 100% 不可用。加行前先按忽略大小写搜一遍
  （`UiNav.Game.cs:46-54`）。

**从 A11yFramework 继承来的**：

- IL2CPP 的静默失败：`PatchAll` 抛异常时**其余补丁照样生效**，症状是
  「只有一个功能不工作」，非常难查——所以 `Patches.cs:78-79` 用 `LogError` 而不是吞掉。
- IL2CPP 下裸 `as` / `is` 判 `RectTransform` 一类会静默失败，必须走 `TryCast`。
- `List.Sort` 的比较函数必须满足传递性，否则顺序会随机抖。
- 世界坐标 ≠ 像素坐标：`WorldToScreenPoint` 之后才能按屏幕位置分档。
- 被复用的文本控件可能在面板还没显示时就被写入文本，只挂 `set_Text`
  会把玩家不该看到的文本念出来——所以要剧透闸门。
- **无障碍层不能扩大可达范围**：曾经写过「导航不到就自动点掉」的兜底，已删除。
- 别用 PowerShell 文本 cmdlet（`Get-Content`/`Set-Content` 往返）改源码：
  编码和 BOM 会被悄悄改掉；含中文的 `.ps1` 必须存成 **UTF-8 带 BOM**，
  否则 Windows PowerShell 5.1 按 GBK 解码会直接语法错误。
  （`edit` 类工具也会剥 BOM，改完记得复查——本项目实际踩过。）

## 如何复现分析

**类型与方法**：`probe\decomp\types_c.txt` 是从实机 `GameAssembly.dll` 的元数据里
导出的类名表（769 条），本文件里 `DialogueManager`、`DialogueBubbleUI`、
`DialogueLineRepository`、`OpeningPerformanceManager` 等名字都出自这里；
`DialogueManager.cs` / `DialogueBubbleUI.cs` 的行号引用来自 ilspycmd 反编译
`BepInEx\interop\Assembly-CSharp.dll`。行号是**某一版 interop 的行号**，游戏一更新就漂。

**运行时**：`probe\run_probe.ps1` 会把探针插件拷进 `BepInEx\plugins\` 并启动游戏，
转储场景树 / `Selectable` / 文本控件的**类型名、方法名、对象名与长度**
（**不写台词原文**，`DumpUiText` 默认关闭）。

**文本**：本作没有 Naninovel。台词正文在 `DialogueNode.text` 与运行时
`DialogueManager.Say(text, …)`（`DialogueManager.cs:3705`）直接灌进气泡的通路上，
气泡控件是 `DialogueBubbleUI._dialogueText`（`DialogueBubbleUI.cs:323`）；
开场字幕走 `OpeningPerformanceManager` 的 cue（`OpeningLineDatabase.GetText`）。
**离线剧本导出仍未做**，见 `无障碍可行性验证.md` 第 2 节与 `docs\TODO-待验证.md`。

**分析笔记故意不入库**：含台词原文的导出物、反编译产物、游戏截图、含台词的会话日志
全部在 `.gitignore` 里（`extracted/`、`decompiled/`、`probe/evidence/*.log`、
`probe/evidence/*.png`、`LogOutput.log`）。

## 合规说明

- 游戏著作权归作者所有。本仓库**不含任何游戏资源**：没有剧本原文、没有图片、
  没有音频、没有字体，也没有反编译得到的游戏代码。
- 文档里不写台词原文（剧透 + 再分发）；探针只记类型名/方法名/对象名/长度。
- 不绕过任何技术保护措施，不修改游戏文件，不联网。
- 修改自用通常在灰色地带被容忍，但**分发修改版**与**绕过技术保护措施**都有法律风险；
  本项目只分发自己写的代码与第三方运行时的原样副本。
- 第三方二进制以**未修改的独立动态库**形式分发（LGPL 的「共享库机制」），
  许可证全文随包附上，来源与获取途径写在本仓库
  `mod\licenses\THIRD-PARTY-NOTICES.txt`。

> ⚠️ **本仓库还没有根 `LICENSE` 文件**（骨架生成器没生成），
> 也没有 `NOTICE.md` / `CHANGELOG.md`。发布前必须补：
> 平台层代码是 A11yFramework（MIT, Nornageste）的拷贝，保留原许可与署名；
> 其余逐作代码与文档的建议同样以 MIT 发布。**这是待办，不是已完成。**

## 许可

- **平台层与契约层**（`Speech.cs`/`Nvda.cs`/`Zdsr.cs`/`Sapi.cs`/`Hotkey.cs`/
  `UiNav.cs`/`KeyEdge.cs`/`Repeat.cs`/`A11yHost.cs`/`GlobalKeys.cs`）：来自
  [A11yFramework](https://github.com/Nornageste-mh/Framework)，**MIT**。
- **逐作代码与文档**（`Plugin.cs`、`UiNav.Game.cs`、`Reader.cs`、`Choices.cs`、
  `ScreenText.cs`、`Patches.cs`、`TextProc.cs`、`docs\`、
  `mod\package\` 下的说明书、构建脚本）：本项目自己写的，目标是 MIT
  （**根 `LICENSE` 尚未落盘，见上**）。
- **第三方运行时**：BepInEx 6.0.0-be.788（LGPL-2.1）、Il2CppInterop 1.5.3（LGPL-3.0）、
  HarmonyX / Mono.Cecil / MonoMod / Cpp2IL / AsmResolver / AssetRipper（MIT）、
  dobby（Apache-2.0）、UnityDoorstop（LGPL-2.1）、NVDA Controller Client（LGPL-2.1）、
  .NET 6 mini-coreclr（MIT）。许可证全文在 `mod\licenses\`，
  逐作声明在 `mod\licenses\THIRD-PARTY-NOTICES.txt`。

## 几条纪律

1. **不要把未验证的事写成已验证。** 允许写「尚未实机复测」，不允许把 TODO 写成「已支持」。
2. **无障碍层绝不扩大可达范围**：不可用选项故意不点，剧透文本有闸门。
3. **发布前必须验证「关掉补丁时游戏本身行为不变」**——`测试问卷.md` 第 8 节，硬要求。
4. **别把游戏内容放进仓库**：一句台词、一张截图都不行。
