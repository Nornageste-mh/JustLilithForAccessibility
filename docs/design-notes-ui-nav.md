# 控件朗读命名与降级 + 导航快捷键 · 代码级设计笔记

- 目标作品：《不/存在的莉莉丝》（`justlilith_a11y`，Steam AppID 4643090，Unity 2021.3.45f2c1 / IL2CPP，主程序 `Lilith.exe`）
- 调研对象：`Framework`（L1 平台层，冻结层）、`noexistence_a11y`（IL2CPP 唯一参考）、`belfry_a11y` / `transparenther_a11y` / `MeiKongForAccessibility`（L1 前身与钩子来源）、`justlilith_a11y` 现状骨架
- 纪律：本文全部为**静态源码结论**，没有任何一行在游戏里跑过；凡未上机确认的条目一律写「尚未实机复测」。所有结论带 `文件:行号`。本文不改任何源码，只提供设计。
- 反编译边界：只读仓库内开源实现与 Framework 源码，未反编译游戏程序集；本作类型清单来自**已有探针产物** `justlilith_a11y/probe/decomp/types_c.txt`。

---

## 0. 基线：L1 与逐作骨架当前长什么样

| 层 | 文件 | 状态 |
|---|---|---|
| L1 平台层 | `Framework/platform/UiNav.cs`、`KeyEdge.cs`、`Hotkey.cs`、`Repeat.cs`、`Speech.cs` | 冻结层（`Framework/README.md:177-179`：new_game.py 生成后不再改） |
| L2 契约 | `Framework/contract/A11yHost.cs` | 四个只增不改的钩子（`A11yHost.cs:194-215`） |
| 逐作拷贝 | `justlilith_a11y/mod/src/JustLilithA11y/UiNav.cs`（71.3KB，与平台层同构，差 1 字节） | 逐作拷贝，改 L1 需整份换代 |
| 逐作钩子 | `justlilith_a11y/mod/src/JustLilithA11y/UiNav.Game.cs`（104 行） | **模板占位**：别名表是上一作（钟塔）的，`InputFieldRoleName` 反射上一作 `NameInputManagerTMP`（`UiNav.Game.cs:1-38`、`61-104`） |
| 逐作配置 | `justlilith_a11y/mod/src/JustLilithA11y/Plugin.cs`（284 行） | 配置项已齐，`CfgMenuNav` 帮助文本写死 Tab（`Plugin.cs:122-129`） |
| 缺失件 | `Choices.cs`、`SpeakerNames.cs`、`UiVis.cs`、`Patches` 实现 | 全部没有（`Plugin.cs:193` 调用 `Choices.Update()`、`271-283` 是空 TODO），骨架当前编不过 |

调用链：`Plugin.Update`（`Plugin.cs:236-251`）→ `A11yHost.TickUiNav`（`Plugin.cs:190-197`）→ `UiNav.Update()`（`UiNav.cs:1450-1573`）→ `Announce`（`1177-1191`）→ `Describe`（`1073-1099`）→ `TextOf`（`962-1001`）。

---

## A.0 阻断级前置风险：L1 的 `as` / `is` 在 IL2CPP 下静默失败

**这条必须先修，否则 A/B/C/D 全部无意义。**

证据链：

1. `justlilith_a11y/mod/src/JustLilithA11y/JustLilithA11y.csproj` 引用 `BepInEx.Unity.IL2CPP`、`Il2CppInterop.Runtime`、`BepInEx/interop/Assembly-CSharp.dll` → 本作是 **IL2CPP 路线**。
2. `Framework/platform/UiNav.cs` 全文**没有任何 `TryCast` / `As<T>` 辅助函数**（对 `Framework/platform/*.cs` 检索 `TryCast|static .* As<|Cast<` 零命中），并且有 14 处裸 `as`/`is`：
   - `UiNav.cs:321` `if (!(s.transform is RectTransform)) continue;`（**Scan 的第一道过滤器**）
   - `UiNav.cs:466`、`509`、`606`、`671`（`transform as RectTransform`，排序/可见性/相机）
   - `UiNav.cs:872`、`874`（`c as TextMeshProUGUI` / `c as Text`，**文本读取的唯一收口 `TextOn`**）
   - `UiNav.cs:1077`、`1078`、`1079`（`s as Toggle/Slider/TMP_InputField`）
   - `UiNav.cs:1094` `else if (s is Button)`、`1381` `Button b = s as Button;`
   - `UiNav.cs:1353`、`1365`、`1374`、`1436`（Activate/Adjust）
3. `noexistence_a11y/mod/src/NoExistenceA11y/UiNav.cs:97-110` 用带注释的 `As<T>` 明确写了为什么不能这么写：

   > IL2CPP 下**不能**用 `as`/`is` 做 Unity 组件类型判断。Il2CppInterop 是按「方法的声明返回类型」造代理对象的，所以 `s.transform` 拿到的是一个 Transform 代理，哪怕底层其实是 RectTransform，`s.transform is RectTransform` 也永远是 false，`as` 永远是 null —— 而且是静默失败，不报错，表现就是「界面上明明有控件，一个都扫不到」。必须走 TryCast，它按原生类做兼容性判断。

   参考作对照：`noexistence` 走 `As<T>()`；`belfry`/`transparenther` 是 Mono 路线（`UiNav` 里 `Input.GetKeyDown` 裸用，`README.md:323-346` 也把它们与 L1 区分），所以 Framework 的 `as/is` 写法**只在 Mono 下被验证过**。

**可推论的后果（尚未实机复测，但按参考作注释几乎是必然）**：

- `UiNav.cs:321` 恒 false → `Scan`（`287-405`）一个控件都留不下 → `Enter`（`180-199`）念「当前界面上没有可操作的项目。」，导航模式直接不可用。
- 若 321 侥幸通过（例如 `is` 对代理的行为与参考作描述的版本不同），则 `TextOn`（`870-877`）恒取不到文本 → 所有控件退化到 `AncestorNameOf`（`1004-1018`）的**对象名**；`Describe` 的 `s is Button`（`1094`）恒 false → 全部落到 `1095` 的 `s.GetType().Name`，而 IL2CPP 代理的 `GetType().Name` 只会是 **`Selectable`**（声明类型），连真实类型名都念不出来；`Button` 激活分支（`1381-1393`）恒不触发 → 全部走 `ExecuteEvents.submitHandler`（`1395-1402`），开关/滑条的值切换逻辑（`1365-1379`）全部失效。
- `s as TMP_InputField`（`1079`/`1353`）恒 null → 输入框被当成普通控件，"已进入输入框，直接打字"（`1353-1363`）永不出现。

**建议（交给 Lead 落地，属于 L1 修复）**：

1. 在 L1 加一个 `Cast.cs`（或放进 `UiNav.cs` 私有区）：
   ```csharp
   // 与 noexistence UiNav.cs:105-110 同构
   private static T As<T>(UnityEngine.Object o) where T : Il2CppObjectBase
   {
       if (o == null) return null;
       try { return ((Il2CppObjectBase)o).TryCast<T>(); } catch { return null; }
   }
   private static bool Is<T>(UnityEngine.Object o) where T : Il2CppObjectBase
       => As<T>(o) != null;
   ```
   注意：`Transform` 代理的 `TryCast<RectTransform>()` 是否成功，必须实机确认（这是全部排序/可见性判断的基础）。
2. 把上列 14 处替换为 `As<T>` / `Is<T>`；`GetType().Name`（`1095`、`1340`）改用「真实原生类名」（`TryCast` 目标的 `typeof(T).Name` 或经 IL2CPP 的 `il2cpp_class_get_name`），否则念出来的是 `Selectable`。
3. 平台层是按 Mono 验证的，因此「L1 冻结」这条对**本作不成立**：本作要么给 L1 打 IL2CPP shim（推荐，因为 L1 未来还要给更多 IL2CPP 新作用），要么整份换 `noexistence` 版 `UiNav`（它有完整的 `As<T>` 链，但命名链没有 `GameLabelOf` 钩子，`UiNav.cs:1204-1228`）。**不要**在逐作拷贝 `justlilith_a11y/.../UiNav.cs` 上手改两行，否则与平台层漂移，下一作生成即错。

> 结论：本文后面所有"退化"分析都建立在「控件能被扫到、类型判断能成功」的前提上；若 A.0 不修，本作听到的不是"原始标识"，而是"没有可操作的项目"或"Selectable"。这一点优先级高于 B/C/D/E。

---

## A. 控件朗读名称解析的完整链路

### A.1 决策树（全部在 `UiNav.cs` 同文件）

```
Announce(prefix)                                  1177-1191
  └─ Describe(Selectable)                          1073-1099
       ├─ 文本名：输入框走 InputFieldLabel         1037-1063
       │            其余走 TextOf                  962-1001
       │   TextOf:
       │     1) OwnTextOf(s)                        739-765   ← 控件自己子树里的 TMP（最多 3 段，"，"拼接）
       │     2) A11yHost.GameLabelOf(s)             967-980   ← 逐作钩子（放在"同行兄弟文字"之前，拥有覆盖权）
       │     3) row = AliasAncestorOf(s)            926-941   ← 上溯 ≤4 层查 NameAlias
       │     4) near = RowTextOf(s)                 896-912   ← 上溯 ≤2 层找"同行/相邻"文本
       │        若 row≠near：AliasOnSelf ? near+"，"+row : row+"，"+near     986-993
       │     5) row 非空则返回 row                  996-998
       │     6) 否则 AncestorNameOf(s)              1004-1018 ← 上溯 ≤4 层第一个非样板名，全败→gameObject.name
       └─ 类型/值后缀                              1081-1097
             TMP_InputField → "，输入框"，当前内容/当前为空，最多 N 个字   1083-1091
             Toggle         → "，开关，开/关"                              1092
             Slider         → "，滑条，" + SliderValueText                  1093
             Button         → "，按钮"                                    1094
             其它           → "，" + s.GetType().Name                      1095  ★退化
             !interactable  → "，不可用"                                  1097
组播报：GroupName(root)  1105-1118  ← A11yHost.GameGroupName 钩子，未填则 root.name（★退化）
```

辅助函数与阈值（同一文件）：

| 函数/常量 | 行号 | 要点 |
|---|---|---|
| `OwnTextOf` | 739-765 | `GetComponentsInChildren<TextMeshProUGUI>(true)`，最多 3 段去重 |
| `Norm` / `Noise` | `739` / `883-893` | `A11yHost.GameTextIsNoise` 返回 true → 返回空串（唯一收口） |
| `IsBoilerplateName` | 772-786 | `button/controlbutton/toggle/slider/text/image/…/fill/handle/area` 视为样板名 |
| `HasOtherSelectable` | 810-826 | 容器里除自己这支外还有别的 `Selectable` → 这层不是"一行" |
| `FirstTextOutside(parent, exclude)` | 828-867 | `HasOtherSelectable` 成立直接返回 `""`；只接受 `activeInHierarchy`；上溯途中遇 `Selectable` 跳过 |
| `TextOn` | 870-877 | TMP → `Noise(Norm(tmp.text))`；legacy `Text` → 同上 |
| `AliasOnSelf` / `AliasAncestorOf` | 915-923 / 926-941 | 名字表在逐作 `UiNav.Game.cs` |
| `PlaceholderText` | 1022-1030 | TMP_InputField 的占位提示 |
| `SliderValueText` | 1066-1071 | 0–1 念百分比，否则 `N / M` |
| `SceneSettleSeconds` / `QuitConfirmSeconds` | `142` / `1279` | 1.5 秒；8 秒（`README.md:268-285` 记录 8f） |

### A.2 四个"原始标识"出口（逐条）

| # | 出口 | 位置 | 触发条件 | 实际念出的东西 |
|---|---|---|---|---|
| 1 | `AncestorNameOf` 兜底 | `1004-1018`，返回在 `1017` | 自己子树无文本 **且** `GameLabelOf` 未填或返回空 **且** 别名表未命中 **且** `RowTextOf` 为空（或 `row==near`）**且** 上溯 4 层全是样板名 | `s.gameObject.name` |
| 2 | `Describe` 类型名 | `1095` | 文本名已取到，但控件不是 `TMP_InputField/Toggle/Slider/Button`（含 IL2CPP 下 `is Button` 恒 false 的情形，见 A.0） | `s.GetType().Name` |
| 3 | `GroupName` 兜底 | `1105-1118`，返回在 `1117` | `A11yHost.GameGroupName` 未填（本作 `UiNav.Game.cs` 占位里**确实没有**这个钩子）或返回空 | `root.name`（Canvas 顶层对象名），出现在每次进组播报里 |
| 4 | 文本本身是占位英文 | `883-893` 未拦下时 | 游戏 TMP 里写着 `New Text` / `Button` / `音效名称` 之类预制体占位 | 原样念出占位词（第四作把这句列为"比对象名更糟：玩家会以为那个东西真的叫 New Text"，`MeiKongA11y/UiNav.Game.cs:13-30`） |

补充出口（输入框专用）：`InputFieldLabel` 最后也是 `AncestorNameOf`（`1062`），所以输入框同样会念对象名。

**关键结构性事实**：`TextOf` 永远不会返回空串 —— 走到最后一定 `return AncestorNameOf(s)`（`1000`），而 `AncestorNameOf` 最后 `return s.gameObject.name`（`1017`）。也就是说**退化是静默的**：`Describe`/`Announce` 拿到的字符串看起来"有内容"，没有任何标志能告诉调用方"这是对象名"。这正是需要 C 节 `LabelQuality` 的原因；也是"仅凭字符串判断是否退化"不可靠的原因（对象名可能与真实标签同名）。

### A.3 钩子的实际实现与局限（本作要照抄的样板）

`A11yHost.cs` 契约（`194-215`）：

```csharp
internal static Func<string, bool>      GameTextIsNoise;   // 194-200：这段文字是预制体占位，别念
internal static Func<Selectable, string> GameLabelOf;      // 202-206：逐作标签解析；返回空串 = 我不知道，照旧降级
internal static Func<Transform, string>  GameGroupName;    // 208-209：组的中文名
internal static Func<Transform, int>     GameGroupPriority; // 211-215：小者排前，默认 100
```

平台调用点：噪声在 `Noise`（`883-893`，所有文本读取的唯一收口，`Framework/CHANGELOG.md:462` 明确这一点）；`GameLabelOf` 在 `TextOf:967-980`（"同行兄弟文字"之前）与 `InputFieldLabel:1042-1052`（"占位提示"之前）；`GameGroupName` 在 `GroupName:1109-1114`。

唯一完整实现见 `MeiKongForAccessibility/mod/src/MeiKongA11y/UiNav.Game.cs`，可迁移的三条经验：

1. **先噪声、后钩子**：`PlaceholderTexts` 是 `"New Text","Button","Text","Label","Item","Slider","Toggle","音效名称","000","00","0"`（`40-45`），`GameTextIsNoise` 去 trim 后查表（`47-51`）。占位文本清成空串后，`OwnTextOf` 才为空，`GameLabelOf`（`58-165`）才轮得到 —— 顺序错了钩子永远不会被调用。
2. **钩子失败必须返回空串**：`58-165` 整体 `try`，`165` `catch { return ""; }`。返回空串平台继续降级；抛异常会把游戏 Update 打断。
3. **路径决定语义，名字表只放平表能表达的**：`GameLabelOf` 里先 `PathOfLabel(s.transform)`（`68-69`，定义在 `204-214`：上溯最多 8 层、`/` 前缀插到最前），再按路径分支；同名不同义靠父节点名破解（`CommonButton` 在番茄钟是停止/暂停/下一个，在互动区是"和诗萌聊聊"，`13-30`）；序号靠 `GetSiblingIndex()+1`（`/QuickButtons/`、`/SFXSlot/`）；数值角色靠**引用比对游戏自己序列化的字段**（`PomodoroFields`，`291-300`；注释 `273-283` 解释"槽位(2) 是专注时长"这种靠猜不可靠）；`NameAlias` 只留两条（`263-268`，注释 `260-261`：平台在"同行兄弟文字"之后查它，只放平表能表达的）。

**局限（本作必须注意）**：

- `GameLabelOf` 只在 `OwnTextOf` 为空时被问（`967-980`）。**控件子树里只要有任何 TMP 文本（哪怕占位、哪怕错误），钩子根本不会被调用** —— 所以必须先靠 `GameTextIsNoise` 清占位，否则钩子形同虚设。这也是"有文本但读错"这一类（同行是曲名/进度值、按钮里写着"确定"但实际是关闭）无法被钩子纠正的原因。
- `GameLabelOf` 看不到"这一项在整屏中的语义序号"以外的上下文（例如"这是第 3 个存档槽"必须自己用父节点/兄弟序号算）。
- `NameAlias` 只按 `gameObject.name` 精确匹配（`915-923`），对同名多义无能为力；且本作 `UiNav.Game.cs:23-38` 还是上一作的表（`Start/Load/Gallery/Options/Exit/Yes/No/Confirm/Cancel`），**暂时一条都不能信**（铁律：别名每条都要 probe 依据，`README.md:197-201`）。
- `GroupName` 缺失 → 每次进组都念英文 Canvas 顶层对象名（`1117`），这是本作**当前最刺耳**的退化，补一个 `GameGroupName` 就能消掉大半。

### A.4 哪些 UI 结构会命中退化分支（逐条）

| # | 结构 | 为什么退化 | 证据 |
|---|---|---|---|
| 1 | `Text` 是控件的**兄弟**而不是子节点（uGUI 设置面板的典型布局） | 第 1 层 `OwnTextOf` 取不到 → 靠第 4 层 `RowTextOf`（上溯 ≤2 层）补救；超过 2 层或中间夹了 `Selectable` 就断链 | `UiNav.cs:946-960`（实测结构 TextSpeed/ScreenPixel1080）、`896-912`、`828-867` |
| 2 | 容器里还有别的 `Selectable`（列表/网格的每一项） | `HasOtherSelectable` 成立 → `FirstTextOutside` 直接返回 `""`，行内标签永远取不到 | `810-826`、`828-867`；反向故障见 `noexistence UiNav.cs:1059-1067`（标题被当每个按钮标签，整屏全念「通讯」） |
| 3 | 文本在子物体且**超过 3 段** | `OwnTextOf` 只取前 3 段，顺序是 `GetComponentsInChildren` 顺序而非视觉顺序 → 漏词/颠倒 | `739-765` |
| 4 | `LocalizeStringEvent` / 本地化表驱动文本 | 文本是运行时注入的：扫描/播报若早于注入读到空/占位 → 退化；`Announce` 每次重新 `Describe` 所以文本后来更新能自动生效，但"内容变了"的判定依赖 rescan（见 D.4） | 本作类型 `AutoBindLocalizeStringEvent`（`probe/decomp/types_c.txt:215`）、`Localization/LocalizedText/LocalizedSprite`（`217-223`）、`SettingLocalizedText`（`399` 一带） |
| 5 | 标签节点 `!activeInHierarchy` | `FirstTextOutside` 只接受激活节点（`828-867`），未激活的 `Text`/`TMP` 取不到 → 退化 | 同上 |
| 6 | 自定义渲染 / Spine / 非 uGUI 命中区 | 没有 TMP 文本可读，只能靠对象名或钩子 | 本作类型 `LilithSkeletonFacePatch`、`CharacterHitArea`、`TransparentWindowNew`、`ActiveWindowCollider`（`types_c.txt:89-136`）—— 桌宠交互与可点击立绘有高风险 |
| 7 | 纯图片/换图按钮 | 无文本，靠 `NameAlias`/`GameLabelOf` | `ButtonSwapSprite`/`ButtonPressedSwapSprite`/`ButtonToggle`（`types_c.txt:416-420`）、`ButtonSound`/`ButtonSwitch`（`144-145`） |
| 8 | 预制体占位文本 | 见 A.2 出口 4；必须 `GameTextIsNoise` 拦 | `MeiKongA11y/UiNav.Game.cs:40-51` |
| 9 | `Text`（legacy uGUI）而不是 TMP | `OwnTextOf` 只读 TMP（`739-765`），legacy `Text` 只在 `TextOn`（`870-877`，即 `FirstTextOutside` 链）里被读 → 子节点里的 legacy Text 直接被忽略 | 同上 |
| 10 | 输入框 | 走 `InputFieldLabel`（`1037-1063`），不填 `InputFieldRoleName` 时依次降级到占位提示 → 别名 → 同行 → 对象名；**不能**走 `TextOf`，否则读成「已输入内容，输入框，当前内容 已输入内容」（`1032-1036` 注释） | 本作 `SettingInputFieldItem`（`types_c.txt:402`）、`SettingHotkeyRow`（`394`） |
| 11 | 文本清洗后为空 | `Noise` 已把整段判成噪声 → 等于无文本 → 退化 | `883-893`；`TextProc.ToSpeech`（`noexistence TextProc.cs:46-83`）之后只剩标点时 `IsTrivial` 应判琐碎（`101-110`） |
| 12 | 层级对象名本身就是英文/编号 | 前 5 层全空时 `AncestorNameOf` 直接念它 | `1004-1018`、`1017` |

关于任务里点名的 `"Function Name"` / `"Object Name"`：**全工作区 652 个 `.cs/.md` 检索零命中** → 插件侧不会生成这两个字符串；若实机听到，来源只能是 ①游戏自己的 TMP 文本/本地化模板，或 ②热键重绑定行 `UI.TraySettingNew.SpecialItems.SettingHotkeyRow`（`types_c.txt:394`）的显示模板占位。**必须 probe 取证**（见第 9 节 V3），不要先写死进噪声表（会误伤真实内容）。

---

## B. 导航模式开关（导航快捷键）

### B.1 参考作实现对照

| 作品 | 位置 | 写法 | 备注 |
|---|---|---|---|
| Framework L1 | `UiNav.cs:1498` | `if (KeyEdge.Pressed(KeyCode.Tab)) { Diag(...); Toggle(); return; }` | **硬编码 Tab**，不可配置，无 `try/catch`；`KeyEdge` 只为防合成输入重复触发（`1522-1524` 注释、`KeyEdge.cs:28-47`） |
| justlilith 拷贝 | 同文件 `1498`（与平台层同构，差 1 字节） | 同上 | 逐作拷贝，需随 L1 换代 |
| belfry | `belfry_a11y/.../UiNav.cs:1460` | `KeyEdge.Pressed(KeyCode.Tab)` + `Diag` | 与 L1 同 |
| transparenther | `transparenther_a11y/.../UiNav.cs:1417` | `Input.GetKeyDown(KeyCode.Tab)`（裸用） | 无 `KeyEdge`、无 `Diag` |
| noexistence（IL2CPP） | `noexistence_a11y/.../UiNav.cs:1681` | `Input.GetKeyDown(KeyCode.Tab)`（裸用） | 同上 |

进入/退出语义已成型，不需要重新发明：

- `Toggle()`：`174-178`；`Enter()`：`180-199`（先 `SceneStable` 门禁 `274-283`，再 `Scan`，空则 `Repeat.Say("当前界面上没有可操作的项目。")`，否则 `_active=true, _index=0, AnnounceGroup(true)`）。
- `ExitInternal(bool announce)`：`201-216`（清 `Groups/_index/_pendingRescanFrame/_announcedItem/ClearQuitConfirm/ReleaseSelection`；`announce` 时 `Repeat.Say("已退出导航模式。", true)`）。
- 场景切换防护：`1452-1465`（检测到换场景 → `KeyEdge.Reset()`、`_active` 时 `ExitInternal(false)`、`return`）。
- 开关时的朗读文案是现成的：进组播报 `GroupPrefix`（`1148-1175`）单组时是「导航模式，<组名>，共 N 项。」；多组是「第 N / 共 M 组：<组名>，N 项。」+ 最多 6 组总览 +「用 PageUp / PageDown 换组。」；退出是「已退出导航模式。」（`213` 附近）。
  → **所以"导航模式开/关"的朗读不需要新增文案，只要 `GameGroupName` 有中文名**（否则念成「导航模式，Canvas，共 N 项。」，见 A.2 出口 3）。

### B.2 `Input.GetKeyDown` 的异常风险与参考作里的 `try/catch`

Unity 事实（书面依据）：工程若把 Player Settings → Active Input Handling 设成 "Input System Package (New)" / "Both"，旧 `UnityEngine.Input` 的读取在运行时会抛 `InvalidOperationException`（"You are trying to read Input using the UnityEngine.Input class, but you have switched active Input handling to Input System package"）。仓库里**没有**这条注释的书面记录（全仓检索 `Input System|InputSystem|InvalidOperationException` 只命中 `aliya_a11y/decompiled/cs/JsonData.cs` 的无关异常），但所有作者都做了防御，位置如下：

| 位置 | 包裹范围 | 注释/说明 |
|---|---|---|
| `Framework/platform/Hotkey.cs:122-144` | `Down(Spec)` **整体** `try/catch`，`126` 是唯一 `Input.GetKeyDown` 调用 | 注释 `141`：「输入系统不可用（编辑器/无头）时当作没按，绝不把异常抛进游戏主循环」 |
| `noexistence UiNav.cs:1417-1423`（BlockGameAdvance 内） | `try { if (!Input.GetKeyDown(Return/KeypadEnter/Space)) return false; } catch { return false; }` | `1393-1407` 注释讲的是「游戏 Update 在 `Input.GetKeyDown` 成立后紧接调 `DialogueButtonClicked()`，两个 Update 谁先跑不确定」→ 两种判据都要有；异常兜底是顺带的 |
| `noexistence UiNav.cs:1681`（Tab） | **没有** `try/catch` | 裸用 |
| `noexistence Choices.cs:218-241` | 数字键直选整段 `try { … } catch { return; }`（`222-235`） | 逐作层对 `Input.GetKeyDown` 的统一防御姿势 |
| `noexistence Plugin.cs` 的 `Speech.Speak` 等 | 各处 `try/catch` | — |
| `MeiKong Plugin.cs:250-290` | `Update()` **整体** `try/catch`（`289` 记 `A11yHost.Diag`）；`297-303` 的 `KeyDown(cfg)` 自身**无** `try/catch`，靠外层 | — |
| `transparenther Plugin.cs:625-634` | 热键前先判 `EventSystem.current.currentSelectedGameObject` 上的 `TMP_InputField`，整段 `try/catch` | 「在输入框里打字时不抢按键」；`636-656` 数字键 `Input.GetKeyDown` 裸用，也在 try 内 |
| justlilith | `Plugin.cs:236-251` `Update` 有 `try/catch`（只记一次异常） | 但 `UiNav.Update` 内部 `1498`/`1525`/`1557-1572` 全部裸 `KeyEdge.Pressed` → `KeyEdge.Pressed` 内部裸 `Input.GetKey`/`GetKeyDown`（`KeyEdge.cs:53-70`），异常会一路冒到 `Plugin.Update` 的 catch，表现为**热键整段失效并刷日志** |

结论：本作类型清单里没有任何 `UnityEngine.InputSystem.*`（`types_c.txt` 检索零命中），大概率仍是旧 Input；但**不能赌**。设计里必须保证：①平台层按键读取全部经 `KeyEdge`，`KeyEdge` 自己加上 `try/catch` 降级为 false（与 `Hotkey.cs:141` 同思路）；②逐作热键检测放在 `Plugin.Update` 的 `try` 内。

### B.3 CTRL+ALT 组合键设计

**为什么不是 Tab**：Tab 在本作里几乎肯定被游戏占用（`SettingHotkeyRow` `types_c.txt:394` 说明游戏有可重绑定热键；`README.md:197-201` 铁律「不要填游戏原生占用的键」，`Plugin.cs:107-109` 也写了同样的警告）。旧 Input 只读不消费，Tab 按下游戏也会响应 → 导航与游戏抢键。而且 NVDA/读屏用户习惯用 Tab 在控件间移动，冲突更明显。

**键位候选与系统级冲突**（选键时必须避开）：

| 组合 | 冲突 |
|---|---|
| `Ctrl+Alt+N` | NVDA 主菜单 |
| `Alt+Tab` / `Alt+Shift+Tab` | 任务切换 |
| `Ctrl+Alt+Tab` | 任务切换常驻 |
| `Ctrl+Alt+I / M / F / L / S / Space` | Windows 放大镜 |
| `Ctrl+Alt+Del` | 安全注意序列（不可用） |
| `Ctrl+Shift+…` / `Ctrl+Space` | 中文输入法切换/中英切换 |
| `Alt+Shift` | 输入法语言切换 |

**建议默认 `Ctrl+Alt+G`**（不在上表、不是读屏热键、不是常用游戏键），并在配置里允许改；同时保留 `Tab` 作为可选次键（默认关）。

**检测位置**：`Plugin.Update`（`Plugin.cs:236-251`），在 `A11yHost.TickUiNav` 之前；**不要**做 PlayerLoop 注入，理由：①PlayerLoop 阶段顺序不可控，且与游戏 `Update` 的相对次序才是真正影响"抢键"的因素；②逐件层已有 `Plugin.Update` 这个稳定入口；③导航开关只需每帧一次边沿判断，早一帧晚一帧无影响。

**代码结构（逐作层，最小改动）**：

```csharp
// Plugin.cs —— 配置
CfgNavKey = Config.Bind("界面导航", "导航模式开关键", "Ctrl+Alt+G",
    "进入 / 退出导航模式的组合键。写法见 Hotkey.cs：F9 / Alt+Shift+F9 / Ctrl+Alt+G / 左右修饰键分别写。\n" +
    "留空则只能用 Tab（见下一项）。\n" +
    "警告：不要填游戏原生占用的键（见 README 的「游戏原生占用的键」）。");
CfgNavKeyAllowTab = Config.Bind("界面导航", "Tab 也能切换导航模式", false,
    "游戏可能自己用 Tab 切输入框/暂停菜单；开着会抢键。默认关。");

// Plugin.cs —— 边沿闩锁（Hotkey.Down 只回答"是否按住"，没有边沿语义）
private static bool _navKeyLatched;

private static bool NavKeyPressed()
{
    bool down = false;
    if (CfgNavKey != null && !string.IsNullOrEmpty(CfgNavKey.Value))
        down = Hotkey.Down(CfgNavKey.Value);          // 内部有 try/catch，见 Hotkey.cs:122-144
    if (!down && CfgNavKeyAllowTab != null && CfgNavKeyAllowTab.Value)
        down = KeyEdge.Pressed(KeyCode.Tab);           // 与平台同一个键：二选一，不叠加
    if (!down) { _navKeyLatched = false; return false; } // 抬起 → 重新武装
    if (_navKeyLatched) return false;                    // 长按/OS 自动重复不再触发
    _navKeyLatched = true;
    return true;
}
```

**接入 L1 的两个方案**（必须由 Lead 决定，二者都要改 L1，因为 `Toggle` 是平台私有入口）：

- **方案 A（推荐）**：`A11yHost` 增加一个只增不改的钩子 `internal static Func<bool> NavTogglePressed;`（放在 `211-215` 之后，默认 `null` → 前三作行为逐字节一致，与 `A11yHost.cs:180-192` 的"钩子只增不改"原则一致）；`UiNav.cs:1498` 改为：
  ```csharp
  bool toggleRequested = A11yHost.NavTogglePressed != null
      ? A11yHost.NavTogglePressed()
      : KeyEdge.Pressed(KeyCode.Tab);
  if (toggleRequested) { Diag(...); Toggle(); return; }
  ```
  逐作 `Plugin.Awake`（`169-178`）填 `A11yHost.NavTogglePressed = NavKeyPressed;`。
- **方案 B（不改 `1498`，只加公开入口）**：L1 增加 `public static void ToggleFromHost()`（一行转发 `Toggle()`），逐作在 `Plugin.Update` 里检测热键后调用它。缺点：与 `UiNav.Update` 里的场景切换防护（`1452-1465`）重复执行顺序问题，且开关时机可能早于 scene-stable 门禁判断。
- **方案 C（完全不改 L1）不可行**：逐作层无法模拟一次 `Tab` 按下（不可注入 `Input`），只能让玩家真按 Tab。

> 无论哪个方案，`CfgNavKey` 必须是 `string` + `Hotkey.Parse` 校验（`Hotkey.cs:57-106`）：拒绝纯数字（`87`，`Enum.TryParse("0")` 会成功但得 `KeyCode.None`，`G3`），往返校验（`95-99`），配置写错时返回人话原因。写错不能静默禁用。

### B.4 与游戏自身快捷键 / 输入法 / 重复朗读的冲突处理

1. **组合键严格要求修饰键**：`Hotkey.Down(Spec)` 的语义是「写了哪个修饰键就要求哪个按住，没写的必须没按住」（`Hotkey.cs:132-136`），所以 `Ctrl+Alt+G` 不会被没按修饰键的 `G` 触发，也不会抢游戏自己的 `G`/`F9`/`Alt+F9`。这是选 `Hotkey.cs` 而不是 `KeyEdge` 做开关的核心理由。
2. **输入框聚焦时忽略开关热键**：进入输入框时平台已经 `ExitInternal(false)`（`1353-1363`），若此时再按开关会重新 `Enter` 并抢走焦点 → 在 `NavKeyPressed()` 最前面加：
   ```csharp
   var es = EventSystem.current;
   if (es != null && es.currentSelectedGameObject != null &&
       es.currentSelectedGameObject.GetComponent<TMP_InputField>() != null) return false;
   ```
   先例：`transparenther Plugin.cs:625-634`（注意 IL2CPP 下应写 `As<TMP_InputField>()`，见 A.0）。
3. **输入法**：Ctrl+Alt 组合在部分中文 IME 下会被拦截/组合；这不影响安全（最坏是没反应），但要在帮助文本里给替代键（F 系列）。另外 `Alt` 单独按下会激活窗口菜单栏（Windows），已按 `Hotkey.cs` 语义要求"未写的修饰键必须没按住"规避。
4. **重复朗读**：`KeyEdge` 的闩锁（`KeyEdge.cs:53-70`）解决"合成输入一次按下报两次 `GetKeyDown`"（`28-47` 注释记录实测：发一次 Tab 出现"进入→退出→进入"三次迁移）；`Hotkey` 没有闩锁，所以逐作层自己加（B.3 的 `_navKeyLatched`）。**不要**用 `Time.frameCount` 做冷却：长按会把冷却变成连续开关。
5. **与 `BlockGameAdvance` 的关系**：开关只改 `_active`，不写 `_submitHandledFrame`（`1233-1252`），所以不会影响游戏对回车/空格的判读；但**切换瞬间**若同帧还有 `KeyEdge.Pressed(Return)`，`1525-1553` 的 `return` 会在 `1498` 之后才跑（`1498` 已 `return`），即同帧只处理开关，不会误激活。这是现有顺序的正确性，保留。
6. **`sendNavigationEvents`**：与本开关无关，但同属"导航模式"的必判定项 —— 本作要不要常驻关必须单独判定（`Plugin.cs:180-189` 注释；`README.md:348-358` 记录 belfry 不能关、transparenther 必须关，两作结论相反）。判据：游戏自己有没有用 `SetSelectedGameObject`/依赖选中态（`SelectByUs` 在 `261-272` 会改选中态）。

---

## C. 分层降级方案（绑定文本 → 行内/相邻 → 语义描述 → 原始标识）

### C.1 分层定义

| 层 | 名称 | 内容 | 质量值 |
|---|---|---|---|
| L0 | 绑定文本 | `OwnTextOf` 非空（子树 TMP，且过 `Noise`），或 `GameLabelOf` 返回非空（逐作给出真正的名字） | `Bound` |
| L1 | 行内/相邻标签 | `AliasOnSelf`/`AliasAncestorOf` 命中（人工映射），或 `RowTextOf` 拿到同一行/相邻文本 | `Nearby` |
| L2 | 语义描述 | 无任何可读文本，但能说出「组名 + 控件类型 + 第 N 项/共 M 项 + 相邻项」 | `Semantic` |
| L3 | 原始标识 | `AncestorNameOf` 的 `gameObject.name`、`Describe:1095` 的 `GetType().Name`、`GroupName:1117` 的 `root.name` | `Raw` |

设计原则：**L2 优先于 L3**。现在平台层直接从 L1 跳到 L3（`1000` → `AncestorNameOf`），缺的正是 L2；补上 L2 后，L3 只在"连类型/位置都算不出来"时兜底（几乎只剩"逐作明确要求念对象名排查"的场景）。

### C.2 数据结构与签名（L1 内部）

```csharp
internal enum LabelQuality { Bound = 0, Nearby = 1, Semantic = 2, Raw = 3 }

internal struct LabelResult
{
    internal string Text;        // 给朗读用的文本（Raw 时是对象名/类型名）
    internal LabelQuality Quality;
    internal string Source;      // "own" / "gamelabel" / "alias" / "row" / "ancestor" / "typename" / "group"
}

// 新主函数：保留原有 TextOf 语义，额外给出质量与来源
internal static LabelResult ResolveLabel(Selectable s);

// 兼容包装：前三作/现有调用点零改动
internal static string TextOf(Selectable s) { return ResolveLabel(s).Text; }
```

改造点（都在 `UiNav.cs` 同一文件，行为中性重构）：

- `TextOf`（`962-1001`）内部改写成 `ResolveLabel`，逐分支打 `Source` 与 `Quality`：
  - `964 own = OwnTextOf(s)` 非空 → `Bound/"own"`；
  - `967-980 GameLabelOf` 非空 → `Bound/"gamelabel"`；
  - `982 AliasAncestorOf` / `915-923 AliasOnSelf` 命中 → `Nearby/"alias"`；
  - `983 RowTextOf` 非空 → `Nearby/"row"`；
  - 都空 → **不要**立刻 `return AncestorNameOf(s)`；返回 `Text=""`、`Quality=Semantic`，由调用方（`Describe`/`Announce`）拼语义描述；
  - 仅当调用方明确要求"要一个字符串"（诊断日志、probe dump）且 `CfgSpeakRawName=false` 时，才用 `AncestorNameOf` 并标 `Raw/"ancestor"`。
- `Describe`（`1073-1099`）：`1081` 改为按 `Quality` 分支：
  - `Bound`/`Nearby` → 照旧拼 `Text`；
  - `Semantic` → 先 `SemanticText(s)` 再拼类型/状态后缀（后缀逻辑 `1081-1097` 不动）；
  - `Raw` → 当 `CfgSpeakRawName=false` 时只念 `SemanticText(s)`，并（可选）加一句降级提示（见 D.3）。
  同时把 `1095` 的 `s.GetType().Name` 换成真实类型名表（`As<Button>` 等，见 A.0），并把未知类型映射为「项」而不是英文类型名。
- `AncestorNameOf`（`1004-1018`）保留，但只在 `Raw` 分支被调用；`1017` 的兜底也保留（诊断/probe 需要）。
- `GroupName`（`1105-1118`）：`1117` 的 `root.name` 也标 `Raw`；播报时可只念"面板"而不是对象名（由 `CfgSpeakRawName` 决定）。

### C.3 `SemanticText(Selectable)` 伪代码

```csharp
// 需要的数据：Groups（Scan 结果）、_groupIndex、_index。
// Scan 在 287-405 建 Groups，496-528 SortWithin，369-375 组排序；
// Announce 里已有 _index 与 Items（1177-1191），需要补 GroupStart 与组内下标。
private static string SemanticText(Selectable s)
{
    int gi = GroupIndexOf(s);                 // 由 Scan 时建立 selectable→组 的映射
    var items = Groups[gi].Items;
    int local = items.IndexOf(s);             // 组内 0 基下标
    int total = items.Count;
    var sb = new StringBuilder();
    string grp = GroupName(Groups[gi].Root);  // 1105-1118；CfgSpeakRawName=false 时 null→"面板"
    if (!string.IsNullOrEmpty(grp)) sb.Append(grp).Append("，");
    sb.Append(TypeWord(s));                   // 按钮/开关/滑条/输入框/可点区域（真实类型判定，见 A.0）
    sb.Append("，第 ").Append(local + 1).Append(" 项，共 ").Append(total).Append(" 项");
    // 邻居：只在邻居能读到 Bound 文本时补，避免"按钮、按钮、按钮"
    AddNeighbor(sb, "上一项是", items, local - 1);
    AddNeighbor(sb, "下一项是", items, local + 1);
    // 状态后缀交给 Describe 现有逻辑（1083-1097），不要重复
    return sb.ToString();
}

private static void AddNeighbor(StringBuilder sb, string prefix, List<Selectable> items, int i)
{
    if (i < 0 || i >= items.Count) return;
    var r = ResolveLabel(items[i]);
    if (r.Quality > LabelQuality.Nearby || string.IsNullOrEmpty(r.Text)) return;
    sb.Append("；").Append(prefix).Append("『").Append(r.Text).Append("』");
}
```

所需 Unity API：`GetComponentsInChildren<TextMeshProUGUI>(true)`、`transform.parent` 上溯与 `GetSiblingIndex()`（已有）、`RectTransform.GetWorldCorners`（已有，`496-528`）、`Groups[i].Items`（已有）；**不需要** `LayoutGroup` 反射——位置排序已经按屏幕坐标做过（`496-528`），组内下标直接复用 `Items` 顺序即可。

### C.4 各层风险

| 层 | 风险 | 说明 |
|---|---|---|
| L0 | 文本正确性无法保证 | 子树里可能有占位/装饰文本（`"New Text"`），必须先靠 `GameTextIsNoise` 清（`MeiKongA11y/UiNav.Game.cs:40-51`）。本作噪声表待 probe |
| L1 | `HasOtherSelectable` 一刀切 | `828-867` 在容器里还有别的 `Selectable` 时直接返回 `""`；列表项自身无文字时**必然**走到 L2/L3，这是最常见退化源 |
| L1 | 别名表跨作污染 | 本作 `UiNav.Game.cs:23-38` 是钟塔表，**当前任何一条都不该生效**（对象名不匹配时只是不生效，但一旦巧合匹配就会念错名字） |
| L2 | 序号与视觉不一致 | `Scan` 已按屏幕位置排序（`496-528`，`CfgSortByPosition` 可关）；若 `CfgSortByPosition=false` 则序号按层级，与画面不一致，此时语义描述里的"第 N 项"会误导 → 建议 `Semantic` 文本在 `CfgSortByPosition=false` 时省略序号只念组名+类型 |
| L2 | 组名是对象名（出口 3） | 必须补 `GameGroupName`，否则语义描述变成「Canvas，按钮，第 3 项」——比对象名好，但仍不专业 |
| L3 | 用户被误导 | 对象名可能看起来像人话（`Exit`、`Close`），用户会以为那个按钮就叫这个；需要降级确认或降级提示（C.5） |
| 全部 | IL2CPP 类型判断 | 见 A.0，`TypeWord` 依赖真实类型 |

### C.5 "仅解析到原始标识时，激活前插入降级确认"如何接入

**激活入口与调用链（带行号）**：

```
Plugin.Update (Plugin.cs:236-251)
  └─ A11yHost.TickUiNav (Plugin.cs:190-197)
       └─ UiNav.Update (UiNav.cs:1450)
            └─ KeyEdge.Pressed(Return/KeypadEnter/Space) 1525-1526
                 ├─ CurrentItem()==null → return            1527-1529
                 ├─ _submitHandledFrame = Time.frameCount  1531   ← 防止游戏同一帧再吃一次回车
                 ├─ NeedsQuitConfirm && !QuitConfirmArmed
                 │     → ArmQuitConfirm(); return          1536-1540  （1281-1300 / 1302-1330）
                 ├─ Activate(target)                       1543
                 └─ RequestRescanNextFrame / Delayed       1549-1550 （1410-1419）
Activate(Selectable) 1332-1408：
  1334 null → ExitInternal(false)
  1345-1349 !interactable → Repeat.Say("该项当前不可用。", true)
  1353-1363 TMP_InputField → ExitInternal(false) + SelectByUs + ActivateInputField + 提示
  1365-1372 Toggle → 取反 + Repeat.Say(开/关, newLine:false 等价 interrupt=false) + RequestRescanNextFrame
  1374-1379 Slider → Repeat.Say("滑条请用左右方向键调整。", true)
  1381-1393 Button → label=TextOf(s)(1384) → Repeat.Say("已激活 "+label, interrupt:false)(1385) → onClick.Invoke()
  1395-1402 其它 → ExecuteEvents.Execute(submitHandler)
  1404-1407 catch → LogError("激活控件失败: ...")
```

**插入点**：`1536-1540` 与 `1543` 之间（即"退出确认已处理、真正 Activate 之前"）。理由：

1. `1531` 已经占住 `_submitHandledFrame`，此时插入不会让游戏多判读一次回车（`BlockGameAdvance:1233-1252` 的两种判据都成立）；
2. 早于 `Activate` 才能"不执行动作只朗读/要求确认"；
3. 晚于 `NeedsQuitConfirm` 可以避免与退出确认叠加成三重确认（退出确认优先，见 C.6）。

**状态机（镜像 `ArmQuitConfirm`/`ClearQuitConfirm`，`1302-1330`，8 秒窗口同 `QuitConfirmSeconds`）**：

```csharp
private static Selectable _degradedTarget;
private static float      _degradedUntil;

private static bool DegradedConfirmArmed
    => _degradedTarget != null && Time.unscaledTime < _degradedUntil;

private static void ArmDegradedConfirm(Selectable s, LabelResult r)
{
    _degradedTarget = s;
    _degradedUntil = Time.unscaledTime + QuitConfirmSeconds;
    // 先念能读的（L2 语义 + 邻居），不要念对象名；再要求二次确认
    Repeat.Say(SemanticText(s) + "。这一项读不出可读文字，可能只是图标按钮。"
             + "再按一次回车确认；按方向键取消。", true);
    A11yHost.Diag("[UiNav] 降级确认 target=" + s.gameObject.name + " source=" + r.Source);
}

private static void ClearDegradedConfirm() { _degradedTarget = null; _degradedUntil = 0f; }
```

**接在 1536-1540 之后**：

```csharp
LabelResult label = ResolveLabel(target);
if (label.Quality == LabelQuality.Raw
    && A11yHost.CfgSpeakRawName != null && !A11yHost.CfgSpeakRawName.Value)
{
    if (DegradedConfirmArmed && _degradedTarget == target) { ClearDegradedConfirm(); }
    else { ArmDegradedConfirm(target, label); return; }
}
// 落到原来的 1543 Activate(target)
```

清理点（一个都不能漏）：`ExitInternal`（`201-216`，与 `ClearQuitConfirm` 同处）、方向键/翻页/Home/End 分支（`1557-1572`）、当前项失效重扫（`1504-1512`）、场景切换（`1452-1465`）、鼠标点击/选中变化（若平台有 `SelectByUs` 的反向路径 `261-272`）。参考作把 `ClearQuitConfirm` 放在 `Announce` 里（`1177-1191`：`SelectByUs→ClearQuitConfirm`），照抄即可。

**配置项**（新增，默认值按"新作更保守"）：

| 配置项 | 默认 | 作用 |
|---|---|---|
| `CfgSpeakRawName` | **false**（本作） | 允许把对象名/类型名念出来。前三作在 L1 里默认 true 以保持逐字节一致 |
| `CfgDegradedConfirm` | true | 仅当解析到 `Raw` 时插入二次确认 |
| `CfgDegradedHint` | true | 不确认时（`CfgDegradedConfirm=false`）在朗读末尾加一句"（这一项没有可读文字）" |
| `CfgDegradedConfirmSeconds` | 复用 `QuitConfirmSeconds`（8 秒） | 确认窗口 |

注意 `Plugin.cs` 现有配置里**没有任何"原始标识回退"开关**（`Plugin.cs:55-68` 只有 `CfgQuitConfirm`/`CfgQuitNames`/`CfgExcludeNames`），`CfgSpeakName` 是"朗读带说话人"（`Plugin.cs:89-90`），不是对象名 —— 别混淆。

### C.6 与退出确认（`NeedsQuitConfirm`）的交互

- `NeedsQuitConfirm`（`1281-1300`）用 `CfgQuitNames` **全等**匹配 `gameObject.name`（`1295`，`OrdinalIgnoreCase`），8 秒窗口（`1302-1330`），激活前先念「这是退出游戏。再按一次回车或空格确认退出，按别的键取消。」（`1324`）。
- 顺序建议：**退出确认优先**（先 `1536-1540`），降级确认在后。若退出目标同时也是 `Raw`，两个确认会叠加成三次回车 —— 建议在 `NeedsQuitConfirm` 已武装时不 `ArmDegradedConfirm`（退出本来就是危险动作，用户已经被告知对象名/标签）。
- `ClearQuitConfirm` 被 `ExitInternal`（`201-216`）和 `Announce`（`1177-1191`）调用；新增的 `ClearDegradedConfirm` 必须跟它们同点调用，否则退出导航后 8 秒内按回车会莫名其妙"确认"了一个旧目标。

---

## D. 与朗读队列的关系（降级确认/补充朗读如何不打断、不重复）

### D.1 `Speech` 的能力边界（硬约束）

`Framework/platform/Speech.cs`：

- 对外只有两个方法：`Speak(string text, bool interrupt)`（`478-533`）与 `Stop()`（`535-561`）。
- `Speak` 里 `480` 空串直接返回；`488-493` 诊断日志；`495-525` 按后端 `Tolk_Output(text, interrupt)` / `Nvda.Speak(text, interrupt)` / `Zdsr.Speak(text, interrupt)` / `Sapi.Speak(text, interrupt)`；`527-532` 异常时后端降 `None`。
- **没有队列、没有优先级、没有 `IsSpeaking`、没有"等当前读完再念"的接口**。是否排队/是否掐断完全由后端实现与 `interrupt` 布尔决定。
- `Repeat.Say(text, interrupt=true)`（`Repeat.cs:59-63`）= `Note(text)` + `Speech.Speak(text, interrupt)`；`Note`（`69-75`）对相邻相同文本去重；`Again`（`103-113`）不经去重与排队；`Push/Drop`（`81-100`）给"优先项"（选项）用。

由此得两条设计结论：

1. **不能实现"等当前朗读结束再补念"**（无法查询是否在朗读）；只能选 `interrupt=true`（掐断）或 `false`（交给后端排队/叠加）。
2. **"重复朗读"的防线只有两条**：`Repeat.Note` 的相邻去重（`69-75`）+ 调用方自己的状态机（例如降级确认只念一次、rescan 合并播报）。

### D.2 各类朗读的 `interrupt` 策略

| 朗读内容 | 通道 | `interrupt` | 现状/改动 |
|---|---|---|---|
| 导航开关（进组播报/退出） | `Repeat.Say`（`1141-1146`/`201-216`） | **true** | 现有；必须掐断（否则用户不知道模式已切换） |
| 当前项描述 + `i / N` | `Announce`（`1177-1191`） | **true** | 现有；用户按方向键就是要立刻听到新项 |
| 「界面已更新。」 | `Announce`（`1494`） | true | 现有；见 D.4 的连读风险 |
| 「该项当前不可用。」 | `Repeat.Say`（`1345-1349`） | true | 现有 |
| 「已激活 <label>」 | `Repeat.Say(..., false)`（`1385`） | **false** | 现有；激活后游戏的音效/语音要说，别抢 |
| 降级确认提示（C.5） | `Repeat.Say` | **true** | 新增；必须让用户听见"要二次确认"，否则第二次回车会被当成重复按键 |
| 降级提示（不确认模式，结尾一句） | 新增 `Repeat.SaySoft` 或并入 `Describe` | **false** | 新增；只补充信息，不该打断正在念的项名 |
| 输入框提示（`1353-1363`） | `Repeat.Say` | true | 现有 |

参考作的一致做法：`noexistence Plugin.cs:126-151` 把 `CfgSpeechInterrupt` 默认开（与 TransparentHer 一致），理由是"长句被打断可以按重读键翻回"；`553-565` 在"有游戏语音"时让路（先 `Speech.Speak(name, false)` 再 `Speech.Speak(line, CfgSpeechInterrupt)`）。本作的补充朗读（降级提示）应取 `false`，理由同上：它是信息补充，不是状态变更。

### D.3 具体实现建议（不新增后端能力）

1. **L1 增加一个"软朗读"入口**，避免逐作层直接调 `Speech.Speak`（`lint_repeat.ps1` 禁止逐作层直调 `Speech.Speak(`，`Repeat.cs:6-32`）：
   ```csharp
   // Repeat.cs
   internal static void SaySoft(string text) // 不打断、去重、不进"重读优先项"
   {
       if (string.IsNullOrEmpty(text)) return;
       Note(text);
       Speech.Speak(text, false);
   }
   ```
2. **降级提示只念一次**：在 `Announce` 里维护 `_lastAnnouncedItem`（`1193-1203` 已有 `_announcedItem` 类似状态），同一控件同一 `Quality` 只补一次。
3. **不要用定时器补念**：`G11`（`README.md:362-388`）明确"界面重扫不能定时，0.35 秒重扫撞场景销毁会无 C# 异常原生崩溃"；补念同理，不要在 `Invoke`/协程里延后朗读。
4. **降级确认的"再次按回车"必须走同一个 `KeyEdge` 边沿**：否则合成输入的双报会让"确认"和"执行"在同一帧发生，等于没确认（`KeyEdge.cs:28-47` 的教训）。

### D.4 重复/连读风险点（实测要听的）

- `1494`（rescan 后发现列表变化 → `Announce("界面已更新。")`）与 `1543`（激活后）之间，`1549-1550` 会请求两次重扫（下一帧 + 第 14 帧）；两次重扫各自可能触发 `1494` → 用户听到连续的「界面已更新。…… 界面已更新。」。建议：`Announce` 里对"界面已更新"做 600ms 内的抑制（用 `Time.unscaledTime` 记时间戳，不用协程）。
- `1525-1553` 回车分支里 `NeedsQuitConfirm` 与降级确认都可能 `return`，用户按两次回车会先听到两条不同的确认语 → 文案要能区分（"再按一次回车确认退出" vs "再按一次回车确认激活"）。
- `Announce` 每次 `Repeat.Say(..., true)`（`1177-1191`），所以连续按方向键会不断掐断上一条 → 快速浏览时只能听到最后一个，属预期；但要保证每个新项的 `Describe` 足够短（L2 语义描述比对象名长，注意别把「第 N 项 / 共 M 项」越写越长）。

---

## E. 参考作里值得迁移的"朗读文本清洗 / 角色名 / 选项朗读 / 历史回放"清单

只列清单与位置，不抄大段代码。按对本作的价值排序。

| # | 模式 | 位置 | 对本作的价值 |
|---|---|---|---|
| 1 | 文本清洗管线（Ruby、标签、行内注释、换行→逗号、方块字符、标点归一） | `noexistence_a11y/.../TextProc.cs:17-30`（五个正则）、`46-83`（`ToSpeech`）、`86-98`（`Fingerprint`）、`101-110`（`IsTrivial`） | 高：本作是文字冒险，台词/选项都要清洗；规则要按本作文本格式重写（**待 probe**）。本作已有 `TextProc.cs`（4.3KB）但内容待核 |
| 2 | 选项整组朗读 + 内容变了才播报 + 数字键直选 | `noexistence_a11y/.../Choices.cs:163-216`（Update，`171` 判面板可见、`184` 子物体数变化才 Refresh、`188-215` 整组拼一段）、`218-241`（数字键，`try/catch`）、`243-257`（`onClick.Invoke`）、`261-267`（Harmony 挂点） | 高：选项是核心路径；注释 `9-28` 的三坑（回调时 TMP 还没写、容器第 0 个子物体不是第一项、文字在子物体）可直接复用 |
| 3 | 历史回放 / 重读 | `noexistence Plugin.cs:387-415`（`Remember`/`RepeatLast`，只重读最后一句；`399-401` 注释建议用游戏自带 History 面板 + Tab 逐条）；`belfry_a11y/.../Reader.cs:92-100`、`137`、`205`；`MeiKongA11y/Reader.cs:121`；`transparenther Plugin.cs:214-230` | 高：本作有 `MemoWindowChrome`/`StashWindow`（`types_c.txt:335-336`）与 `ChatListWindow`，历史/回想可复用这套"记住最后一句 + 游戏面板逐条走" |
| 4 | 说话人名字映射，查不到退回英文 ID（不静默丢人） | `noexistence_a11y/.../SpeakerNames.cs:9-61`（生成表）、`64-69`（`Resolve`） | 高：本作 `Lilith`/角色名需中文表；退回 ID 比静默好 |
| 5 | 有配音行让路：先念名字（不打断）再念台词 | `noexistence Plugin.cs:548-565`（尤其 `553-560`） | 中高：本作有 `ButtonSound`/配音，需实测 |
| 6 | 输入框字段角色命名（引用比对游戏序列化字段，而非按槽位猜） | `MeiKongA11y/UiNav.Game.cs:273-300`（`PomodoroRoleOf`/`PomodoroFields`）、`285-288` | 中高：本作 `SettingInputFieldItem`（`types_c.txt:402`）与可能的姓名输入框；上一作 `NameInputManagerTMP` 反射（`justlilith UiNav.Game.cs:61-104`）**待验证是否同名** |
| 7 | 占位文本噪声表 | `MeiKongA11y/UiNav.Game.cs:40-45`（`PlaceholderTexts`）、`47-51`（`GameTextIsNoise`） | 中高：本作 `SettingHotkeyRow` 等模板行可能带占位文本 |
| 8 | 路径感知标签解析（`PathOfLabel` + 父节点名 + 兄弟序号） | `MeiKongA11y/UiNav.Game.cs:58-165`、`204-214` | 高：直接套到本作 Canvas 路径（`/Canvas/…/`） |
| 9 | 组中文名 + 组排序权重 | `MeiKongA11y/UiNav.Game.cs:226-244`（`GameGroupName`）、`246-257`（`GameGroupPriority`） | 高：消除出口 3（`GroupName:1117` 念 Canvas 名） |
| 10 | 面板可见性判定（画面上才算活着） | `noexistence_a11y/.../UiVis.cs`（61 行，`Visible(panel)`）；调用点 `Choices.cs:171` | 中：本作面板多（`ViewPanelBase`/`Panel_ClosetMain`/`TypePanel` 等），避免给隐藏面板播报 |
| 11 | 动画期内容节流（内容没变不播报） | `noexistence Choices.cs:184-215`（`key` 比较） | 中：本作有 `PlayerLineButtonAnimWave/Order`（`types_c.txt:358-359`）、`OptionButtonAnim`，动画期间列表抖动会刷屏 |
| 12 | 噪声/静音（`Speech.Stop` 与"沉默键"） | `transparenther Plugin.cs:664-671`（沉默键）、`658-662`（重读键） | 中：本作 `CfgSilenceKey("0")` 已有（`justlilith Plugin.cs:104-105`） |

---

## F. 风险汇总

| 编号 | 风险 | 级别 | 说明 |
|---|---|---|---|
| R0 | L1 `as`/`is` 在 IL2CPP 下静默失败（A.0） | **阻断** | 不修则导航扫不到控件或全念 `Selectable`；修法有（`TryCast` shim），但 14 处 + `GetType()` 都要换，且要验证 `Transform→RectTransform` 的 `TryCast` |
| R1 | 本作 `UiNav.Game.cs` 是上一作占位 | 高 | 别名表/输入框反射全部不是本作证据（`UiNav.Game.cs:1-22`）→ 当前任何"已实现"的说法都不成立 |
| R2 | `GameGroupName` 缺失 → 每次进组念 Canvas 名 | 高 | `UiNav.cs:1117`；补钩子即可（MeiKong 样板 `226-244`） |
| R3 | L2 语义层依赖"组内序号"，`CfgSortByPosition=false` 时误导 | 中 | `496-528` 排序逻辑；设计里已要求降级到"只念组名+类型" |
| R4 | 降级确认与退出确认叠加 | 中 | 建议退出确认优先、降级确认不在其武装期内触发（C.6） |
| R5 | 热键与游戏/读屏/IME 冲突 | 中 | `Ctrl+Alt+G` + `Hotkey` 严格修饰键语义；Tab 默认关（B.3） |
| R6 | `Speech` 无队列/无 `IsSpeaking` | 中 | 只能 `interrupt` 布尔；补充朗读一律 `false`；不能"等读完" |
| R7 | 平台层改动会波及前三作 | 中 | 四钩子只增不改（`A11yHost.cs:180-192`）；新配置项默认值必须保持前三作逐字节一致 |
| R8 | 逐作拷贝 `UiNav.cs` 与平台层漂移 | 中 | 两份 sha 不同、行数一致；改 L1 后必须整份覆盖到 `justlilith_a11y/.../UiNav.cs`（不要只改一处） |
| R9 | 重复朗读/连读 | 低中 | `Repeat.Note` 相邻去重 + 时间戳抑制（D.4） |
| R10 | 三处文案"Tab"硬编码 | 低 | `Plugin.cs:122-129`、`UiNav.cs:35-46` 文件头、`UiNav.cs:1361`「按 Tab 返回导航」；改键后必须同步，否则文档与行为不一致 |

---

## G. 待实机验证清单（每条都要写"尚未实机复测"）

| 编号 | 验证项 | 方法 | 通过标准 |
|---|---|---|---|
| V1 | IL2CPP 下 `Scan` 是否真的一无所获（A.0） | 装 probe 插件，进任意菜单，看 `Scan`/`DumpScan`（`UiNav.cs:398`）日志条数 | 有 Items 则 R0 表现为分辨率更低（类型判断失败），无 Items 则阻断 |
| V2 | 真实控件类型分布 | probe 转储每个 `Selectable` 的 `GetType().Name`（原生类名） | 得到 Button/Toggle/Slider/自定义 的占比，决定 `TypeWord` 表 |
| V3 | `"Function Name"` / `"Object Name"` 从哪来 | dump 全部 TMP/legacy `Text` 的文本，grep 这两个串；再查 `SettingHotkeyRow` 显示 | 找到来源后决定进噪声表还是靠钩子改名 |
| V4 | 占位文本实例清单 | 同 V3，收集 `New Text`/`Button` 等候选，逐条确认是否真实按钮的标签 | 噪声表每条都有出处（`README.md:197-201`） |
| V5 | 本地化注入时机（`AutoBindLocalizeStringEvent`） | 切语言、进面板时 dump 文本 + 帧号 | 文本在 Scan 前还是后被写入，决定是否需要延迟重扫 |
| V6 | 文本是否在兄弟/子物体、层级深度 | dump 每个控件的 `TextOf` 链路各层（own/row/alias/ancestor） | 统计各层命中率，验证 L2 的必要性 |
| V7 | 桌宠/立绘可点击区（`LilithSkeleton*`/`CharacterHitArea`）是否产生 `Selectable` | probe 按类型列 `Selectable` | 决定是否要 `CfgExcludeNames` 排除或 `GameLabelOf` 命名 |
| V8 | `Ctrl+Alt+G` 是否被 IME/游戏占用 | 手动按，看导航日志与游戏反应 | 无冲突才作默认；有冲突换 F 键并改默认 |
| V9 | Tab 是否被游戏占用 | 手动按 Tab 观察游戏行为（切输入框/暂停） | 决定 `CfgNavKeyAllowTab` 默认值 |
| V10 | `Input` 是否仍可用（Active Input Handling） | 看 `Player.log` 有无 `InvalidOperationException`；或 probe 打印 `Input.GetKeyDown` 结果 | 决定是否必须把 `KeyEdge` 全面 `try/catch` |
| V11 | 后端对 `interrupt=false` 的排队/叠加行为 | 连续两条 `SaySoft` 听感；NVDA/Tolk/SAPI 分别测 | 决定降级提示是否改用 `interrupt=true` |
| V12 | 重扫连读（D.4） | 存档/设置面板里反复上下移动，听是否连念「界面已更新。」 | 出现则加时间戳抑制 |
| V13 | 退出确认 + 降级确认叠加体验 | 故意进一个无文本的退出按钮 | 不应出现三重确认 |
| V14 | `sendNavigationEvents` 本作要不要常驻关 | 判据：游戏自己是否调用 `SetSelectedGameObject` / 依赖选中态（`Plugin.cs:180-189`） | 关掉后鼠标点过的按钮不会被空格重复激活，且游戏自身导航不受影响 |

---

## H. 落地顺序建议（给 Lead）

1. **先修 R0（A.0）**：加 `As<T>/Is<T>` shim，替换 `UiNav.cs` 14 处 `as`/`is` 与两处 `GetType().Name`；同步到 `justlilith_a11y/.../UiNav.cs`（整份覆盖，别手改单行）。这一步不通过，后面全是空谈。
2. **补 `UiNav.Game.cs`（R1/R2）**：先补 `GameGroupName`（消除出口 3）与 `GameTextIsNoise`（占位文本，V3/V4 取证后），再按 V2/V6 统计结果写 `GameLabelOf`；`NameAlias` 每条注明来源（`README.md:197-201`），并把上一作的表整段清空重来。
3. **导航开关（B）**：`A11yHost.NavTogglePressed` 钩子 + `UiNav.cs:1498` 分叉（方案 A），`Plugin.Update` 填 `NavKeyPressed`；`CfgNavKey`/`CfgNavKeyAllowTab` 两个配置 + 三处 Tab 文案同步（R10）。
4. **分层降级（C）**：`LabelResult`/`ResolveLabel` 行为中性重构 → `SemanticText` → 降级确认状态机（C.5）→ 配置项（`CfgSpeakRawName` 本作默认 false）。
5. **朗读队列（D）**：`Repeat.SaySoft` + `Announce` 抑制时间戳 + 文案区分；不需要改 `Speech` 后端。
6. **E 清单按价值排**：先 1/2/3（文本清洗、选项、历史），再 8/9（路径标签、组名），最后 6/10/11。

> 全文结论均为静态源码推断；R0 的实际表现（扫不到 vs 全念类型名）与上述所有"待实机验证"项，**尚未实机复测**。
