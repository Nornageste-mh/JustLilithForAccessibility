# 待实机验证清单

> 这份清单只列**没有实机证据就不能写进文档、也不能写进代码**的东西。
> 每条都写明：**还缺什么证据、由谁给、验证完能解锁什么**。
>
> 纪律：**「还没验证」写成「已支持」是本项目明令禁止的。**
> 允许的写法是「尚未实机复测」。本清单归零 = 这个补丁可以发第一个测试版。
>
> 状态标记：`[ ]` 未验证 ｜ `[~]` 部分证据 ｜ `[x]` 已验证（**只有真的跑过才能打 x**）
>
> 最后更新：2026-10-22（实机验证当天）
> 当前完成度：**7 / 23 已验证，9 条部分证据，7 条仍未验证**
> 证据来源：2026-10-22 实机日志与操作记录（设置面板 8 个 tab 导航、选项朗读 +
> 数字键、导航开关 `Ctrl+Alt+Plus`、退出导航后游戏热键仍正常），以及反编译
> `BepInEx\interop\Assembly-CSharp.dll` 的实查结论。**没有引入任何离线统计或未跑过的推断。**

---

## A. 阻塞代码的（不解决就写不出补丁点）

### [x] A1. 导航模式键位 —— 最终定为 `Ctrl+Alt+Plus`

- **结论**：`Ctrl+Alt+Plus`（主键盘加号 = `Ctrl+Alt+Shift+=`），`Tab` 作为兜底仍生效。
- **为什么不选纯 `Ctrl+Alt`**：本作自带 **9 条 `Ctrl+Alt+字母` 热键**
  （`Ctrl+Alt+L` 强制点击穿透、`Ctrl+Alt+S` 静音、`Ctrl+Alt+N` 随手记、
  `Ctrl+Alt+B` 详细备忘、`Ctrl+Alt+R` 提醒事项、`Ctrl+Alt+P` 番茄钟、
  `Ctrl+Alt+T` 计时、`Ctrl+Alt+K` 闹钟、`Ctrl+Alt+M` 音乐播放器），纯 `Ctrl+Alt` 会冲突；
  加号不在游戏热键表里，且主键盘 `=` 上的加号要多按一个 `Shift`，误触概率最低。
- **实现位置**：逐作层 `mod\src\JustLilithA11y\Plugin.cs:189`
  （`Config.Bind("界面导航", "导航开关快捷键", "Ctrl+Alt+Plus", …)`）、
  `Plugin.cs:477` 的 `NavTogglePressed()`；平台层 `UiNav.cs:2160-2170`
  （`navHook != null ? navHook() : KeyEdge.Pressed(KeyCode.Tab)`，钩子没填才退回 `Tab`）。
- **证据**：2026-10-22 实机，按 `Ctrl+Alt+Plus` 能进 / 出导航模式并逐项朗读设置面板；
  退出导航后按游戏自带 `Ctrl+Alt+M` 正常打开音乐播放器（不抢键）。
- **解锁**：文档键位表去掉「待实现」标注（README / 安装说明 / 常见问题已改）。

### [~] A2. 说话人名字的来源

- **已有证据**：`DialogueNode.speaker` 是**字符串**字段
  （`Reader.cs` 使用；`Plugin.cs:125-128` 的配置帮助文本记录了这一点）。
  另有 `DialogueLineEntry`（`probe\decomp\types_c.txt:190`）与
  `DialogueLineRepository`（`:176`）可作旁证。
- **仍缺**：`speaker` 的实际取值是不是中文名（还是内部 ID）。日志里会打印前几条，
  **没有摘录核对**。若不是中文名，需要一张角色表。
- **由谁验证**：Lead / 测试者看 `[Reader]` 日志。
- **解锁**：确认 `[朗读] 朗读时带上说话人` 默认值是否合适；文档里「名字念成未知」那节。

### [~] A3. 剧情文本的载体与解析方式

- **已有证据**（反编译实查）：
  - 节点文本：`DialogueNode.text`；
  - 运行中直接灌进气泡的台词：`DialogueManager.Say(text, …)`（`DialogueManager.cs:3705`）；
  - 气泡控件：`DialogueBubbleUI._dialogueText`（`DialogueBubbleUI.cs:323`）；
  - 开场演出：`OpeningPerformanceManager` 的 cue + `OpeningLineDatabase.GetText(key, language)`；
  - 判定当前行：`DialogueManager.IsDialogueActive`（3072）/ `CurrentNode`（3116）。
- **仍缺**：**离线剧本导出**（资源包里存的是字符串还是 ID、要不要查 Localization 表），
  以及配音 id 与行号的完整对照。只记结构，不把正文带出仓库。
- **由谁验证**：Lead（解包 + 离线统计）。
- **解锁**：`无障碍可行性验证.md` 第 3 节的量化统计、配音覆盖审计。

### [~] A4. 台词打印的补丁点：实现已就位，实机日志未核对

- **已有实现**：`Reader.cs` 订阅 `DialogueManager` 的四个实例事件
  （`OnDialogueStart` 2559 / `OnDialogueAdvance` 2574 / `OnDialogueEnd` 2604 /
  `OnOptionSelected` 2619，统一走 `add_Xxx` 方法，追加语义），
  另有每帧轮询兜底（`IsDialogueActive`/`CurrentNode`/气泡 TMP）；
  `Patches.cs` 提供 Harmony 第三条腿（默认关，配置 `[其它] 挂载 Harmony 台词补丁`）。
- **已有间接证据**：同一套 Il2CppInterop 反射通路在**选项**上实机跑通了
  （`PlayerLineController.IsAnyOptionsShowing` / `_texts` / `OnButtonClicked` 都工作）。
- **仍缺**：实机日志里 `[Reader]` 的 `path=`（事件 / 轮询）到底是哪条在走；
  以及「每打印一行触发一次」的逐条记录。
- **由谁验证**：Lead / 测试者看 `LogOutput.log`。
- **解锁**：删掉「补丁点未接通」的旧说法（文档已改）。

### [ ] A5. 一行台词会不会被拆成多次打印（判重与「一段」的边界）

- **现状**：仍未知。上一作有「分页/换行继续」的问题。
- **需要什么证据**：实机日志里同一段剧情打印了几次、每次的长度与内容长度是否累计。
- **由谁验证**：Lead（日志）。
- **解锁**：`Backspace` 重读的「一段」范围；避免重复朗读。

### [x] A6. 选项：出现时刻、文字写入时刻、可否选择

- **结论**：选项朗读与数字键直选**已实机验证**：弹出时念
  「有 N 个选项。选项 1：…」，数字键 1–9 能直接选中。
- **实现位置**：`Choices.cs`（`DialogueNode.HasOptions`/`DialogueOption.text`；
  玩家台词选项走 `PlayerLineController.IsAnyOptionsShowing`（4170）/`_texts`（3357）/
  `OnButtonClicked(int)`（5366））。
- **证据**：2026-10-22 实机。
- **备注**：`Choices.cs:122` 明确「焦点在输入框里时不抢数字键」（设置面板的生日/昵称输入框）。

### [x] A7. 限时选择 —— 结论：本作**没有**

- **结论**：反编译实查，`DialogueOption` 只有 `text` / `nextId` 两个属性，
  `DialogueNode` 只有 `duration`，两边都**没有倒计时字段** ⇒ 本作没有限时选择。
- **处理**：`[朗读] 限时选择时长` 与 `沉默按键` 保留为**保留字段**（避免配置迁移丢键），
  文档里明确写「本作没有效果」。
- **解锁**：不用再找计时逻辑；文档「限时」两处改成「本作没有」。

---

## B. 阻塞文档的（不解决就不能发布）

### [x] B1. 游戏原生占用的键

- **结论**：本作自带 9 条 `Ctrl+Alt+字母` 热键，见 A1 的清单（实机确认它们能用，
  导航模式也不抢它们）。
- **处理**：导航键避开这 9 条（选加号）；文档「游戏自带的键」一节已按此写。
- **备注**：`Tab` 与方向键在游戏里的原生占用仍未单独核对；
  `ClipboardPasteHook.LowLevelKeyboardProc` 抓什么键也**未查**。

### [ ] B2. `EventSystem.sendNavigationEvents` 是否常驻关

- **现状**：`Plugin.cs:182-197` 留了注释，**没有结论**。
- **需要什么证据**：`测试问卷.md` 第 8 节第 4 问的实机结果（点过按钮再按空格，会不会重复点）。
  判据：游戏自己有没有用 `SetSelectedGameObject` / 选中态。
- **由谁验证**：实机测试者（必答项）。
- **解锁**：`TickUiNav` 里那行决定；否则键盘用户会踩「空格把上次点的按钮又点一次」。

### [~] B3. 界面结构与对象名（`NameAlias` 是否成立）

- **已有证据**：**设置面板**已实机确认 8 个 tab（其他 / 快捷键 / 我 / 莉莉丝 / 语言 /
  声音 / 图像 / 控制），控件读法正常；`UiNav.Game.cs` 的 `NameAlias` 已按本作
  `SettingView.cs:260-265` 的字段名重写（`quitGameButton` 等），不再是上一作模板。
- **仍缺**：**标题菜单 / 存读档 / 画廊 / 聊天**的对象名与屏幕顺序没逐个核对。
- **由谁验证**：Lead / 测试者（导航过去听一遍 + 诊断日志）。
- **解锁**：这些面板的按钮名与顺序。

### [x] B4. `[界面导航] 退出确认对象名` 的默认值

- **结论**：默认值已从 `"TODO"` 改为 **`quitGameButton`**，来源反编译实查
  （`SettingView.cs:264` 的字段名，莉莉丝面板的退出按钮）。见 `Plugin.cs:225`。
- **仍缺**：实机按一下「退出游戏」看二次确认是否真的拦住了（未验证）。
- **解锁**：误按退出不会直接关游戏。

### [~] B5. 配音覆盖：哪些行有语音

- **已有结论**：本作台词**全程有配音**（用户与实机结论），因此默认不朗读台词；
  判定实现走 `DialogueManager.ResolveNodeSoundId`（3979）与
  `DialogueLineRepository.TryGetVoiceSoundId`（190）。
- **仍缺**：逐句比例审计（日志 `[Reader] stats` 的实际值没摘录）；
  若有极少数无配音行，是否需要单独开关也没定。
- **由谁验证**：Lead / 测试者。
- **解锁**：文档里「配音」措辞定稿；是否要把 `朗读无配音剧情` 默认打开。

### [x] B6. 手机 / 聊天 / 便签是不是主线必经

- **结论**：本作**没有手机**，但有聊天 / 备忘 / 待办类面板
  （`ChatListWindow`、`ChatListRow`、`MemoView`、`Cell_MemoItem`、`Cell_TodoItem`）。
- **处理**：这些面板靠**界面导航逐控件朗读**；「新消息自动播报」**未实现**，
  文档已写明（`[朗读] 朗读手机消息` 标保留字段）。
- **仍缺**：面板里逐项读法是否够用（没有逐面板核对）。

### [ ] B7. 存档路径与存档 / 设置 / 画廊界面结构

- **现状**：文档只写「一般是 `%USERPROFILE%\AppData\LocalLow\<公司>\<游戏>\`，本作未实查」。
- **需要什么证据**：实机找到存档目录（`测试问卷.md` 第 0 节要求填）。
- **由谁验证**：测试者。
- **解锁**：常见问题里的存档说明；卸载说明。

### [~] B8. 系统中文语音与后端链路的实机表现

- **已有证据**：实机**听到了导航朗读**，说明「补丁 → 某个后端 → 发声」这条链路是通的。
- **仍缺**：日志里 `语音后端就绪: ______` 的实际值（NVDA / Tolk / SAPI 到底是哪一个）
  没有摘录；系统中文语音与 SAPI 兜底也没单独试。
- **由谁验证**：测试者（发日志开头几行即可）。
- **解锁**：文档里后端顺序的措辞；「没声音」排查流程的可信度。

---

## C. 构建/发布层待验证

### [ ] C1. `mod\build.ps1` 端到端跑通

- **现状**：脚本常量已按本作修正（`$BepInExVersion = "6.0.0-be.788"`、默认
  `-GameDir` 指向本机安装路径、IL2CPP 包的探测文件改成 `BepInEx\core\BepInEx.Core.dll`），
  **但从未真正跑过一次**：取 BepInEx 要联网、本环境禁网。
- **需要什么证据**：在有网机器上跑
  `.\build.ps1 -GameDir "D:\Steam\steamapps\common\The NOexistenceN of Lilith"`，
  产物 `mod\package\` 能启动游戏并听到启动播报。
- **由谁验证**：Lead（或测试者）。
- **解锁**：发布流程可信。

### [ ] C2. BepInEx 6 `be` 版下载 URL 与资产名

- **现状**：`build.ps1:63` 拼的是
  `https://github.com/BepInEx/BepInEx/releases/download/v$ver/BepInEx_win_x64_$ver.zip`。
  这是照 BepInEx 5 的命名习惯写的，**6.0.0-be.788 的真实资产名没核过**。
- **需要什么证据**：访问该 release 页面确认资产文件名，或直接放一份到 `mod\dl\`
  让离线构建跳过下载（脚本已支持离线缓存）。
- **由谁验证**：Lead。
- **解锁**：构建可重复。

### [ ] C3. `mod\release.ps1` 的必需文件清单与 zip

- **现状**：已按 BepInEx 6 改成 `BepInEx\core\BepInEx.Core.dll`，并补了
  `baselib.dll`、`dotnet`、`BepInEx.Unity.IL2CPP.dll`、`Il2CppInterop.Runtime.dll`
  （依据实机目录实测；`release.ps1:46-62`）。
  `licenses\THIRD-PARTY-NOTICES.txt` 已写出（见 D2），这一项的 `[MISS]` 检查应该能过
  ——**但没有真的跑过 release.ps1**。
- **需要什么证据**：跑一次 `.\release.ps1`，zip 里的目录树与 `安装说明.md` 里画的一致。
- **由谁验证**：Lead（发布动作由 Lead 统一做）。
- **解锁**：发布包合规且可用。

### [~] C4. `probe\run_probe.ps1` 与探针链路

- **已有证据**：探针已实机跑通并产出 `probe\decomp\types_c.txt`（769 个类名）。
- **仍缺**：`probe\run_probe.ps1:12` 的 `$G = '<游戏目录>'` 占位符是否已替换、
  脚本本身能否一键重跑（`probe\` 不在文档层写作用域）。
- **由谁负责**：Lead / probe 负责人。

---

## D. 合规与仓库结构

### [ ] D1. 根 `LICENSE` / `NOTICE.md` / `CHANGELOG.md` 缺失

- **现状**：仓库根只有 README / 测试问卷 / 可行性验证 / docs / mod / tools / probe +
  `.gitignore` / `.gitattributes`。`new_game.py` 没生成这三个文件
  （平台层是 A11yFramework 的 MIT 代码，需要保留署名）。
- **需要什么证据**：无（不需要实机证据，需要**人决定**）——但属于发布前硬门槛。
- **由谁负责**：Lead 决定是否补；README 已标注「这是待办，不是已完成」。

### [~] D2. `mod\licenses\THIRD-PARTY-NOTICES.txt`

- **现状**：`licenses\` 里有 24 份许可证全文 + 本次写出的逐作第三方声明
  （来源、版本、源码获取途径、未修改声明）。`release.ps1:55` 的 `[MISS]` 检查应能过。
- **仍缺**：人复核声明内容与实际随包二进制是否一一对应（尤其 dotnet\ 与 baselib.dll）。

### [~] D3. `.gitignore` 的 `package/` 规则（**已修，等复核**）

- **原状**：`.gitignore:53` 是 `package/`，把 `mod\package\` 整个忽略掉，
  而用户文档源就在那里（`build.ps1:144` 的 `$keep` 会把两份 md 保留下来）。
  **后果：全新 clone 后这两份说明书不存在，build 也保不住它们。**
- **本次处理**：

  ```
  /mod/package/*
  !/mod/package/*.md
  ```

  构建产物仍不入库，两份 md 会进版本库。
- **需要人复核**：白名单是否符合预期。**未验证**：本环境不允许 `git init`，
  没有用 `git check-ignore` 实测过规则效果。

### [x] D4. `mod\build.ps1` 头部注释里的第三方清单

- **结论**：已补 `dotnet\`（.NET 6 mini-coreclr）与 `baselib.dll` 两样 BepInEx 6 才有的东西，
  与 `release.ps1` 的必需文件清单对齐。
- **证据**：`mod\build.ps1:12-21` 注释（本轮修改）。

---

## E. 文档同步遗留（本轮写作用域外，需下一轮处理）

- `测试问卷.md` 仍按「0.1.0 补丁点未接通、台词不会念」的旧口径写（第 0/3/4/5/6 节），
  与 README / 安装说明的「已实机验证」口径**不一致**。
- `无障碍可行性验证.md` 仍是 P0 静态调研口径（第 2/3/4 节大量「未确认」「待实测」），
  其中 A3/A6/A7/B1/B6 已有实机或反编译结论，需要回填。
- 两份文档都不在 Lead 本轮给的写作用域（README.md / mod\package\ 两份 md / 本文件），
  **本轮未改**，已回报 Lead。

---

## 用法

1. 每条解决后，把 `[ ]` 改 `[x]`，**在下面追加一行证据**（日志行 / 命令输出 / 问卷编号）。
   例：`- 证据：2026-10-22 实机，按 Ctrl+Alt+Plus 进入导航并逐项朗读设置面板`
2. **不允许**因为「看起来应该没问题」把 `[ ]` 改 `[x]`。
3. 本文档归零（或明确豁免剩余条）之前，**不要发「正式版」**，最多发「测试版」。
