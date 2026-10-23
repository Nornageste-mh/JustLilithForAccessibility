using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

namespace JustLilithA11y
{
    /// <summary>
    /// 《不/存在的莉莉丝》读屏无障碍补丁。
    ///
    /// 这是**逐作层**的入口。平台层（Speech / Nvda / Sapi / UiNav）从 A11yFramework
    /// 整份拷来，**一行都不要改**；这个文件负责四件事：
    ///   1) 注册配置项（分「朗读」「界面导航」「其它」三组，组名不要改）
    ///   2) 把配置与钩子填进 A11yHost（契约层）
    ///   3) 每帧驱动 Reader / Choices / UiNav
    ///   4) 按配置挂 Harmony 补丁（默认不挂，见 Patches.cs）
    ///
    /// === 为什么基类是 BasePlugin 而不是 BaseUnityPlugin ===
    /// 本作是 **IL2CPP + BepInEx 6**。BepInEx 6 的 IL2CPP 运行时里没有 BaseUnityPlugin
    /// （BepInEx.Unity.IL2CPP.dll 的元数据里查不到这个类型名，只有 BasePlugin），
    /// 插件也不直接是 MonoBehaviour —— 拿 Update 要靠 AddComponent&lt;T&gt;() 挂一个自己的组件。
    /// 这和 Mono 路线（《钟塔》《妹控计划》）写法不同，是 BepInEx 6 的硬事实，不是笔误。
    ///
    /// === 补丁点一览（方法名**全部**来自 Assembly-CSharp.dll 的反编译实查）===
    ///
    ///   功能                          挂载点（来源）
    ///   --------------------------    ------------------------------------------------------------
    ///   朗读台词（有/无配音）          订阅 DialogueManager.OnDialogueStart / OnDialogueAdvance
    ///                                 （DialogueManager.cs:2559 / 2574，实例属性）
    ///   朗读选项 + 数字键选择          订阅 OnDialogueAdvance + DialogueNode.options[].text
    ///                                 （DialogueNode.cs:234）→ DialogueManager.SelectOption(int)
    ///                                 （DialogueManager.cs:3760，0 基）
    ///   玩家台词选项（玩家自己的话）    PlayerLineController.IsAnyOptionsShowing（4170）+ _texts（3357）
    ///                                 → PlayerLineController.OnButtonClicked(int)（5366）
    ///   有配音判定                     DialogueManager.ResolveNodeSoundId(node)（3979）
    ///                                 与 DialogueLineRepository.TryGetVoiceSoundId(lineId, out sid)（190）
    ///   拦住游戏自己的「推进剧情」     订阅/轮询已经能在导航模式下抑制（UiNav.BlockGameAdvance）
    ///   菜单 / 存档 / 设置键盘导航      无挂载点，UiNav 每帧扫描 Selectable；
    ///                                 逐作层只在 UiNav.Game.cs 补别名 / 标签 / 组名
    ///   台词兜底（默认关）             Harmony 后缀 DialogueManager.Say（3705 / 3687），见 Patches.cs
    ///
    /// === 为什么「朗读」优先订阅事件、不优先打补丁 ===
    /// IL2CPP 里 interop 生成的 DialogueManager 是**托管代理**，方法体只是转发进 native。
    /// Harmony 打代理是能生效的，但①拿到的是「调用瞬间」的原始参数，不一定是屏幕上最终那句话
    /// （打字机/快进会重发）；②事件是游戏自己的通知，语义正确、不用猜。
    /// 所以：事件优先 → 失败降级为每帧轮询 → 两者都失败时可以用配置打开 Say 补丁做第三条腿。
    /// 实际走哪条路径，Reader 会在日志里用 path=events / path=poll / path=say-patch 标出来。
    ///
    /// 铁律 1：不许猜方法名和对象名。只能来自反编译实查，或实机运行时日志。
    /// </summary>
    [BepInPlugin(Guid, "不/存在的莉莉丝 A11y Reader", "0.2.0")]
    public class Plugin : BasePlugin
    {
        public const string Guid = "justlilith.a11y.reader";

        // ==================== 配置项 ====================
        //
        // 组名与项名照抄三作（流水线 §6 P2）：玩家在三个补丁之间切换时不用重新学。
        // ★ 中文说明里**不要写没实测过的数字**。本作的配音覆盖率、选项处数、手机剧情段落
        //   目前都还没有实机统计（Reader 会在日志里按固定间隔打一条 stats 供校准），
        //   所以这里的措辞一律是「先这样默认，日志见真章」，不编数字。

        // ---- 朗读 ----
        internal static ConfigEntry<bool> CfgReadUnvoiced;
        internal static ConfigEntry<bool> CfgSpeakWhenUnknown;
        internal static ConfigEntry<bool> CfgSpeakName;
        internal static ConfigEntry<bool> CfgReadChoices;
        internal static ConfigEntry<bool> CfgChoiceHotkeys;
        internal static ConfigEntry<bool> CfgScreenText;
        internal static ConfigEntry<float> CfgRealTimeSeconds;
        internal static ConfigEntry<string> CfgSilenceKey;
        internal static ConfigEntry<string> CfgRepeatKey;
        internal static ConfigEntry<string> CfgSpeechBackend;
        internal static ConfigEntry<bool> CfgReadPhone;
        internal static ConfigEntry<bool> CfgReadPhoneSticker;

        // ---- 界面导航 ----
        internal static ConfigEntry<bool> CfgMenuNav;
        internal static ConfigEntry<string> CfgNavKey;
        internal static ConfigEntry<string> CfgMenuKey;
        internal static ConfigEntry<bool> CfgMenuKeepAlive;
        internal static ConfigEntry<bool> CfgBodyNav;
        internal static ConfigEntry<bool> CfgSortByPosition;
        internal static ConfigEntry<bool> CfgVisibleOnly;
        internal static ConfigEntry<bool> CfgQuitConfirm;
        internal static ConfigEntry<string> CfgQuitNames;
        internal static ConfigEntry<string> CfgExcludeNames;

        // ---- 其它 ----
        internal static ConfigEntry<bool> CfgSpeakRawName;
        internal static ConfigEntry<bool> CfgUnknownConfirm;
        internal static ConfigEntry<bool> CfgGlobalKeys;
        internal static ConfigEntry<bool> CfgStartupHint;
        internal static ConfigEntry<bool> CfgAnnounceOnSkipStop;
        internal static ConfigEntry<bool> CfgBubblePersist;
        internal static ConfigEntry<bool> CfgDiagLog;
        internal static ConfigEntry<bool> CfgHarmonyPatches;

        public override void Load()
        {
            // ================================================================
            // 1) 先填日志出口 —— 后面每一步失败都要能记下来
            // ================================================================
            A11yHost.Log = Log;

            // ================================================================
            // 2) 注册配置项
            //    三组的组名固定：「朗读」「界面导航」「其它」
            // ================================================================
            CfgReadUnvoiced = Config.Bind("朗读", "朗读无配音剧情", false,
                "★ 默认关（用户拍板）：本作台词全程有配音，读屏用户只需要导航与控制，\n" +
                "台词交给游戏语音，不重复朗读。\n" +
                "打开后：没有配音的台词（DialogueNode.soundId 为空且台词库查不到语音）会由补丁念出来。\n" +
                "实机日志（[Reader] stats）会给出有配音/无配音的实际比例，需要时可据此再调。");

            CfgSpeakWhenUnknown = Config.Bind("朗读", "无法判定时也朗读", false,
                "★ 默认关（用户拍板）：本作台词全程有配音，默认不朗读任何台词。\n" +
                "打开后：当抓不到当前行的剧本数据、或判定不了有没有配音时（例如刚读档跳进剧情），\n" +
                "选择朗读而不是跳过（宁多读，不缺读）。\n" +
                "注意两项都关着时，对话事件仍然会订阅——它负责判定「这句有配音，只让语音播」并写日志，\n" +
                "只是不再调用语音合成念台词。");

            CfgSpeakName = Config.Bind("朗读", "朗读时带上说话人", true,
                "在台词前加上说话人名字。旁白没有名字，不会加前缀。\n" +
                "本作 DialogueNode.speaker 是字符串，实际值会在日志里逐条打印前几个，\n" +
                "如果发现是内部 ID 而不是中文名，把这一项关掉即可。");

            CfgReadChoices = Config.Bind("朗读", "朗读选项", true,
                "出现选项时，把全部选项一次念完并编号。\n" +
                "只念一遍并进重读缓冲，不会因为每帧轮询而重复念。");

            CfgChoiceHotkeys = Config.Bind("朗读", "数字键选择选项", true,
                "用数字键 1-9 选择对应编号的选项。\n" +
                "只做「替你按了那一个按钮」，不会取消或缩短游戏自己的倒计时，也不删任何选项。");

            CfgScreenText = Config.Bind("朗读", "朗读界面浮动文本", true,
                "朗读**不经过对话系统**的界面文字：画面中央的提示条、开场「记忆相框」的逐句字幕等。\n" +
                "本作开场演出（OpeningPerformanceManager 的 cue）把文本直接写进 TMP，不产生 DialogueNode，\n" +
                "所以只订阅对话事件会漏读它们。\n" +
                "只扫**可见**且**不属于**对话气泡 / 台词选项 / 可导航控件的文字；\n" +
                "纯数字（倒计时、百分比、时间）与占位字会被过滤。\n" +
                "若觉得界面文字念得太碎，把这一项关掉即可。");

            CfgRealTimeSeconds = Config.Bind("朗读", "限时选择时长", 20f,
                "★ 本作目前**没有效果**（保留字段，避免配置迁移时丢键）。\n" +
                "反编译实查：DialogueBubbleUI 的选项、PlayerLineController 的台词选项都没有\n" +
                "倒计时字段（DialogueOption 只有 text / nextId 两个属性，DialogueNode 只有 duration），\n" +
                "也就是说本作没有限时选择。\n" +
                "真有计时的话也只允许**拉长**、不允许取消：到点会走沉默分支，\n" +
                "取消掉等于删掉一个剧情分支。");

            CfgSilenceKey = Config.Bind("朗读", "沉默按键", "0",
                "在限时选择里立刻选择「沉默」（什么都不做，让计时自己走到沉默分支）。\n" +
                "留空则关闭。沉默键只清补丁自己的待选列表，绝不碰游戏的计时器。");

            CfgRepeatKey = Config.Bind("朗读", "重读按键", "Backspace",
                "重新朗读当前这一句的按键。填 KeyCode 名称（如 Backspace / F8）。\n" +
                "走的是平台层的重读缓冲，所以刚念过的、被选项挤掉的那句都还能听回来。\n" +
                "警告：不要填游戏原生占用的键。");

            CfgSpeechBackend = Config.Bind("朗读", "语音后端", "自动",
                "用哪个后端朗读。默认「自动」按 Tolk → NVDA → SAPI 挑第一个可用的。\n" +
                "可以填：自动 / Tolk / NVDA / SAPI。\n" +
                "钉死的后端不可用时**不会**回退 —— 这是刻意的，用于排查。");

            CfgReadPhone = Config.Bind("朗读", "朗读手机消息", true,
                "★ 本作目前**没有接上**（保留字段）。\n" +
                "反编译实查：本作没有手机，但有**聊天/备忘**类界面\n" +
                "（ChatListWindow / ChatListRow / MemoView / Cell_MemoItem / Cell_TodoItem）。\n" +
                "这些面板现在靠「界面导航」逐控件朗读（打开导航模式后用上下键听）；\n" +
                "「自动播报新消息」需要实机确认行文本长什么样，尚未实现，见 README 的待办。\n" +
                "留着这个键是为了将来接上时配置不用改名。");

            CfgReadPhoneSticker = Config.Bind("朗读", "朗读手机表情", true,
                "★ 本作目前**没有效果**（保留字段）：表情图现在只会被当作图片控件，\n" +
                "由界面导航报「图片」；等聊天面板自动播报实现后再决定要不要单独报「表情图片」。");

            CfgMenuNav = Config.Bind("界面导航", "菜单键盘导航", true,
                "让标题菜单 / 存读档 / 设置 / 画廊可以用键盘操作并被朗读。\n" +
                "  导航开关键   进入 / 退出导航模式（默认 Ctrl+Alt，见下一项）\n" +
                "  上 / 下      上一项 / 下一项\n" +
                "  左 / 右      调整滑条\n" +
                "  回车 / 空格  激活（按钮点击、开关切换、输入框聚焦）\n" +
                "  Home / End   第一项 / 最后一项\n" +
                "  PageUp/PageDown  切换面板组");

            CfgNavKey = Config.Bind("界面导航", "导航开关快捷键", "Ctrl+Alt+Plus",
                "进入 / 退出界面导航模式的快捷键。三种写法都认：\n" +
                "  Ctrl+Alt+Plus   纯组合键 + **主键盘加号**（默认，= Shift + 主键盘 '='）：\n" +
                "                  三个键一起按下即触发；小键盘加号不算；纯 Ctrl+Alt 不再触发。\n" +
                "  Ctrl+Alt        纯修饰键组合：按住 Ctrl 和 Alt，约 0.25 秒后触发一次；\n" +
                "                  期间按了别的键就取消，松开后再按才再触发。\n" +
                "  Ctrl+Alt+G     组合键 + 主键：按下 G 的那一帧触发。\n" +
                "  Tab             单键（等于平台层原本的默认行为）。\n" +
                "为什么默认改主键盘加号（用户拍板）：纯 Ctrl+Alt 太容易与读屏/输入法/游戏自带热键混，\n" +
                "而本作游戏自带热键全是 Ctrl+Alt+单字母，加号不在其中；\n" +
                "且主键盘 '=' 上的加号必须多按一个 Shift，误触概率最低。\n" +
                "为什么可能需要它全局生效：本作窗口是 WS_EX_NOACTIVATE（永远拿不到键盘焦点），\n" +
                "只读游戏内的输入拿不到按键 —— 见下一项「快捷键全局生效」。\n" +
                "写错了会自动退回 Tab 并在日志里提示。");

            CfgGlobalKeys = Config.Bind("界面导航", "快捷键全局生效", true,
                "游戏窗口没有焦点时，也响应导航开关键、方向键、回车、数字键等快捷键。\n" +
                "★ 默认开，而且**本作必须开**：实机取证显示游戏窗口带 WS_EX_NOACTIVATE\n" +
                "（永远拿不到键盘焦点），默认还开着「单击穿透」，只靠游戏内的输入系统\n" +
                "一个按键都收不到 —— 关了这项，导航和数字键会全部失效。\n" +
                "实现方式是读取系统键盘状态（GetAsyncKeyState），不装键盘钩子、不注入、不拦键；\n" +
                "游戏自己该收到的按键照样收到。\n" +
                "如果某项系统权限导致读取失败，日志会写明失败原因，此时退回「只在游戏有焦点时生效」。");

            CfgMenuKey = Config.Bind("界面导航", "打开菜单快捷键", "Ctrl+Alt+O",
                "随时打开 / 关闭游戏功能菜单（SettingView）的快捷键，**不需要先进导航模式**。\n" +
                "本作功能菜单默认不显示，而且会自己隐藏；有了这个键就能随时调出来。\n" +
                "为什么默认 Ctrl+Alt+O：本作游戏自带 9 条热键全是 Ctrl+Alt+单字母\n" +
                "（M 音乐 / K 闹钟 / T 计时 / P 番茄钟 / R 简易备忘 / B 详细备忘 / N 草稿备忘 / S 静音 / L 单击穿透），\n" +
                "O 没有被占用；导航开关键是 Ctrl+Alt+主键盘加号，也不冲突。\n" +
                "写法与「导航开关快捷键」相同（Ctrl+Alt+O / Ctrl+Alt+Plus / F2 之类），写错会在日志里提示并停用本快捷键。\n" +
                "打开/关闭时会朗读「功能菜单已打开 / 已关闭」。");

            CfgMenuKeepAlive = Config.Bind("界面导航", "功能菜单不自动隐藏", true,
                "★ 默认开（用户需求）：功能菜单存在时间很短，读屏用户来不及操作。\n" +
                "开启后：**由本补丁的快捷键打开**的菜单，一旦被游戏隐藏就自动重新打开\n" +
                "（有 1.5 秒冷却与次数上限，不会无限刷；对话一开始就立刻停止保活，剧情优先）。\n" +
                "关掉后菜单恢复游戏原本的行为。\n" +
                "注意：如果你用鼠标点了菜单上的关闭按钮，本补丁分不清「你关的」和「游戏自动关的」，\n" +
                "会重新打开一次 —— 本作窗口是单击穿透的，实机上点不到游戏按钮，所以基本不会遇到。");

            CfgBodyNav = Config.Bind("界面导航", "身体部位导航组", true,
                "在导航里增加一组「莉莉丝交互」，共 8 项：头 / 脸 / 胸口 / 身体 / 左手 / 右手 / 裙子 / 腿。\n" +
                "选中某项按回车 → 直接触发游戏自己的部位交互（走游戏内 API，不注入鼠标键盘）。\n" +
                "实现说明：游戏里这 8 个部位对象只是 Collider2D，不是可导航控件，所以本补丁\n" +
                "另外造了一个**完全透明**的 Canvas 放 8 个虚拟按钮；它们看不见，但导航能选中、回车能触发。\n" +
                "关掉后这一组消失（导航里其它控件不受影响）。");

            CfgSortByPosition = Config.Bind("界面导航", "按屏幕位置排序控件", true,
                "按控件在屏幕上的位置排序（先上后下、同一行先左后右），让「第几项」和画面对得上。\n" +
                "关掉则改回按渲染层级（兄弟节点序号）排序。");

            CfgVisibleOnly = Config.Bind("界面导航", "只导航看得见的控件", true,
                "只把画面上真正能看到、能点到的控件纳入导航。\n" +
                "只有在发现正常按钮被误排除时才需要关掉。");

            CfgQuitConfirm = Config.Bind("界面导航", "退出前二次确认", true,
                "在导航模式下激活下面列出的控件会先朗读一次确认，再按一次才真的执行；\n" +
                "按方向键即取消。");

            CfgQuitNames = Config.Bind("界面导航", "退出确认对象名", "quitGameButton",
                "哪些控件需要二次确认，按 Unity 里的对象名**全等**匹配（不是正则）。\n" +
                "默认值来自反编译实查：SettingView.cs:264 的 quitGameButton（莉莉丝面板的退出按钮）。\n" +
                "多个名字用逗号分隔；留空则关闭。名字不准时以日志里打印的对象名为准。");

            CfgExcludeNames = Config.Bind("界面导航", "排除的对象名", "",
                "这些对象**及其整棵子树**里的控件不纳入导航（按祖先链匹配）。\n" +
                "用途：把画面上的噪音（社交链接条、装饰按钮）从导航里摘掉。\n" +
                "多个名字用逗号分隔；留空则关闭。");

            CfgSpeakRawName = Config.Bind("界面导航", "朗读原始控件名", false,
                "控件读不到任何文字时，退化成念 Unity 里的对象名 / 类型名 / ID。\n" +
                "★ 默认关，而且**不是给玩家用的开关**：这些名字（CommonCloseButton、Button (6)、CCPlus）\n" +
                "要么不像人话，要么同名控件在不同面板里含义完全不同，念出来比不念更误导。\n" +
                "默认行为改念结构化描述（如「未命名按钮，第 3 项，共 7 项，面板设置」）。\n" +
                "只在排查「这个控件到底是什么」时临时打开；同时把「界面诊断日志」打开看原始名。");

            CfgUnknownConfirm = Config.Bind("界面导航", "推断标签激活前确认", true,
                "当控件的标签是**推断**出来的（结构化描述或原始对象名，不是界面上真有的文字），\n" +
                "第一次激活只朗读并提示，再按一次才真正执行。\n" +
                "理由：读屏玩家分不清「未命名按钮，第 2 项」和旁边那个「确定」，\n" +
                "按错可能直接存档 / 覆盖 / 返回标题。8 秒不确认失效，换项或退出导航即清除。");

            CfgStartupHint = Config.Bind("其它", "启动时播报", true,
                "游戏启动后朗读一句「无障碍补丁已加载」，用来确认读屏通路是通的。");

            CfgAnnounceOnSkipStop = Config.Bind("朗读", "快进停止时补念当前句", true,
                "★ 本作目前**只有保底行为**（没有独立的快进判定）。\n" +
                "反编译全文检索没找到任何 skip / 快进状态属性（IsSkipping / skipMode / IsFastForward 全无命中）。\n" +
                "所以现在的实际行为是：同一句在 2.5 秒去重窗口内不会重复念，窗口外停住的那句照常念 ——\n" +
                "这样即使游戏有快进，停下来那一句也不会漏；代价是快进过程中会跟着念（刷屏）。\n" +
                "等实机确认快进机制（可能是连点推进而不是独立状态）后再改成真正的「跳过期间静音」。");

            CfgBubblePersist = Config.Bind("其它", "对话气泡不自动消失", true,
                "★ 默认开（用户需求）：对话气泡出现后，不要等几秒就自己消失。\n" +
                "实机/反编译依据：本作 DialogueManager 自己有自动消失计时器\n" +
                "（`_autoDismissTimer`，方法 `UpdateAutoDismiss()`（DialogueManager.cs:3573）推进，\n" +
                "`ResetAutoDismissTimer()`（:3540）清零）。\n" +
                "开启后：对话进行期间每 0.5 秒把那个计时器清零 → 气泡一直留着，直到你推进下一句。\n" +
                "顺带把「选项列表多久后不再响应数字键」从 60 秒放宽到 10 分钟（气泡不消失时你可能会想很久）。\n" +
                "关掉后完全恢复游戏原本的行为（气泡按游戏设定自动消失）。");

            CfgDiagLog = Config.Bind("其它", "界面诊断日志", false,
                "把进入导航模式时扫描到的控件全部写进 LogOutput.log。\n" +
                "排查完请关掉，否则日志会变得很大。\n" +
                "注意：日志含台词原文，**不要公开**（IP 红线，见 README）。");

            CfgHarmonyPatches = Config.Bind("其它", "挂载 Harmony 台词补丁", false,
                "给台词朗读加第三条腿：直接用 Harmony 拦 DialogueManager.Say。\n" +
                "默认关——正常情况下事件订阅已经够用，补丁只是事件全部失效时的排查/兜底手段。\n" +
                "打开后日志里会出现 [Patches] 已挂载 …，台词的 path= 会变成 say-patch。\n" +
                "★ 只在需要验证「事件挂了还是抓不到数据」时打开，平时别开（可能多念临时台词）。");

            // ================================================================
            // 3) 挂上自己的 MonoBehaviour（BepInEx 6 的 BasePlugin 不是组件，
            //    每帧逻辑必须在 AddComponent 出来的组件里跑）
            // ================================================================
            try
            {
                AddComponent<LilithBehaviour>();
            }
            catch (Exception e)
            {
                Log.LogError("[a11y] AddComponent<LilithBehaviour> 失败: " + e);
            }
        }
    }

    /// <summary>
    /// 每帧逻辑的宿主。BepInEx 6 的 BasePlugin 不是 GameObject，所以单独一个组件。
    /// 里面所有对外调用都包了 try/catch：读屏补丁最忌讳因为一处异常把游戏的帧循环打断。
    /// </summary>
    internal sealed class LilithBehaviour : MonoBehaviour
    {
        private bool _updateErrorLogged;
        private static bool _repeatKeyErrorLogged;
        private static bool _silenceKeyErrorLogged;
        private static float _nextStatsAt;
        private static float _hotkeyCheckAt;
        private static float _hotkeyGiveUpAt;
        private static bool _hotkeyChecked;
        private Harmony _harmony;

        /// <summary>
        /// 单路隔离执行：某个模块抛异常时只记一次日志，不牵连同一帧里的其它模块。
        /// </summary>
        private static void TickPart(string name, Action body)
        {
            if (body == null) return;
            try { body(); }
            catch (Exception e) { LogPartError(name, e); }
        }

        private static void LogPartError(string name, Exception e)
        {
            try
            {
                if (!_partErrorsLogged.Add(name)) return;
                LogError("[a11y] " + name + " 更新异常（同类只记一次，其它模块继续运行）: " +
                         e.GetType().Name + ": " + e.Message);
            }
            catch { }
        }

        private static readonly HashSet<string> _partErrorsLogged = new HashSet<string>();

        private void Awake()
        {
            // ================================================================
            // 4) 填契约层 —— 平台层唯一看得见的东西
            //    漏填不会崩，只会静默失效，所以最后一定要 Validate()
            // ================================================================
            try
            {
                A11yHost.CfgSpeechBackend  = Plugin.CfgSpeechBackend;
                A11yHost.CfgDiagLog        = Plugin.CfgDiagLog;
                A11yHost.CfgVisibleOnly    = Plugin.CfgVisibleOnly;
                A11yHost.CfgSortByPosition = Plugin.CfgSortByPosition;
                A11yHost.CfgExcludeNames   = Plugin.CfgExcludeNames;
                A11yHost.CfgQuitConfirm    = Plugin.CfgQuitConfirm;
                A11yHost.CfgQuitNames      = Plugin.CfgQuitNames;

                A11yHost.BlockGameAdvance      = () => UiNav.BlockGameAdvance;
                A11yHost.ShouldMuteUnitySubmit = () => UiNav.ShouldMuteUnitySubmit;

                // 可选槽位（平台层里有安全默认值：留空 = 不念原始名 / 推断标签激活前要确认）
                A11yHost.CfgSpeakRawName  = Plugin.CfgSpeakRawName;
                A11yHost.CfgUnknownConfirm = Plugin.CfgUnknownConfirm;

                // ★ 本作**必须**开全局按键读取：实机取证显示游戏窗口带 WS_EX_NOACTIVATE
                //   （永远拿不到键盘焦点），只读游戏内输入时一个键都收不到，
                //   导航开关键/方向键/回车/数字键会全部失效。
                //   平台层据此把 KeyEdge.Pressed / Hotkey.Down 的读取并集到 GlobalKeys（GetAsyncKeyState），
                //   不装钩子、不拦键。玩家可以在配置里关掉（退回只在游戏有焦点时生效）。
                A11yHost.UseGlobalKeys = true;
                try
                {
                    if (Plugin.CfgGlobalKeys != null) A11yHost.UseGlobalKeys = Plugin.CfgGlobalKeys.Value;
                }
                catch { }

                // 逐作层的标签钩子：平台层按「子树文字 → 钩子 → 别名 → 对象名」取值，
                // 所以这里返回空串是安全的（等于不干预），返回非空即覆盖。
                A11yHost.GameTextIsNoise = s => UiNav.GameTextIsNoise(s);
                A11yHost.GameLabelOf     = s => UiNav.GameLabelOf(s);
                A11yHost.GameGroupName   = t => UiNav.GameGroupName(t);
                // 组排序：给「莉莉丝交互」一个很小的优先级（20），让它排在设置面板等默认 100 之前
                A11yHost.GameGroupPriority = t => UiNav.GameGroupPriority(t);

                // 每帧入口。逐作在这里决定要不要跑 UiNav。
                //
                // ★ 四路**互相隔离**：实机踩过的坑 —— UiNav 的类型初始化抛异常
                //   （NameAlias 忽略大小写重复键 → TypeInitializationException），
                //   异常从 UiNav.Update() 冒出来后，同一个 lambda 里后面的 Reader.Update()
                //   / Choices.Update() 全都被跳过，表现成「对话朗读完全没反应，stats lines=0」，
                //   查了半天才发现是另一个模块炸了。所以这里每路各自 try/catch：
                //   任何一个模块坏掉都不许拖死其它模块。
                A11yHost.TickUiNav = () =>
                {
                    TickPart("nav", () =>
                    {
                        if (Plugin.CfgMenuNav != null && Plugin.CfgMenuNav.Value) UiNav.Update();
                    });
                    TickPart("reader", Reader.Update);      // 台词抓取（事件失效时在这里降级轮询）
                    TickPart("choices", Choices.Update);    // 数字键选择
                    TickPart("screentext", ScreenText.Update); // 界面浮动文本（不经过 DialogueManager 的台词）
                    // 打开菜单快捷键 + 功能菜单保活（不依赖导航模式）
                    TickPart("menu", LilithMenu.Update);
                    // 身体部位导航组（隐藏 Canvas 的 8 个虚拟控件，场景切换后自动重建）
                    TickPart("bodynav", LilithBodyNav.Update);
                TickPart("lilithapi", LilithApi.Update);   // 部位交互分阶段链 + 兜底判定
                };

                A11yHost.Validate();
            }
            catch (Exception e)
            {
                LogError("[a11y] 填契约层失败: " + e);
            }

            // ================================================================
            // 5) 初始化语音后端
            // ================================================================
            try
            {
                Speech.Init(A11yHost.Log);
                if (Speech.Current == Speech.Backend.None)
                    LogWarn("语音不可用: " + Speech.LastError);
                else
                    LogInfo("语音后端就绪: " + Speech.BackendName);
            }
            catch (Exception e)
            {
                LogWarn("语音初始化异常: " + e.Message);
            }

            // ================================================================
            // 6) 导航开关键 → 平台层钩子
            // ================================================================
            BindNavToggle();

            // ================================================================
            // 7) 挂补丁（默认不挂，见 Patches.cs 顶部说明）
            // ================================================================
            _harmony = new Harmony(Plugin.Guid);
            try
            {
                int n = Patches.Apply(_harmony);
                if (n > 0) LogInfo("Harmony 补丁组已挂载: " + n);
                else LogInfo("Harmony 补丁未挂载（按配置）：台词由事件订阅 / 每帧轮询负责。");
            }
            catch (Exception e)
            {
                // ★ 注意：PatchAll 抛异常时**其他补丁仍然生效**。
                //   症状极隐蔽：只有一个功能不工作。所以这里必须 LogError 而不是吞掉。
                LogError("Harmony 补丁失败: " + e);
            }

            // ================================================================
            // 8) 启动提示（确认读屏通路是通的）
            // ================================================================
            try
            {
                if (Plugin.CfgStartupHint != null && Plugin.CfgStartupHint.Value)
                {
                    string key = (Plugin.CfgNavKey == null) ? "Ctrl+Alt" : Plugin.CfgNavKey.Value;
                    Repeat.Say("不存在的莉莉丝，无障碍补丁已加载。按住 " + key + " 约四分之一秒，开关界面导航。", true);
                }
            }
            catch (Exception e)
            {
                LogWarn("启动播报失败: " + e.Message);
            }

            // 与游戏自带热键系统的冲突检查：Awake 时热键表还是空的（实机探针实测 ~45 秒才读到 9 条），
            // 所以从 3 秒后开始每 2 秒重试，最多 30 秒（见 CheckHotkeyConflicts）。
            _hotkeyCheckAt = Time.unscaledTime + 3f;
            _hotkeyGiveUpAt = Time.unscaledTime + HotkeyRetryMax;

            // 契约层自检结果一次性打进日志：实机排查第一个要看的就是这几行
            try
            {
                string[] missing = A11yHost.Missing();
                if (missing != null && missing.Length > 0)
                    LogWarn("契约层缺项: " + string.Join("，", missing));
                else
                    LogInfo("契约层自检通过。");
            }
            catch { }
        }

        // ====================================================================
        // 导航开关键
        // ====================================================================

        /// <summary>
        /// 平台层（UiNav）每帧问一次「这一帧按了导航开关键吗」。
        ///
        /// 写法按配置值分三类，**分类结果缓存**（只有改配置才重算；每帧不做 Split/Parse 分配）：
        ///   ① 纯修饰键组合（默认 "Ctrl+Alt"）→ KeyEdge.ModifierChordDown(spec)：
        ///      左右任一都算；全按住 ≥ 0.25 秒才触发一次；按住期间按任何键盘键就作废
        ///      （Ctrl+Alt+X / Ctrl+Alt+N / Ctrl+Alt+Tab 都不会误开导航）；自带闩锁，
        ///      触发后必须全松才能再触发。
        ///   ② 带主键的写法（Tab / Ctrl+Alt+G / F9 …）→ Hotkey.Down(已解析的 Spec)：
        ///      按下那一帧触发；解析只做一次。
        ///   ③ 都不认识 → 警告一次并退回 Tab（保证玩家总有一个能用的开关）。
        /// 输入框聚焦时不接这个键：Ctrl+Alt 在不少输入法里是切换键。
        /// </summary>
        private static bool NavTogglePressed()
        {
            try
            {
                if (IsTextInputFocused()) return false;

                string spec = (Plugin.CfgNavKey == null) ? "" : Plugin.CfgNavKey.Value;
                spec = spec == null ? "" : spec.Trim();

                // 空值 = 玩家清空了配置：保持平台层原本的行为（Tab）
                if (spec.Length == 0) return KeyEdge.Pressed(KeyCode.Tab);

                if (!_specChecked || !string.Equals(_specCheckedFor, spec, StringComparison.Ordinal))
                    ClassifySpec(spec);

                bool edge = false;
                switch (_specKind)
                {
                    case SpecKind.PureModifiers:
                        try { edge = KeyEdge.ModifierChordDown(spec); }
                        catch (Exception e) { LogOnce("nav-chord", e); }
                        break;

                    case SpecKind.Hotkey:
                        try { edge = Hotkey.Down(_specParsed); }
                        catch (Exception e) { LogOnce("nav-hotkey", e); }
                        break;

                    default:
                        return KeyEdge.Pressed(KeyCode.Tab);   // 写法不认识 → Tab 兜底
                }

                if (edge)
                {
                    A11yHost.Diag("[Nav] 导航开关键按下（" + spec + "）");
                    return true;
                }
                return false;
            }
            catch { return false; }
        }

        private enum SpecKind { Unknown = 0, PureModifiers = 1, Hotkey = 2 }

        private static bool _specChecked;
        private static string _specCheckedFor;
        private static SpecKind _specKind;
        private static Hotkey.Spec _specParsed;

        /// <summary>判定一次配置写法属于哪一类（只在配置文本变化时调用）。</summary>
        private static void ClassifySpec(string spec)
        {
            _specChecked = true;
            _specCheckedFor = spec;
            _specKind = SpecKind.Unknown;

            if (IsPureModifierSpec(spec))
            {
                _specKind = SpecKind.PureModifiers;
                LogInfo("[Nav] 导航开关键 = 组合键 " + spec + "（按住 ≥0.25 秒触发，期间按别的键作废）");
                return;
            }

            try
            {
                Hotkey.Spec parsed;
                string why;
                if (Hotkey.Parse(spec, out parsed, out why))
                {
                    _specParsed = parsed;
                    _specKind = SpecKind.Hotkey;
                    LogInfo("[Nav] 导航开关键 = 热键 " + spec + "（按下即触发）");
                    return;
                }

                LogWarn("[Nav] 导航快捷键 \"" + spec + "\" 不可识别（" + (why ?? "") +
                        "），已退回 Tab。请改成 Ctrl+Alt（纯组合键）、Ctrl+Alt+G（组合键+主键）或 Tab。");
            }
            catch (Exception e) { LogOnce("nav-parse", e); }
        }

        /// <summary>spec 是不是「只由修饰键组成」（如 Ctrl+Alt / Ctrl+Shift+Alt）。</summary>
        private static bool IsPureModifierSpec(string spec)
        {
            try
            {
                if (string.IsNullOrEmpty(spec)) return false;
                string[] parts = spec.Split(new[] { '+', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts == null || parts.Length == 0) return false;
                for (int i = 0; i < parts.Length; i++)
                {
                    string p = parts[i].Trim().ToLowerInvariant();
                    switch (p)
                    {
                        case "ctrl": case "control":
                        case "alt":
                        case "shift":
                        case "win": case "windows": case "command": case "cmd": case "meta": case "super":
                            break;
                        default:
                            return false;      // 出现非修饰键 token
                    }
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>焦点在可输入文本框里吗（TMP_InputField / 旧版 InputField）。</summary>
        private static bool IsTextInputFocused()
        {
            try
            {
                EventSystem es = EventSystem.current;
                if (es == null) return false;
                GameObject go = es.currentSelectedGameObject;
                if (go == null) return false;
                if (go.GetComponent<TMP_InputField>() != null) return true;
                if (go.GetComponent<UnityEngine.UI.InputField>() != null) return true;
                return false;
            }
            catch { return false; }
        }

        private static readonly System.Collections.Generic.HashSet<string> _loggedOnce =
            new System.Collections.Generic.HashSet<string>();

        private static void LogOnce(string where, Exception e)
        {
            try
            {
                if (!_loggedOnce.Add(where)) return;
                LogWarn("[Nav] " + where + " 失败（同类只提示一次）: " + e.GetType().Name + ": " + e.Message);
            }
            catch { }
        }


        /// <summary>
        /// 把导航开关键挂到平台层。
        /// 平台层的钩子字段是 A11yHost.NavTogglePressed（Func&lt;bool&gt;）；留空时平台层退回 Tab。
        /// </summary>
        private static void BindNavToggle()
        {
            try
            {
                A11yHost.NavTogglePressed = NavTogglePressed;
                LogInfo("[Nav] 导航开关已绑定: " +
                        (Plugin.CfgNavKey == null ? "" : Plugin.CfgNavKey.Value) +
                        "（Tab 仍可用作兜底）。");

                // 注意：平台层**故意**没有 A11yHost.CfgNavKey ——
                // 「组合键怎么配」归逐作层（各作配置项不同），平台层只收一个 Func<bool> 的布尔结果。
                // 所以这里不需要、也不该把 ConfigEntry 往契约层塞。
            }
            catch (Exception e)
            {
                LogWarn("[Nav] 绑定导航开关键失败: " + e.Message);
            }
        }

        // ====================================================================
        // 热键（重读 / 沉默）
        // ====================================================================

        private void PollHotkeys()
        {
            try
            {
                if (Plugin.CfgRepeatKey != null && !string.IsNullOrEmpty(Plugin.CfgRepeatKey.Value) &&
                    Hotkey.Down(Plugin.CfgRepeatKey.Value))
                {
                    Reader.RepeatLast();
                }
            }
            catch (Exception e) { LogHotkeyOnce(true, e.GetType().Name + ": " + e.Message); }

            try
            {
                if (Plugin.CfgSilenceKey != null && !string.IsNullOrEmpty(Plugin.CfgSilenceKey.Value) &&
                    Hotkey.Down(Plugin.CfgSilenceKey.Value))
                {
                    Choices.ChooseSilence();
                }
            }
            catch (Exception e) { LogHotkeyOnce(false, e.GetType().Name + ": " + e.Message); }
        }

        // 注意：这里必须 static，且**不能**把 System.Exception 作为参数类型。
        // 实机警告证据：`[Warning:Il2CppInterop] Method Void LogHotkeyOnce(Boolean, System.Exception)
        //   on type JustLilithA11y.LilithBehaviour has unsupported parameter System.Exception e of type System.Exception`
        // —— Il2CppInterop 会给 MonoBehaviour 子类的**实例方法**生成 il2cpp 包装，
        //    遇到托管专属类型（System.Exception）就报 unsupported parameter。
        // 改成 static + 只收字符串后该警告消失。
        private static void LogHotkeyOnce(bool repeat, string detail)
        {
            if (repeat)
            {
                if (_repeatKeyErrorLogged) return;
                _repeatKeyErrorLogged = true;
            }
            else
            {
                if (_silenceKeyErrorLogged) return;
                _silenceKeyErrorLogged = true;
            }
            LogWarn("[a11y] 热键 " + (repeat ? "重读" : "沉默") + " 异常（只记一次）: " + detail);
        }

        // ====================================================================
        // 与游戏自带热键系统的冲突检查
        //
        // 依据：HotkeyService（HotkeyService.cs:10 `public static class HotkeyService`）
        //   `static IReadOnlyList<Descriptor> All`（393）
        //   `Descriptor.Id`（106）/ `Descriptor.Default`（121，HotkeyCombo）
        //   `HotkeyCombo.ToDisplayString()`（202）/ `IsSet`（126）
        // 游戏的热键可以在设置面板里改，所以「我们用的键」和「游戏默认键」重不重要，
        // 但**一定要让玩家看得到**：冲突时读屏和游戏会同时响应同一个键。
        // 这里只做两件事：把游戏热键表打进日志（实机校准用）+ 撞键时给一条明确警告。
        // 改成「自动避让」是不做的 —— 那会让键位随游戏版本漂移，玩家更没法预期。
        // ====================================================================

        // 实现注记（实机教训）：`HotkeyService.All` 在 Awake 时还是**空表**，
        // 探针实测要游戏跑到 ~45 秒才读出 9 条（music/alarm/timer/pomodoro/memo_simple/
        // memo_detailed/memo_scratch/mute/click_through，默认全是 Ctrl+Alt+<字母>）。
        // 实机日志复核（2026-10-22 19:12）连 30 秒都没等到表，说明初始化比探针更晚，
        // 于是改成**每 5 秒重试、最长 4 分钟**：读到非空即打表并停止；超时只记一条日志放弃。
        // 这条检查纯属「校准/提示」，读不到不影响朗读与导航，所以可以等很久、但不能刷屏。
        private const float HotkeyRetryEvery = 5f;
        private const float HotkeyRetryMax = 240f;

        private static int _hotkeyRetries;

        private static void CheckHotkeyConflicts()
        {
            try
            {
                float now = Time.unscaledTime;

                // HotkeyService.All 是 Il2CppSystem.Collections.Generic.IReadOnlyList<Descriptor>：
                // 托管侧没有 Count 属性、也没有可用 GetEnumerator（实测 CS1061 / CS1579），
                // 只有索引器 all[i]，长度用 CountOf() 反射拿；
                // 反射也拿不到时（实机日志里出现过：表明明有 9 条、CountOf 却一直 0）
                // 用索引器探测兜底 —— 一直读到抛异常为止。
                var all = HotkeyService.All;
                int total = CountOf(all);
                if (all != null && total <= 0) total = ProbeCount(all);
                if (all == null || total <= 0)
                {
                    if (now >= _hotkeyGiveUpAt)
                    {
                        _hotkeyChecked = true;
                        LogInfo("[Hotkey] " + HotkeyRetryMax + " 秒内仍未读到游戏热键表（可能本作不需要它，或更晚才初始化），跳过冲突检查。");
                    }
                    else
                    {
                        _hotkeyCheckAt = now + HotkeyRetryEvery;
                        _hotkeyRetries++;
                        // 前 3 次打 Info（Lead 看得到「确实在等」），之后只进 Diag，避免刷满日志
                        if (_hotkeyRetries <= 3)
                            LogInfo("[Hotkey] 热键表还没准备好（第 " + _hotkeyRetries + " 次），" + HotkeyRetryEvery + " 秒后再试。");
                        else
                            A11yHost.Diag("[Hotkey] 热键表还没准备好（第 " + _hotkeyRetries + " 次）。");
                    }
                    return;
                }

                _hotkeyChecked = true;

                var ours = new List<KeyValuePair<string, string>>();
                AddOwn("导航开关", Plugin.CfgNavKey, ours);
                AddOwn("重读", Plugin.CfgRepeatKey, ours);
                AddOwn("沉默", Plugin.CfgSilenceKey, ours);

                LogInfo("[Hotkey] 游戏自带热键（冲突校准用，共 " + total + " 条）：");
                int seen = 0;
                int limit = Math.Min(total, 512);   // 防御：表异常大时不要刷屏
                for (int i = 0; i < limit; i++)
                {
                    string id = "";
                    string disp = "";
                    try
                    {
                        HotkeyService.Descriptor d = all[i];
                        if (d == null) continue;
                        id = d.Id;
                        // HotkeyCombo 是 [StructLayout(Explicit)] 的**值类型**（HotkeyCombo.cs:9-10），
                        // 不能和 null 比较；IsSet 为假时表示这条热键没绑。
                        HotkeyCombo combo = d.Default;
                        if (combo.IsSet) disp = combo.ToDisplayString();
                    }
                    catch { continue; }

                    seen++;
                    LogInfo("[Hotkey]   " + (id ?? "") + " = " + (disp ?? ""));

                    for (int k = 0; k < ours.Count; k++)
                    {
                        if (SameKeySpec(ours[k].Value, disp))
                        {
                            LogWarn("[Hotkey] 冲突：本补丁的「" + ours[k].Key + "」用的是 " + ours[k].Value +
                                    "，游戏热键 " + (id ?? "") + " 也是 " + disp +
                                    "。请在配置里换一个键，否则一个键会同时触发两件事。");
                        }
                    }
                }
                LogInfo("[Hotkey] 实际读出 " + seen + " 条。");

            }
            catch (Exception e)
            {
                _hotkeyChecked = true;
                LogWarn("[Hotkey] 读取游戏热键表失败（不影响朗读）: " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>
        /// 读 Il2Cpp 集合的长度。Il2CppSystem 的 IReadOnlyList 在托管侧既没有 Count 属性
        /// 也没有 GetEnumerator（实测：foreach 报 CS1579、Count 报 CS1061），只有索引器；
        /// 所以长度用反射拿（属性 get_Count / 方法 get_Count 都试），拿不到就返回 0。
        /// </summary>
        private static int CountOf(object list)
        {
            try
            {
                if (list == null) return 0;
                Type t = list.GetType();
                System.Reflection.PropertyInfo p = t.GetProperty("Count");
                if (p != null)
                {
                    object v = p.GetValue(list);
                    if (v != null) return Convert.ToInt32(v);
                }
                System.Reflection.MethodInfo m = t.GetMethod("get_Count");
                if (m != null)
                {
                    object v = m.Invoke(list, null);
                    if (v != null) return Convert.ToInt32(v);
                }
                // 退路：ICollection<T> / ICollection 的非泛型 Count
                var iface = t.GetInterface("System.Collections.ICollection");
                if (iface != null)
                {
                    System.Reflection.PropertyInfo ip = iface.GetProperty("Count");
                    if (ip != null)
                    {
                        object v = ip.GetValue(list);
                        if (v != null) return Convert.ToInt32(v);
                    }
                }
            }
            catch { }
            return 0;
        }

        /// <summary>
        /// 索引器探测兜底：一直读到抛异常为止，得到集合长度。
        /// 为什么要这个：实机日志（2026-10-22 19:2x）里 [Hotkey] 连续 38 次
        /// 「热键表还没准备好」，而 Lead 的探针同一时刻能读到 9 条 ——
        /// 说明 CountOf 的反射路径在这个 Il2Cpp 包装类型上返回 0，而索引器是好的。
        /// 纯只读探测，不修改集合；上限 256 条防跑飞。
        /// </summary>
        private static int ProbeCount(object list)
        {
            if (list == null) return 0;
            try
            {
                Type t = list.GetType();
                System.Reflection.PropertyInfo item = t.GetProperty("Item");
                if (item == null) return 0;
                int last = 0;
                for (int i = 0; i < 256; i++)
                {
                    try
                    {
                        object v = item.GetValue(list, new object[] { i });
                        if (v == null) break;
                        last = i + 1;
                    }
                    catch { break; }
                }
                return last;
            }
            catch { return 0; }
        }

        private static void AddOwn(string name, ConfigEntry<string> entry, List<KeyValuePair<string, string>> into)
        {
            try
            {
                if (entry == null) return;
                string v = entry.Value;
                if (string.IsNullOrEmpty(v) || v.Trim().Length == 0) return;
                into.Add(new KeyValuePair<string, string>(name, v.Trim()));
            }
            catch { }
        }

        /// <summary>把 "Ctrl+Alt+G" 这类写法归一后比较（忽略空格与大小写）。</summary>
        private static bool SameKeySpec(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            return Norm(a) == Norm(b);
        }

        private static string Norm(string s)
        {
            var sb = new System.Text.StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (char.IsWhiteSpace(c)) continue;
                sb.Append(char.ToUpperInvariant(c));
            }
            return sb.ToString();
        }

        // ====================================================================
        // 每帧
        // ====================================================================

        private void Update()
        {
            try
            {
                if (A11yHost.TickUiNav != null) A11yHost.TickUiNav();
            }
            catch (Exception e)
            {
                // 每帧的异常不能刷屏：只记一次
                if (!_updateErrorLogged)
                {
                    _updateErrorLogged = true;
                    LogError("[a11y] Update 异常: " + e);
                }
            }

            // 热键放在 TickUiNav 外面：即使导航/朗读每帧逻辑抛异常，重读键也还能用
            PollHotkeys();

            // 3 秒后做一次「与游戏热键撞键」的检查（只做一次）
            try
            {
                if (!_hotkeyChecked && Time.unscaledTime >= _hotkeyCheckAt) CheckHotkeyConflicts();
            }
            catch { }

            // 定期打一条统计，供实机校准（不刷屏）
            try
            {
                if (Time.unscaledTime >= _nextStatsAt)
                {
                    _nextStatsAt = Time.unscaledTime + 60f;
                    LogInfo("[Reader] stats " + Reader.StatsLine());
                }
            }
            catch { }
        }

        private void OnDestroy()
        {
            try { Reader.Shutdown(); } catch { }
            try { Speech.Shutdown(); } catch { }
            try { LilithMenu.Reset(); } catch { }
            try { LilithBodyNav.Reset(); } catch { }
            try { LilithApi.Reset(); } catch { }
            try { _harmony?.UnpatchSelf(); } catch { }
            try { A11yHost.Clear(); } catch { }
        }

        // ====================================================================
        // 日志小工具（A11yHost.Log 在 OnDestroy 之后可能被清空，这里自己兜一层）
        // ====================================================================

        private static void LogInfo(string msg)
        {
            try { A11yHost.Log?.LogInfo(msg); } catch { }
        }

        private static void LogWarn(string msg)
        {
            try { A11yHost.Log?.LogWarning(msg); } catch { }
        }

        private static void LogError(string msg)
        {
            try { A11yHost.Log?.LogError(msg); } catch { }
        }
    }
}


