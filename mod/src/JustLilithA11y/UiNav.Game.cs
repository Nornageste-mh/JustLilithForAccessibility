using System;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace JustLilithA11y
{
    /// <summary>
    /// 界面导航的「逐作层」补丁面。
    ///
    /// 平台层（UiNav.cs）已经定义了扩展点，本文件只填空：
    ///   · NameAlias        —— 对象名 → 中文名（平台层 line 920 / 935 直连查表）
    ///   · InputFieldRoleName(inf) —— 输入框的角色名（平台层 line 1039 直连调用）
    ///   · GameTextIsNoise  —— 预制体占位字（平台层 line 888，经 A11yHost 钩子转过来）
    ///   · GameLabelOf      —— 控件的可读标签（平台层 line 973 / 1045，经 A11yHost 钩子）
    ///   · GameGroupName    —— 面板组名（平台层 line 1109，经 A11yHost 钩子）
    ///
    /// === 本作实查依据（D:\DSHWorkSpace\justlilith_a11y\probe\decomp\src）===
    ///   · 设置面板是 UI.TraySettingNew 体系：TraySettingNewView（tabs + _settingItemRoot）、
    ///     TraySettingTabItem（_titleText 是 TMP，标题由 RefreshTitle() 写入译文）、
    ///     各设置项是 UI.TraySettingNew.SettingItems / .SpecialItems 下的 Setting*Item（MonoBehaviour）。
    ///   · 标签文字走 Unity Localization：SettingLocalizedText.Apply(TMP_Text label, string table,
    ///     string localizationKey)（SettingLocalizedText.cs:299）把译文写进 TMP。
    ///     → **运行时读 TMP 的 text 就是译文**；但译文异步到达之前 TMP 里可能还是 key 或空，
    ///       所以这里对「长得像 key」的串一律拒绝（TextProc.LooksLikeLocalizationKey）。
    ///       **绝不把 localization key / ID 念给玩家。**
    ///   · 莉莉丝面板是 SettingView（glassChangeButton / hatChangeButton / topMostButton /
    ///     giftButton / quitGameButton / _closeButton / waitSayLilith）。
    ///   · 反编译里**没有** NameInputManager / surnameField1 / nameField1 之类的命名输入管理器
    ///     （全文检索无命中），所以上一作遗留的「姓氏/名字」反射块在这里删掉，不写没依据的死代码。
    /// </summary>
    internal static partial class UiNav
    {
        // =====================================================================
        // 对象名 → 中文名
        // =====================================================================
        //
        // 键是 **GameObject.name**（平台层 AliasOnSelf/AliasAncestorOf 直接拿 transform 的名字查表）。
        // 每条都写明出处；没有出处的宁可不填 —— 查不到会退回「同行文字 → 对象名」，
        // 不会再出现英文名以外的问题。
        //
        // 出处：SettingView.cs:260-264 的**字段名**（编辑器里这些 Button 字段对应的对象名通常同名）。
        //
        // 注意（实机踩过的坑）：这个表用 StringComparer.OrdinalIgnoreCase，
        // 所以**任何两行只差大小写就等于重复键**，静态构造会抛
        // ArgumentException("An item with the same key has already been added")，
        // 整个 UiNav 类初始化失败 → 导航 100% 不可用（日志里是 TypeInitializationException）。
        // 实机曾因同时写了 "closeButton" 与 "CloseButton" 而炸；现已删掉小写那条，
        // 保留下划线版（"_closeButton"）与 Unity 默认命名版（"CloseButton"）各一条。
        // 以后加行请先在本表里按忽略大小写搜一遍。
        private static readonly Dictionary<string, string> NameAlias =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                // ---- 莉莉丝面板（SettingView.cs:260-265）----
                { "glassChangeButton",  "更换眼镜" },
                { "hatChangeButton",    "更换帽子" },
                { "topMostButton",      "窗口置顶" },
                { "giftButton",         "礼物" },
                { "quitGameButton",     "退出游戏" },
                { "waitSayLilith",      "等莉莉丝说完" },
                { "_closeButton",       "关闭" },

                // ---- 通用按钮名（Unity 默认命名，命中即用；不命中无副作用）----
                { "CloseButton",        "关闭" },
                { "YesButton",          "是" },
                { "NoButton",           "否" },
                { "OKButton",           "确定" },
                { "ConfirmButton",      "确定" },
                { "CancelButton",       "取消" },
                { "BackButton",         "返回" },
                { "NextButton",         "下一步" },
                { "PrevButton",         "上一步" },

                // ---- 系统托盘（ShowSystemTray.cs / SystemTray.cs 体系）----
                { "SettingButton",      "设置" },
                { "TraySettingButton",  "设置" },
            };

        // =====================================================================
        // 输入框角色名
        // =====================================================================

        /// <summary>
        /// 输入框的「这个框是填什么的」。本作反编译里没有命名输入管理器，
        /// 设置面板里的输入框都挂在 Setting*Item 上，所以先按设置项标签找；
        /// 找不到返回空串 —— 平台层会接着问 GameLabelOf 钩子、再退占位提示，顺序不会乱。
        /// </summary>
        private static string InputFieldRoleName(TMP_InputField inf)
        {
            try
            {
                if (inf == null) return "";
                string s = SettingItemLabelOf(inf.transform);
                if (s.Length > 0) return s;
            }
            catch { }
            return "";
        }

        // =====================================================================
        // 噪声：预制体占位字
        // =====================================================================

        /// <summary>
        /// 这些是预制体里的占位/类型名，念出来对玩家毫无信息量。
        /// 依据：Unity 新建 UI 的默认文本 + 本作设置项预制体里常见的模板字样。
        /// 命中返回 true = 不念。
        /// </summary>
        private static readonly HashSet<string> PlaceholderTexts =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Text", "Label", "Button", "Item", "Slider", "Toggle", "Dropdown", "InputField",
                "New Text", "New Text (Legacy)", "Placeholder", "Title", "Value", "Content",
                "Image", "Icon", "Panel", "Root", "Group", "Container", "Template",
                "XX", "XXX", "XXXX", "000", "0000", "00", "---", "…", "...",
                "TODO", "TBD", "Lorem ipsum", "Sample", "Example",
            };

        /// <summary>平台层 Noise() 每取一段文本都会问一次。</summary>
        internal static bool GameTextIsNoise(string s)
        {
            if (string.IsNullOrEmpty(s)) return true;
            string t = TextProc.Normalize(s);
            if (t.Length == 0) return true;
            if (PlaceholderTexts.Contains(t)) return true;
            // key 形态（例如 "settings.tab_display"）同样不该进朗读队列
            if (TextProc.LooksLikeLocalizationKey(t)) return true;
            return IsNumericJunk(t);
        }

        /// <summary>
        /// 纯数字垃圾（预制体占位文本）。
        ///
        /// 实机依据（2026-10-22 19:2x 日志，导航模式在 PlayerLine 组里念出过）：
        ///   `112，按钮。16 / 16`、`123123213132，按钮。13 / 16`
        ///   —— 这两个都是预制体 Button 上的占位数字，念出来只会误导。
        /// 但**不能一刀切**把数字都杀掉：音量/页数这类真实数值是有意义的短数字。
        /// 所以规则是：
        ///   · 1~2 位的纯数字放行（"0"/"12"/"50" 可能是数值）
        ///   · ≥3 位纯数字、或数字有明显重复模式 → 判为占位垃圾
        /// 含任何非数字字符时不做判断（交给别的过滤）。
        /// </summary>
        private static bool IsNumericJunk(string t)
        {
            try
            {
                bool allDigits = true;
                for (int i = 0; i < t.Length; i++)
                {
                    char c = t[i];
                    if (c < '0' || c > '9') { allDigits = false; break; }
                }
                if (!allDigits) return false;
                if (t.Length >= 3) return true;
                return false;
            }
            catch { return false; }
        }

        // =====================================================================
        // 控件标签
        // =====================================================================

        /// <summary>
        /// 控件的可读标签（平台层在「自己子树 → 钩子 → 同行兄弟」这一层调用，
        /// 也就是**有覆盖权**：返回非空就直接用它）。
        ///
        /// 本作只在「设置项 / 设置页签 / 面板按钮」这三类上有把握，其余一律返回空串，
        /// 让平台层原本的三层回退照旧工作 —— 不乱猜是这里的第一原则。
        /// </summary>
        internal static string GameLabelOf(Selectable s)
        {
            try
            {
                if (s == null) return "";
                Transform t = s.transform;
                string path = PathOfNode(t, 10);

                // 00) 身体部位导航组（LilithBodyNav.cs 造的隐藏虚拟按钮）：
                //     对象名 `LilithPart_Head` / `LilithPart_HandLeft` …
                //     这些按钮的子树只有一个全透明 Image（故意没有任何文字），
                //     所以标签只能由这里给，绝不能让平台层回退到对象名（会念成「LilithPart_Head」）。
                string partName = t.gameObject.name;
                if (partName.StartsWith(LilithApi.PartPrefix, StringComparison.Ordinal))
                {
                    string partLabel = LilithBodyNav.LabelOfSuffix(
                        partName.Substring(LilithApi.PartPrefix.Length));
                    if (partLabel.Length > 0) return partLabel;
                }

                // 0a) 日历格：`…/Item_Birthday(Clone)/Calendar/bg/CalendarView/…/DateCell (41)`
                //     实机证据（task-9 终验）：生日展开日历后扫到 65 项，日历格念成「14，按钮」。
                //     这类控件的可读名不是纯数字，而是「14 日」。只处理路径含 /Calendar/ 且
                //     对象名以 DateCell 开头的节点 —— 不扩散到别的纯数字控件（音量/页数不能加「日」）。
                //     ★ 当前**不会被执行到**：平台层 UiNav.cs:1214-1215 先跑 OwnTextOf，own 非空
                //       就直接返回「来源=own 质量=Bound」，钩子（1217-1231）只在 own 为空时才调用。
                //       DateCell 的 own 正是「14」→ 实机上仍是「14，按钮」。保留本分支是为了
                //       将来平台层加「逐作 refine」钩子后能立刻生效（见 DateCellLabel 的注释）。
                string dateCell = DateCellLabel(t, path);
                if (dateCell.Length > 0) return dateCell;

                // 0b) 生日行的 SwitchBtn 是**展开器**，不是开关。
                //     实机证据：子物体 switchIcon / dateText 常驻 active，回车后
                //     `Calendar/bg/CalendarView` 由 False→True。所以念「你的生日，展开日历/收起日历」，
                //     彻底避开「开关，开/关」这种错误语义（用户对语义错误的容忍度最低）。
                string exp = ExpanderLabel(t, path);
                if (exp.Length > 0) return exp;

                // 0c) 选择文件夹按钮：**必须优先于行标题**。
                //     实机证据：「我」tab 的 `Item_EditBox(Clone)/Btn_FolderSelect` 被行标题覆盖成
                //     「你的名字，按钮」，但它真实含义是「选择文件夹」（声音 tab 里同名控件是对的）。
                string folder = FolderAliasOf(t);
                if (folder.Length > 0) return folder;

                // 0) 开关类按钮：`Item_Switch(Clone)/Btn_Swtich_Off` / `Btn_Swtich_On`
                //    实机日志证据：`[UiNav] 未命名控件：原始名=Btn_Swtich_Off
                //    路径=TrayCanvas/TraySettingNew/ViewPanel/SettingView/Item_Switch(Clone)/Btn_Swtich_Off`
                //    这类控件的可读标签 = **同一行上的 text_Name** + 开/关状态。
                //    状态优先看对象名后缀（_On/_Off 是游戏自己的两个精灵按钮，同时只有一个启用），
                //    其次看行内 Toggle 的 isOn；都读不到就只说「<文本>，开关」。
                string sw = SwitchRowLabel(t);
                if (sw.Length > 0) return sw;

                // 1) 输入框：先把「行标题」捞出来，绝不让平台层念成「未命名输入框」。
                //    实机证据：`…/SettingView/Item_InputField(Clone)/InputField (TMP)` 被念成
                //    「未命名输入框，第 11 项，共 14 项，面板设置面板，当前内容 Nornageste」——
                //    值念对了、名字没念出来，说明平台层的「子树文字→钩子→同行兄弟」三层都没命中行标题。
                //    这里直接按「输入框自己 → 父行 → 再上两层」找 text_Name / 短中文标签。
                TMP_InputField inf = null;
                try { inf = s.GetComponent<TMP_InputField>(); } catch { }
                if (inf != null)
                {
                    string inputLabel = InputRowLabel(inf);
                    if (inputLabel.Length > 0) return inputLabel;
                }

                // 2) 设置页签：TraySettingTabItem（标题在 _titleText 里，由 RefreshTitle 写译文）
                string tab = SettingItemLabelOf(t, "TraySettingTabItem");
                if (tab.Length > 0) return tab;

                // 3) 设置项：Setting*Item（标签 TMP 在 item 子树里）
                string item = SettingItemLabelOf(t);
                if (item.Length > 0) return item;

                // 4) 图标按钮：按对象名查别名表（别名全部有实机日志/反编译取证，见表上方注释）
                string alias = IconAliasOf(t);
                if (alias.Length > 0) return alias;

                // 5) 其余一律返回空，让平台层原本的回退链照旧工作 —— 不猜。
            }
            catch { }
            return "";
        }

        /// <summary>
        /// 日历格的标签：「14 日」。
        /// 依据：task-9 终验实测 `Item_Birthday(Clone)/Calendar/bg/CalendarView/…/DateCell (41)`
        /// 被念成「14，按钮」——玩家听不出这是日期格，还是页码、音量或数量。
        /// 只对「路径含 /Calendar/ 且对象名以 DateCell 开头」的节点生效；
        /// 文本已经是「14日」这类就不再重复加「日」。
        ///
        /// ★★ 当前**暂不生效**（task-9 终验定论，2026-10-22）：
        ///   平台层 UiNav.cs:1214-1215 的取值顺序是 `OwnTextOf` 先跑，读到自己子树里的文本就直接
        ///   返回「来源=own 质量=Bound」，`A11yHost.GameLabelOf`（UiNav.cs:1217-1231）只在 own 为空时
        ///   才轮到。DateCell 的 own 文本正是「14」，所以本方法**永远没被调用**——实机日志
        ///   `来源=own 质量=Bound` + 念「14，按钮」可证。
        ///   把 GameLabelOf 提到 OwnTextOf 之前会改变平台层对所有控件的标签优先级（影响其它作品），
        ///   属高风险变更，本轮不做。本规则**保留**：将来平台层若加「逐作 refine」钩子（在 own 之后
        ///   仍允许逐作改写标签），本方法即可直接接上去生效，届时只需加一次调用，无需重写逻辑。
        ///   → 现状请在文档「已知限制」里按「日历格念『14，按钮』，缺『日』语义」记录。
        /// </summary>
        private static string DateCellLabel(Transform t, string path)
        {
            try
            {
                if (t == null) return "";
                if (path.IndexOf("/Calendar/", StringComparison.OrdinalIgnoreCase) < 0) return "";

                string nm = "";
                try { nm = t.gameObject.name ?? ""; } catch { }
                if (!nm.StartsWith("DateCell", StringComparison.OrdinalIgnoreCase)) return "";

                string raw = OwnTextOf(t);
                if (raw.Length == 0) return "";
                if (raw.IndexOf('日') >= 0) return raw;                 // 已经是「14日」
                if (raw.IndexOf('月') >= 0) return raw;                 // 万一是「10月」

                // 只有纯数字才好加「日」；混着文字（如「今天」）就原样念。
                bool allDigit = raw.Length > 0;
                for (int i = 0; i < raw.Length; i++)
                {
                    if (raw[i] < '0' || raw[i] > '9') { allDigit = false; break; }
                }
                if (allDigit && raw.Length <= 2) return raw + "日";
                return raw;
            }
            catch { return ""; }
        }

        /// <summary>
        /// 生日行的展开器：`念「行标题，展开日历 / 收起日历」`。
        /// 依据：实测 SwitchBtn 的子物体 switchIcon / dateText 常驻 active，
        /// 回车后 `Calendar/bg/CalendarView` 由 False→True —— 它是折叠面板的开关，不是开关控件。
        /// </summary>
        private static string ExpanderLabel(Transform t, string path)
        {
            try
            {
                if (t == null) return "";
                if (path.IndexOf("Item_Birthday", StringComparison.OrdinalIgnoreCase) < 0) return "";

                string nm = "";
                try { nm = t.gameObject.name ?? ""; } catch { }
                if (nm.IndexOf("Swtich", StringComparison.OrdinalIgnoreCase) < 0
                    && nm.IndexOf("Switch", StringComparison.OrdinalIgnoreCase) < 0) return "";

                // 行标题（你的生日）：往上找 Item_ 行，再取 text_Name
                Transform row = t;
                for (int i = 0; i < 3 && row != null; i++)
                {
                    string gn = "";
                    try { gn = row.gameObject.name ?? ""; } catch { }
                    if (gn.StartsWith("Item_", StringComparison.OrdinalIgnoreCase)) break;
                    row = row.parent;
                }
                string title = BestLabelTextIn(row != null ? row : t);
                if (title.Length == 0) title = "生日";

                bool open = false;
                try
                {
                    if (row != null)
                    {
                        Transform[] all = row.GetComponentsInChildren<Transform>(true);
                        for (int i = 0; i < all.Length; i++)
                        {
                            Transform c = all[i];
                            if (c == null) continue;
                            string cn = "";
                            try { cn = c.gameObject.name ?? ""; } catch { }
                            if (cn.IndexOf("CalendarView", StringComparison.OrdinalIgnoreCase) < 0) continue;
                            open = c.gameObject.activeInHierarchy;
                            break;
                        }
                    }
                }
                catch { }

                // 平台层会自己补「，按钮」，这里只给语义。
                return title + "，" + (open ? "收起日历" : "展开日历");
            }
            catch { return ""; }
        }

        /// <summary>对象名含 Folder / FolderSelect 时优先念「选择文件夹」，优先于行标题。</summary>
        private static string FolderAliasOf(Transform t)
        {
            try
            {
                Transform cur = t;
                for (int i = 0; i < 3 && cur != null; i++)
                {
                    string nm = "";
                    try { nm = cur.gameObject.name ?? ""; } catch { }
                    if (nm.IndexOf("Folder", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        string a;
                        if (IconAliases.TryGetValue(nm, out a) && a.Length > 0) return a;
                        if (nm.IndexOf("FolderSelect", StringComparison.OrdinalIgnoreCase) >= 0
                            || nm.IndexOf("Folder", StringComparison.OrdinalIgnoreCase) >= 0) return "选择文件夹";
                    }
                    cur = cur.parent;
                }
            }
            catch { }
            return "";
        }

        /// <summary>控件自己（或直接子物体里第一个）的可见文本，已 Clean。找不到返回空串。</summary>
        private static string OwnTextOf(Transform t)
        {
            try
            {
                TMP_Text tmp = null;
                try { tmp = t.GetComponent<TMP_Text>(); } catch { }
                if (tmp == null)
                {
                    try { tmp = t.GetComponentInChildren<TMP_Text>(false); } catch { }
                }
                if (tmp == null) return "";
                string s = TextProc.Clean(tmp.text);
                return s;
            }
            catch { return ""; }
        }

        /// <summary>从根到该节点的路径（最多 max 层），用于按路径判定语义（日历/生日行等）。</summary>
        private static string PathOfNode(Transform t, int max)
        {
            try
            {
                if (t == null) return "";
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                Transform cur = t;
                int guard = 0;
                while (cur != null && guard < max)
                {
                    if (sb.Length > 0) sb.Insert(0, "/");
                    sb.Insert(0, cur.gameObject.name ?? "?");
                    cur = cur.parent;
                    guard++;
                }
                return sb.ToString();
            }
            catch { return ""; }
        }

        /// <summary>
        /// 输入框的行标题。
        ///
        /// 实机证据（task-9 复验）：「我」tab 的 `Item_InputField(Clone)/InputField (TMP)` 被念成
        /// 「未命名输入框，第 11 项，共 14 项，面板设置面板，当前内容 Nornageste」——
        /// 值念对了（平台层自己读了 text），名字没念出来（那三层回退都没命中行上的 text_Name）。
        ///
        /// 做法：从输入框自身往上最多 4 层，逐层只看**这一层的直接子物体**（不递归到更深），
        /// 找名字含 text_Name / Name / Title 的 TMP，其次找这一层里唯一的短中文 TMP。
        /// 关键排除：**输入框自身子树里的文本一律不算标签**（那是用户输入的内容）。
        /// </summary>
        private static string InputRowLabel(TMP_InputField inf)
        {
            try
            {
                Transform inputT = inf.transform;
                Transform row = inputT;

                for (int up = 0; up < 4 && row != null; up++)
                {
                    // ① 这一层里名字带 Name/Title 的 TMP（设置项统一用 text_Name）
                    string byName = FindLabelUnder(row, inputT, true);
                    if (byName.Length > 0) return byName;

                    // ② 这一层里第一个「像人话」的短中文 TMP（排除输入框子树）
                    string byText = FindLabelUnder(row, inputT, false);
                    if (byText.Length > 0) return byText;

                    row = row.parent;
                }
            }
            catch { }
            return "";
        }

        /// <summary>
        /// 在 row 这一层（row 自己 + 它的直接子物体）找可读标签。
        /// nameOnly=true 时只认名字含 "text_Name"/"Name"/"Title" 的 TMP。
        /// 永远跳过 skipRoot（输入框自身）及其子树。
        /// </summary>
        private static string FindLabelUnder(Transform row, Transform skipRoot, bool nameOnly)
        {
            try
            {
                if (row == null) return "";

                for (int i = -1; i < row.childCount; i++)
                {
                    Transform c = (i < 0) ? row : row.GetChild(i);
                    if (c == null) continue;
                    if (skipRoot != null && (ReferenceEquals(c, skipRoot) || IsSelfOrDescendant(c, skipRoot))) continue;

                    TMP_Text tmp = null;
                    try { tmp = c.GetComponent<TMP_Text>(); } catch { }
                    string s = ReadLabelTmp(tmp, nameOnly);
                    if (s.Length > 0) return s;

                    // 子物体本身没有 TMP 的话，再看它的一层子物体（Item_Switch 的 text_Name 就在下一层）
                    if (c == row) continue;
                    int n = c.childCount;
                    for (int k = 0; k < n; k++)
                    {
                        Transform g = c.GetChild(k);
                        if (g == null) continue;
                        if (skipRoot != null && (ReferenceEquals(g, skipRoot) || IsSelfOrDescendant(g, skipRoot))) continue;
                        TMP_Text gt = null;
                        try { gt = g.GetComponent<TMP_Text>(); } catch { }
                        string gs = ReadLabelTmp(gt, nameOnly);
                        if (gs.Length > 0) return gs;
                    }
                }
            }
            catch { }
            return "";
        }

        private static string ReadLabelTmp(TMP_Text tmp, bool nameOnly)
        {
            try
            {
                if (tmp == null) return "";
                if (nameOnly)
                {
                    string nm = "";
                    try { nm = tmp.gameObject.name ?? ""; } catch { }
                    bool nameish = nm.IndexOf("text_Name", StringComparison.OrdinalIgnoreCase) >= 0
                                || nm.IndexOf("Name", StringComparison.OrdinalIgnoreCase) >= 0
                                || nm.IndexOf("Title", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!nameish) return "";
                }

                string raw = "";
                try { raw = tmp.text; } catch { return ""; }
                string s = TextProc.Clean(raw);
                if (s.Length == 0) return "";
                if (PlaceholderTexts.Contains(s)) return "";
                if (TextProc.LooksLikeLocalizationKey(s)) return "";   // 绝不念 Tray_UI_xx
                if (!HasCjkText(s)) return "";
                if (s.Length > 40) return "";
                return s;
            }
            catch { return ""; }
        }

        /// <summary>t 是否等于 root 或 root 的后代（IL2CPP 下 Transform.IsChildOf 可用，但仍套 try/catch）。</summary>
        private static bool IsSelfOrDescendant(Transform t, Transform root)
        {
            try
            {
                Transform cur = t;
                for (int i = 0; i < 16 && cur != null; i++)
                {
                    if (ReferenceEquals(cur, root)) return true;
                    cur = cur.parent;
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// 开关行按钮的标签：
        ///   找 1~3 层内父行（名字含 "Item_"）上的 text_Name 文本 → "游戏时不显示，开关，关"。
        /// 依据：实机日志 `Item_Switch(Clone)/Btn_Swtich_Off`、`Item_PaperTip(Clone)/Btn_Swtich_On`
        /// 都挂在 Setting*Item 行里，行上的名称标签是 `text_Name`（同 BestLabelTextIn 的取证）。
        /// </summary>
        private static string SwitchRowLabel(Transform btn)
        {
            try
            {
                string nm = "";
                try { nm = btn.gameObject.name ?? ""; } catch { }
                bool isSwitch = nm.IndexOf("Swtich", StringComparison.OrdinalIgnoreCase) >= 0
                             || nm.IndexOf("Switch", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!isSwitch) return "";

                Transform row = btn;
                for (int up = 0; up < 3 && row != null; up++)
                {
                    string rn = "";
                    try { rn = row.gameObject.name ?? ""; } catch { }
                    if (rn.IndexOf("Item_", StringComparison.OrdinalIgnoreCase) >= 0) break;
                    row = row.parent;
                }
                if (row == null) return "";

                string name = BestLabelTextIn(row);
                if (name.Length == 0) return "";

                string state = SwitchStateOf(btn, row, nm);
                if (state.Length == 0) return name + "，开关";
                return name + "，开关，" + state;
            }
            catch { return ""; }
        }

        /// <summary>
        /// 开关状态：① 按钮对象名后缀 _On/_Off（游戏用两个精灵按钮表示两个状态）
        /// ② 行内 Toggle.isOn ③ 按钮下 Image.sprite 名含 On/Off。
        /// 读不到返回空串（调用方只说「开关」，不编状态）。
        /// </summary>
        private static string SwitchStateOf(Transform btn, Transform row, string btnName)
        {
            try
            {
                if (btnName.EndsWith("_On", StringComparison.OrdinalIgnoreCase)) return "开";
                if (btnName.EndsWith("_Off", StringComparison.OrdinalIgnoreCase)) return "关";
            }
            catch { }

            try
            {
                Toggle tg = null;
                try { tg = row.GetComponentInChildren<Toggle>(true); } catch { }
                if (tg != null) return tg.isOn ? "开" : "关";
            }
            catch { }

            try
            {
                Image img = null;
                try { img = btn.GetComponent<Image>(); } catch { }
                if (img != null && img.sprite != null)
                {
                    string sn = img.sprite.name ?? "";
                    if (sn.IndexOf("_on", StringComparison.OrdinalIgnoreCase) >= 0) return "开";
                    if (sn.IndexOf("_off", StringComparison.OrdinalIgnoreCase) >= 0) return "关";
                }
            }
            catch { }

            // ③ 行内「状态图标子物体」：名字像 On/Off 的节点，看哪个是 active。
            //    依据（task-9 复验日志）：`Item_Birthday(Clone)/Calendar/bg/SwitchBtn` 被念成
            //    「你的生日，开关，按钮」——它既没有 _On/_Off 后缀，行内也没有 Toggle，
            //    精灵名也不含 on/off，但这类开关的两个状态图标是同级子物体，只有一个 active。
            //    注意匹配必须**按词边界**：不能拿 "on" 做子串匹配，否则 "Button" 会被当成 On。
            try
            {
                int hint = ActiveStateIcon(row != null ? row : btn, 0);
                if (hint > 0) return "开";
                if (hint < 0) return "关";
            }
            catch { }

            return "";
        }

        /// <summary>
        /// 在节点子树（最多 3 层）里找「名字是 On / Off / Checkmark / Selected 这种状态图标」，
        /// 返回 1=开、-1=关、0=判断不了。只看 activeInHierarchy 的节点。
        /// </summary>
        private static int ActiveStateIcon(Transform root, int depth)
        {
            if (root == null || depth > 3) return 0;
            int onActive = 0, offActive = 0;

            for (int i = 0; i < root.childCount; i++)
            {
                Transform c = null;
                try { c = root.GetChild(i); } catch { continue; }
                if (c == null) continue;

                string nm = "";
                try { nm = c.gameObject.name ?? ""; } catch { }
                int hint = OnOffHint(nm);
                bool act = false;
                try { act = c.gameObject.activeInHierarchy; } catch { }

                if (hint > 0 && act) onActive++;
                if (hint < 0 && act) offActive++;

                int sub = ActiveStateIcon(c, depth + 1);
                if (sub > 0) onActive++;
                else if (sub < 0) offActive++;
            }

            if (onActive > 0 && offActive == 0) return 1;
            if (offActive > 0 && onActive == 0) return -1;
            return 0;
        }

        /// <summary>
        /// 对象名是不是「开 / 关」状态图标。1=开、-1=关、0=都不是。
        /// 只认完整词或 _on/_off / 后缀，避免 "Button"(含 on) 被误判。
        /// </summary>
        private static int OnOffHint(string nm)
        {
            try
            {
                if (string.IsNullOrEmpty(nm)) return 0;
                string s = nm.ToLowerInvariant();

                bool on = s == "on" || s == "checkmark" || s == "selected" || s == "onicon"
                          || s.EndsWith("_on") || s.EndsWith("on_icon") || s.EndsWith("_checkmark")
                          || s.EndsWith("_selected") || s.EndsWith("switch_on") || s.EndsWith("toggle_on");
                bool off = s == "off" || s == "officon"
                           || s.EndsWith("_off") || s.EndsWith("off_icon")
                           || s.EndsWith("switch_off") || s.EndsWith("toggle_off");

                if (on && !off) return 1;
                if (off && !on) return -1;
                return 0;
            }
            catch { return 0; }
        }

        /// <summary>
        /// 图标按钮的「对象名 → 中文」别名表。
        /// 别名全部来自实机导航日志里被报成「未命名控件」的对象名 + 其路径所处的面板语义；
        /// 没有依据的一律不写（宁可让平台层去推断并要确认，也不编一个错名字）。
        /// </summary>
        private static readonly Dictionary<string, string> IconAliases =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                // 设置面板右上角关闭（日志：TrayCanvas/TraySettingNew/ViewPanel/Btn_Close）
                { "Btn_Close", "关闭" },
                // 音乐/时钟/计时面板的关闭（日志：MusicCtrlPanel/BtnClose、TimeClock/SettingPanel/BtnClose 等）
                { "BtnClose", "关闭" },
                { "Btn_Add", "添加" },                  // TimeClock / TimeCounter 的 SavedTimeList 新增
                { "Btn_FolderSelect", "选择文件夹" },     // 设置项 Item_EditBox 的路径选择
                { "PlayPauseButton", "播放暂停" },        // MusicCtrlPanel/Buttons
                { "PlayModeButton", "播放模式" },         // MusicPlayModeItem 是同一个功能的设置项
                { "StopButton", "停止" },                // MusicCtrlPanel/Buttons
                { "MusicListEntry", "播放列表" },         // MusicCtrlPanel/Buttons
                { "SwitchBtn", "切换" },                 // Item_Birthday(Clone)/Calendar/bg/SwitchBtn
            };

        private static string IconAliasOf(Transform t)
        {
            try
            {
                for (int up = 0; up < 4 && t != null; up++)
                {
                    string n = "";
                    try { n = t.gameObject.name ?? ""; } catch { }
                    string hit;
                    if (n.Length > 0 && IconAliases.TryGetValue(n, out hit)) return hit;
                    t = t.parent;
                }
            }
            catch { }
            return "";
        }

        /// <summary>
        /// 从控件往上找设置项容器（Setting*Item / TraySettingTabItem），
        /// 再把容器子树里有意义的 TMP 文本当成标签。
        /// 只认「含汉字的文本」：数值、英文 key、占位字都不算标签。
        /// </summary>
        private static string SettingItemLabelOf(Transform from) { return SettingItemLabelOf(from, null); }

        private static string SettingItemLabelOf(Transform from, string exactTypeName)
        {
            try
            {
                if (from == null) return "";
                Transform t = from;
                for (int up = 0; up < 7 && t != null; up++)
                {
                    string tn = "";
                    try
                    {
                        Component[] cs = t.GetComponents<Component>();
                        for (int i = 0; i < cs.Length; i++)
                        {
                            Component c = cs[i];
                            if (c == null) continue;
                            string n = c.GetType().Name;
                            if (exactTypeName != null)
                            {
                                if (n == exactTypeName) { tn = n; break; }
                                continue;
                            }
                            if (n.StartsWith("Setting", StringComparison.Ordinal) && n.EndsWith("Item", StringComparison.Ordinal))
                            {
                                tn = n;
                                break;
                            }
                        }
                    }
                    catch { }

                    // 兜底：行容器按**对象名**认（实机里行名就是 `Item_Switch(Clone)` /
                    // `Item_InputField(Clone)` / `Item_PaperTip(Clone)`）。
                    // 为什么需要这条：任务实测「我」tab 的 InputField 行组件类型并不叫 Setting*Item
                    // （否则 GameLabelOf 早就拿到 text_Name 了），但行对象名一定带 `Item_`。
                    // exactTypeName != null 时（TraySettingTabItem 精确匹配）不走这条，避免误认。
                    if (tn.Length == 0 && exactTypeName == null)
                    {
                        string gn = "";
                        try { gn = t.gameObject.name ?? ""; } catch { }
                        if (gn.StartsWith("Item_", StringComparison.OrdinalIgnoreCase)) tn = gn;
                    }

                    if (tn.Length > 0) return BestLabelTextIn(t);
                    t = t.parent;
                }
            }
            catch { }
            return "";
        }

        /// <summary>子树里第一个「像人话」的文本：含汉字、不是占位、不是 key。</summary>
        private static string BestLabelTextIn(Transform root)
        {
            try
            {
                TMP_Text[] tmps = root.GetComponentsInChildren<TMP_Text>(true);

                // 先认「名字里带 text_Name」的那个 TMP —— 实机实测设置项的名称标签就挂在
                // `Item_Switch(Clone)/text_Name`、`Item_ButtonBIg(Clone)/ButtonBIg/text_Name` 上。
                // 关键：它若是未解析的 key（实测就是 Tray_UI_35 这种）或占位，
                // 就**直接判为「这个设置项的标签读不出来」并返回空**，绝不再退而求其次
                // 去拿同一子树里的「值文本」（开 / 关 / 50% …）当标签 —— 那会把状态念成名字。
                for (int i = 0; i < tmps.Length; i++)
                {
                    TMP_Text tmp = tmps[i];
                    if (tmp == null) continue;
                    string nm = "";
                    try { nm = tmp.gameObject.name ?? ""; } catch { }
                    if (nm.IndexOf("text_Name", StringComparison.OrdinalIgnoreCase) < 0) continue;

                    string raw = "";
                    try { raw = tmp.text; } catch { continue; }
                    string s = TextProc.Clean(raw);
                    if (s.Length == 0) return "";
                    if (PlaceholderTexts.Contains(s)) return "";
                    if (TextProc.LooksLikeLocalizationKey(s)) return "";   // 绝不念 Tray_UI_xx
                    if (!HasCjkText(s)) return "";
                    if (s.Length > 40) return "";
                    return s;
                }

                // 没有 text_Name 标记的常规设置项/页签：整个子树里找第一个像人话的中文短文本。
                for (int i = 0; i < tmps.Length; i++)
                {
                    TMP_Text tmp = tmps[i];
                    if (tmp == null) continue;
                    string raw = "";
                    try { raw = tmp.text; } catch { continue; }
                    string s = TextProc.Clean(raw);
                    if (s.Length == 0) continue;
                    if (PlaceholderTexts.Contains(s)) continue;
                    if (TextProc.LooksLikeLocalizationKey(s)) continue;
                    if (!HasCjkText(s)) continue;      // 纯数值/英文不算标签
                    if (s.Length > 40) continue;   // 长段多半是说明文字，不是控件名
                    return s;
                }
            }
            catch { }
            return "";
        }

        private static bool HasCjkText(string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if ((c >= '\u4E00' && c <= '\u9FFF') || (c >= '\u3040' && c <= '\u30FF')) return true;
            }
            return false;
        }

        // =====================================================================
        // 面板组名
        // =====================================================================

        /// <summary>
        /// 面板组的中文名。平台层默认念根对象名（"Canvas/XXX"），
        /// 本作把几个主面板翻成中文，其余返回空串由平台层回退。
        /// </summary>
        internal static string GameGroupName(Transform root)
        {
            try
            {
                if (root == null) return "";
                string n = root.name ?? "";
                if (n.Length == 0) return "";

                // ⓪ 身体部位导航组：LilithBodyNav 造的隐藏 Canvas（见 LilithBodyNav.cs）。
                //    它不在游戏自己的面板体系里，名字也带不了「中文」，所以必须在这里映射。
                //    注意：平台层拿到的组根是**容器** "LilithA11yBodyNavContainer"，
                //    不是 Canvas 的 "LilithA11yBodyNav"（2026-10-23 实机日志证实），故用前缀匹配。
                if (IsBodyNavRoot(n))
                    return LilithApi.BodyGroupName;

                // 先去掉 "View" / "(Clone)" / 空格这类后缀再比对，
                // 依据：实机日志里平台层拿到的原始名是 **TraySettingNew**（不是 TraySettingNewView），
                // 而旧规则只按 "TraySettingNewView" 匹配 → 全部落到「面板1..7」兜底。
                string key = NormalizePanelName(n);

                // ① 精确匹配（按实机日志里的原始名，去后缀后全等）
                switch (key)
                {
                    case "TraySettingNew": return "设置面板";
                    case "PlayerLine": return "选项面板";
                    case "Setting": return "功能菜单";
                    case "MusicPlayer": return "音乐播放器";
                    case "TimeClock": return "时钟";
                    case "TimeCounter": return "计时";
                    case "DialogueBubble": return "对话";
                    case "GiftView": return "礼物";
                    case "SystemTray": return "系统托盘";
                    case "StartMenu": return "标题画面";
                    case "TitleMenu": return "标题画面";
                    case "Memo": return "备忘录";
                    case "Todo": return "待办";
                    case "Closet": return "衣柜";
                    case "Stash": return "收纳";
                    case "Chat": return "聊天";
                    case "Gallery": return "画廊";
                }

                // ② 包含匹配（兼容 "Xxx Panel" / "XxxRoot" 这类命名）
                if (Contains(n, "TraySettingNew")) return "设置";
                if (Contains(n, "SettingView")) return "莉莉丝面板";
                if (Contains(n, "GiftView")) return "礼物";
                if (Contains(n, "SystemTray")) return "系统托盘";
                if (Contains(n, "StartMenu") || Contains(n, "TitleMenu")) return "标题画面";
                if (Contains(n, "Memo")) return "备忘录";
                if (Contains(n, "Todo")) return "待办";
                if (Contains(n, "Closet")) return "衣柜";
                if (Contains(n, "Chat")) return "聊天";
                if (Contains(n, "Gallery")) return "画廊";
                if (Contains(n, "Dialog") || Contains(n, "Dialogue")) return "对话";
            }
            catch { }
            return "";
        }

        /// <summary>
        /// 面板组的排序优先级（数值小的排前面；平台层默认 100）。
        ///
        /// 本作给「莉莉丝交互」一个很小的值：用户主要想先摸莉莉丝本人，
        /// 而不是先翻设置面板，所以身体部位组排在前面（PageUp/PageDown 换组时第一组就是它）。
        /// 其余面板返回 100 = 不干预排序。
        /// </summary>
        internal static int GameGroupPriority(Transform root)
        {
            try
            {
                if (root == null) return 100;
                if (IsBodyNavRoot(root.name)) return 20;
            }
            catch { }
            return 100;
        }

        /// <summary>组根是否属于身体部位导航组（Canvas 本体或它下面的容器）。</summary>
        private static bool IsBodyNavRoot(string n)
        {
            return n != null && n.IndexOf(LilithApi.BodyCanvasName, StringComparison.Ordinal) == 0;
        }

        /// <summary>把 "TraySettingNewView" / "PlayerLine (Clone)" / "XxxView" 归一成面板基名。</summary>
        private static string NormalizePanelName(string n)
        {
            try
            {
                string s = n.Trim();
                // 去掉 Unity 的 "(Clone)" / " (1)" 这类实例后缀
                int paren = s.IndexOf('(');
                if (paren > 0) s = s.Substring(0, paren).Trim();
                if (s.EndsWith("View", StringComparison.OrdinalIgnoreCase) && s.Length > 4)
                    s = s.Substring(0, s.Length - 4);
                return s;
            }
            catch { return n; }
        }

        private static bool Contains(string hay, string needle)
        {
            return hay.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
