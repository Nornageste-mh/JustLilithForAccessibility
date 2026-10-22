using System;
using System.Text.RegularExpressions;

namespace JustLilithA11y
{
    /// <summary>
    /// 台词 / 界面文本的清洗。
    ///
    /// === 纪律（对本作尤其重要）===
    ///   1) 这里只做「把屏幕上的视觉文本变成适合朗读的文本」，**不改剧情文字内容**。
    ///      不替换词、不删句、不「润色」。
    ///   2) 每条正则都要写明依据（哪个类、哪个字段决定了文本长这样）。
    ///      没有依据的形态不猜 —— 拿不准时把原文念出来，比念错好。
    ///   3) 清洗必须是**幂等**的：Clean(Clean(x)) == Clean(x)。
    ///      因为同一条文本可能先经事件路径、再经轮询路径进来。
    ///
    /// === 本作实查依据 ===
    ///   对话气泡正文来自 DialogueBubbleUI._dialogueText（TextMeshProUGUI，DialogueBubbleUI.cs:323）。
    ///   即：文本是 TMP 富文本，可能带 &lt;color&gt;/&lt;size&gt;/&lt;b&gt; 之类的标签，
    ///   以及 &lt;sprite&gt; 图标占位（TMP 的 sprite asset 写法）。这些都不是「字」。
    ///   反编译里未发现 ruby / 注音标签（检索 ruby|&lt;r= 只命中备忘录/聊天类 UI 的 TMP_Text，
    ///   不是对话正文的富文本实现），所以这里不做注音展开 —— 若实机日志里出现注音形态，再补规则。
    /// </summary>
    internal static class TextProc
    {
        /// <summary>
        /// TMP 标签全形态。依据：TMP 的富文本一律是 &lt;...&gt;（成对标签或自闭合标签），
        /// 内容只用于排版/着色，念出来是噪声。范围限定 1..64 字符且不允许内含 &lt; &gt; 换行，
        /// 避免把「a &lt; b &gt; c」这种正文里的数学比较误当标签整段删掉。
        /// </summary>
        private static readonly Regex TagRe =
            new Regex(@"<[^<>\r\n]{1,64}>", RegexOptions.Compiled);

        /// <summary>
        /// 表情 / 图标占位：&lt;sprite=...&gt;、&lt;sprite name="..."&gt;、&lt;sprite index=3&gt;。
        /// 依据：TMP sprite asset 的写法；画面上是图，没有可念的字。
        /// （先于 TagRe 处理只是为了日志里能区分，最终两者都清掉。）
        /// </summary>
        private static readonly Regex SpriteRe =
            new Regex(@"<sprite[^<>]{0,64}>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>控制字符（除 Tab）与零宽字符。依据：TMP 里偶发的排版控制符、复制粘贴残留。</summary>
        private static readonly Regex ControlRe =
            new Regex(@"[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F\u200B-\u200F\uFEFF]", RegexOptions.Compiled);

        /// <summary>两个以上连续空格（半角/全角/不换行）压成一个。依据：中文排版里常见对齐用的多空格。</summary>
        private static readonly Regex SpaceRe =
            new Regex(@"[ \t\u3000\u00A0]{2,}", RegexOptions.Compiled);

        /// <summary>藏在正文里的「动作/声音提示」圆括号：形如（笑）（叹气）。依据：暂未实查确认，先不删。</summary>
        // 说明：本作 DialogueNode 有 emotion（string）字段，情绪更可能是**数据**而不是写在 text 里。
        // 所以这里故意**不**删括号内容 —— 宁可多念，也不要漏掉作者写在台词里的信息。

        /// <summary>说话人名的包裹符号：TMP 标签之外的「【】「」[]（）」等装饰。</summary>
        private static readonly Regex SpeakerWrapperRe =
            new Regex(@"^[\s【\[（(「『""'']+|[\s】\]）)」』""''：:]+$", RegexOptions.Compiled);

        // ================= 对外接口 =================

        /// <summary>
        /// 说话人名清洗：去掉包裹符号、末尾冒号、首尾空白。
        /// DialogueNode.speaker 是 string（DialogueNode.cs:70）。实机日志会打印它的真实取值，
        /// 若发现它是英文 key（如 "lilith"），应在 Reader 里映射成中文名 —— 但那要有日志依据，
        /// 这里**不凭空造映射表**。
        /// </summary>
        public static string Speaker(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            string s = ControlRe.Replace(raw.Replace("\r", "").Replace("\n", " "), "");
            s = TagRe.Replace(s, "");
            s = s.Trim();
            // 去掉首尾的包裹符号（【】/「」/[]/（）等），循环处理「【「名」】」这种嵌套
            for (int i = 0; i < 4; i++)
            {
                string t = SpeakerWrapperRe.Replace(s, "");
                if (t.Length == s.Length) break;
                s = t.Trim();
            }
            return s;
        }

        /// <summary>
        /// 正文清洗。可对同一文本重复调用（幂等）。
        /// 顺序：去 CR → 换行转空格 → 清控制符 → 清 sprite 占位 → 清 TMP 标签 → 压空白 → Trim。
        /// </summary>
        public static string Clean(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";

            string s = raw.Replace("\r\n", "\n").Replace('\r', '\n');
            // 换行在朗读里应当是停顿而不是消失，这里用一个空格代表停顿边界。
            s = s.Replace('\n', ' ');
            s = ControlRe.Replace(s, "");
            s = SpriteRe.Replace(s, "");
            s = TagRe.Replace(s, "");
            s = SpaceRe.Replace(s, " ");
            s = s.Replace('\t', ' ');
            return s.Trim();
        }

        /// <summary>
        /// 这段文本值不值得念。
        /// 判定：清洗后仍有至少一个「有意义」字符（汉字/假名/字母/数字），
        /// 纯标点、纯空白、纯符号占位不念（例如只剩下 “……” 的空行）。
        /// </summary>
        public static bool ShouldRead(string raw)
        {
            string s = Clean(raw);
            if (s.Length == 0) return false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (char.IsLetterOrDigit(c)) return true;
            }
            return false;
        }

        /// <summary>
        /// 这段文字像不像「本地化 key / 资源 ID」—— 界面层禁用。
        ///
        /// 依据：本作设置项标签走 Unity Localization：SettingLocalizedText.Apply(TMP_Text label,
        /// string table, string localizationKey)（SettingLocalizedText.cs:299）会把译文写进 TMP。
        /// 也就是说**运行时 TMP 里正常是译文**；但若某帧还没等到译文（asyncArrived 之前），
        /// TMP 里会短暂留着 key 或空。念出 "settings.tab_display" 这种串对玩家毫无意义。
        ///
        /// 判定规则（保守）：
        ///   - 含汉字/假名 → 不可能是 key，放行；
        ///   - 否则若是「标识符形态」：只由字母/数字/下划线/点/连字符/斜杠组成，且出现 '.'、'_'、'/'，
        ///     或长度 &gt; 16 且不是普通英文单词短语（含空格视为短语，不拦）→ 判为 key。
        /// </summary>
        public static bool LooksLikeLocalizationKey(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            bool hasCjk = false;
            bool hasSpace = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == ' ' || c == '\u3000') { hasSpace = true; continue; }
                if ((c >= '\u4E00' && c <= '\u9FFF') || (c >= '\u3040' && c <= '\u30FF')) { hasCjk = true; break; }
            }
            if (hasCjk) return false;
            if (hasSpace) return false;   // 带空格的英文短语可能是真文案（例如 "Press Space"）

            // 实机实测的 key 前綴（设置面板未解析时 TMP 里就是这些）：Tray_UI_35 / Tray_UI_06 …
            // 只按前綴拦，不做「猜意义」的模糊匹配，免得把正常的英文按钮文案拦掉。
            if (StartsWithOrdinalIgnoreCase(s, "Tray_UI") ||
                StartsWithOrdinalIgnoreCase(s, "Tray_") ||
                StartsWithOrdinalIgnoreCase(s, "UI.") ||
                StartsWithOrdinalIgnoreCase(s, "Lilith_") ||
                StartsWithOrdinalIgnoreCase(s, "Setting_") ||
                StartsWithOrdinalIgnoreCase(s, "Item_") ||
                StartsWithOrdinalIgnoreCase(s, "Label_") ||
                StartsWithOrdinalIgnoreCase(s, "Title_"))
            {
                return true;
            }

            int letters = 0;
            bool dot = false, underscore = false, slash = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '.') { dot = true; continue; }
                if (c == '_') { underscore = true; continue; }
                if (c == '/' || c == '\\') { slash = true; continue; }
                if (c == '-') continue;
                if (char.IsLetterOrDigit(c)) { letters++; continue; }
                return false;             // 出现奇怪字符，不像 key
            }
            if (letters == 0) return false;
            if (dot || underscore || slash) return true;
            return s.Length > 16;         // 单段超长标识符（无空格）也按 key 处理
        }

        private static bool StartsWithOrdinalIgnoreCase(string s, string prefix)
        {
            return s.Length >= prefix.Length &&
                   string.Compare(s, 0, prefix, 0, prefix.Length, StringComparison.OrdinalIgnoreCase) == 0;
        }

        /// <summary>
        /// 界面文本归一化：调用方（UiNav 平台层）已做 Norm/TMP 取文本，
        /// 这里只补一条：把全角空格与不换行空格统一成半角空格，便于后续比较。
        /// </summary>
        public static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return SpaceRe.Replace(s.Replace('\u3000', ' ').Replace('\u00A0', ' '), " ").Trim();
        }
    }
}
