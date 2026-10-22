using System;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace JustLilithA11y
{
    /// <summary>
    /// 屏幕浮动文本监视器：把**不经过 DialogueManager** 的台词也念出来。
    ///
    /// 为什么需要（Lead 实机探针证据，2026-10-22）：
    ///   · 开场「记忆相框」的逐句字幕（"哥哥。" / "哥哥你还真是个孤独的人呢。" /
    ///     "让您失望了？" / "没有哦，我最喜欢哥哥了。" / "谢谢。"）**不属于任何 DialogueNode**。
    ///   · 探针对全部 TMP（含 inactive）与 legacy Text 做了关键字全量扫描：kw-tmp hits=0、
    ///     legacy-text count=0 —— 这些字是运行时动态写进某个 TMP、显示完又被清空的。
    ///   · 机制：OpeningPerformanceManager 的 cue（OpeningPerformanceCue{animationName,text,soundId}），
    ///     文本来自 OpeningLineDatabase.GetText(key, language)，由 ShowTextEventName 动画事件触发显示。
    ///   · 同理 TrayCanvas/ScreenCenterTips/Tip_Banner/Text (TMP) 这类提示条也不走对话系统。
    ///
    /// 做法：每 0.22 秒扫一次**可见**的 TMP_Text，新出现的文本走 Repeat（与台词同一条通路）。
    ///
    /// 四条纪律（缺一条就会灾难）：
    ///   ① 只念**可见**的：activeInHierarchy + 父链 CanvasGroup.alpha &gt; 0.05；
    ///   ② 过滤占位字 / localization key / 纯数字（倒计时、百分比、时间），否则设置面板与
    ///      性能显示会刷屏；
    ///   ③ 排除**已由其它模块负责**的子树：DialogueBubbleUI（气泡台词）、PlayerLineController
    ///      （台词选项）、任何带 Selectable 祖先的控件（导航标签）—— 否则一句台词念两遍；
    ///   ④ 去重：**文本消失前只念一次**（维护「上一帧可见集合」），消失后再出现要过 30 秒才重念。
    ///      没有 ④ 的话一个常驻提示条会每 30 秒念一次，而且开场字幕重播时会重复念。
    /// </summary>
    internal static class ScreenText
    {
        private const string LogPrefix = "[ScreenText]";

        /// <summary>扫描间隔：0.22 秒（约 4.5 次/秒）。开场字幕每句停留远超这个值，够用。</summary>
        private const float ScanEvery = 0.22f;

        /// <summary>同一句话消失后再次出现的静默期。</summary>
        private const float RepeatWindow = 30f;

        /// <summary>超过这个长度的「界面文本」多半是被错误抓到的正文，不念（只在日志里提示）。</summary>
        private const int MaxChars = 160;

        private static float _nextScanAt;

        /// <summary>上一帧可见的文本集合：用来区分「新出现」和「一直在」。</summary>
        private static readonly HashSet<string> _live = new HashSet<string>();

        /// <summary>最近念过的文本 → 念的时刻（用完的清掉，集合不会无限长）。</summary>
        private static readonly Dictionary<string, float> _lastSpoken = new Dictionary<string, float>();

        // ---- 日志节流 ----
        private static int _loggedItems;
        private static int _totalSpoken;
        private static readonly HashSet<string> _loggedErrors = new HashSet<string>();

        // ---- 性能：复用缓冲，不每帧 new ----
        private static readonly List<string> _seenThisScan = new List<string>(64);
        private static readonly StringBuilder _pathSb = new StringBuilder(128);

        internal static void Update()
        {
            try
            {
                if (Plugin.CfgScreenText == null || !Plugin.CfgScreenText.Value) return;

                float now = Time.unscaledTime;
                if (now < _nextScanAt) return;
                _nextScanAt = now + ScanEvery;

                // ⑤ 导航模式期间**只记账、不朗读**。
                //
                // 实机问题：打开设置面板那一刻按 Ctrl+Alt+Plus 进导航，浮动文本通道恰好把
                // 面板上「游戏时不显示 / 游戏时自动穿透 / 单击穿透 / 跨屏拖拽 / 关闭移动 / 开机启动 / 置于顶层」
                // 七条 text_Name 一次性念了出来，和导航自己的「设置面板，共 17 项」抢话，
                // 用户听到的是两路语音互相打断。
                //
                // 为什么不是 return：如果这里直接返回，_live 就停在进入导航前的快照上，
                // 退出导航后屏幕上那些「本来就一直在」的文字会被当成新出现再念一遍。
                // 所以照常扫描、照常更新 _live / _lastSpoken 基线，只把 Speak 这一步关掉；
                // 这样退出导航时既不会补念旧文本，之后真正新出现的提示也仍然能念。
                //
                // 判据用 UiNav.Active（UiNav.cs:169 的 public static bool Active），
                // 是平台层唯一公开的「导航模式是否开着」状态，不需要反射也不需要新增钩子。
                bool speak = true;
                try { speak = !UiNav.Active; } catch { }

                Scan(now, speak);
            }
            catch (Exception e) { LogOnce("update", e); }
        }

        private static void Scan(float now, bool speak)
        {
            TMP_Text[] all;
            try { all = Resources.FindObjectsOfTypeAll<TMP_Text>(); }
            catch (Exception e) { LogOnce("find", e); return; }
            if (all == null) return;

            _seenThisScan.Clear();

            for (int i = 0; i < all.Length; i++)
            {
                TMP_Text t = all[i];
                if (t == null) continue;

                try
                {
                    if (!t.gameObject.activeInHierarchy) continue;

                    string raw;
                    try { raw = t.text; } catch { continue; }
                    if (string.IsNullOrEmpty(raw)) continue;

                    string text = TextProc.Clean(raw);
                    if (!TextProc.ShouldRead(text)) continue;

                    // ② 占位字 / localization key / 纯数字
                    if (IsNoise(text)) continue;

                    // ① 可见性（父链 CanvasGroup）
                    if (!IsVisible(t.transform)) continue;

                    // ③ 已被其它模块负责的子树
                    if (IsOwnedByOtherModule(t.transform)) continue;
                    if (HasSelectableAncestor(t.transform)) continue;

                    _seenThisScan.Add(text);

                    // 导航模式期间：当作「已经念过」，只更新基线，不出声。
                    if (!speak)
                    {
                        _lastSpoken[text] = now;
                        continue;
                    }

                    // ④ 去重：上一帧就在的不重念
                    if (_live.Contains(text)) continue;

                    float prevAt;
                    if (_lastSpoken.TryGetValue(text, out prevAt) && now - prevAt < RepeatWindow) continue;

                    Speak(text, now, PathOf(t.transform));
                }
                catch (Exception e) { LogOnce("item", e); }
            }

            // 用本次可见集合替换 _live（顺手清理 30 秒前的记录）
            _live.Clear();
            for (int i = 0; i < _seenThisScan.Count; i++) _live.Add(_seenThisScan[i]);
            PruneSpoken(now);
        }

        private static void Speak(string text, float now, string path)
        {
            if (text.Length > MaxChars)
            {
                A11yHost.Diag(LogPrefix + " skip too long (" + text.Length + " chars) src=" + path);
                return;
            }

            _lastSpoken[text] = now;
            _totalSpoken++;

            if (_loggedItems < 20)
            {
                _loggedItems++;
                Log("new='" + text + "' src=" + path);
            }
            else
            {
                A11yHost.Diag(LogPrefix + " new='" + text + "' src=" + path + " (#" + _totalSpoken + ")");
            }

            // interrupt:false —— 不许把正在播的台词/语音掐掉，界面浮动文字排在后面说
            try { Repeat.Say(text, false); }
            catch (Exception e) { LogOnce("speak", e); }
        }

        // =====================================================================
        // 过滤
        // =====================================================================

        private static bool IsNoise(string text)
        {
            try
            {
                // 与导航层同一套：占位字 + localization key（Tray_UI_xx 之类）
                Func<string, bool> hook = A11yHost.GameTextIsNoise;
                if (hook != null)
                {
                    if (hook(text)) return true;
                }
                else if (UiNav.GameTextIsNoise(text)) return true;

                if (TextProc.LooksLikeLocalizationKey(text)) return true;

                // 纯数字 / 时间 / 百分比 / 分数（倒计时、性能文本、进度）
                if (IsNumbersOnly(text)) return true;

                return false;
            }
            catch { return true; }   // 判不了就当作噪声：宁可不念，也不要刷屏
        }

        /// <summary>纯数字类文本：3、12:34、45%、1/8、3.5s、-20、+10%…</summary>
        private static bool IsNumbersOnly(string s)
        {
            try
            {
                bool hasDigit = false;
                for (int i = 0; i < s.Length; i++)
                {
                    char c = s[i];
                    if (char.IsDigit(c)) { hasDigit = true; continue; }
                    if (char.IsWhiteSpace(c)) continue;
                    if (c == ':' || c == '：' || c == '/' || c == '.' || c == ',' ||
                        c == '%' || c == '~' || c == '+' || c == '-' || c == 'x' || c == 'X' ||
                        c == 's' || c == 'S' || c == 'm' || c == 'M')
                        continue;
                    return false;   // 出现任何别的字符（含汉字）→ 不是纯数字
                }
                return hasDigit;
            }
            catch { return true; }
        }

        /// <summary>向上最多 12 层判可见：activeInHierarchy + 途经的 CanvasGroup.alpha。</summary>
        private static bool IsVisible(Transform t)
        {
            try
            {
                Transform cur = t;
                int guard = 0;
                while (cur != null && guard++ < 12)
                {
                    if (!cur.gameObject.activeInHierarchy) return false;
                    CanvasGroup cg = cur.GetComponent<CanvasGroup>();
                    if (cg != null && cg.alpha <= 0.05f) return false;
                    cur = cur.parent;
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// 这个 TMP 是不是在「别人已经负责朗读」的子树里：
        ///   DialogueBubbleUI  → Reader 的气泡台词
        ///   PlayerLineController → Reader 的台词选项
        /// 排除掉，避免同一句台词念两遍。
        /// </summary>
        private static bool IsOwnedByOtherModule(Transform t)
        {
            try
            {
                Transform cur = t;
                int guard = 0;
                while (cur != null && guard++ < 12)
                {
                    if (cur.GetComponent<DialogueBubbleUI>() != null) return true;
                    if (cur.GetComponent<PlayerLineController>() != null) return true;
                    cur = cur.parent;
                }
                return false;
            }
            catch { return false; }
        }

        /// <summary>
        /// 带 Selectable 祖先（按钮/开关/滑条/输入框及其标签）的文字归导航层讲，这里不抢。
        /// 这条同时把整个设置面板的 item 标签挡在外面。
        /// </summary>
        private static bool HasSelectableAncestor(Transform t)
        {
            try
            {
                Transform cur = t;
                int guard = 0;
                while (cur != null && guard++ < 8)
                {
                    if (cur.GetComponent<Selectable>() != null) return true;
                    cur = cur.parent;
                }
                return false;
            }
            catch { return false; }
        }

        // =====================================================================
        // 工具
        // =====================================================================

        /// <summary>路径字符串（日志用，例如 `Lilith/Canvas/DialogueBubble/Text`）。</summary>
        private static string PathOf(Transform t)
        {
            try
            {
                _pathSb.Length = 0;
                Transform cur = t;
                int guard = 0;
                while (cur != null && guard++ < 8)
                {
                    if (_pathSb.Length > 0) _pathSb.Insert(0, '/');
                    _pathSb.Insert(0, cur.name);
                    cur = cur.parent;
                }
                return _pathSb.ToString();
            }
            catch { return "?"; }
        }

        private static void PruneSpoken(float now)
        {
            try
            {
                if (_lastSpoken.Count < 64) return;

                List<string> dead = null;
                foreach (KeyValuePair<string, float> kv in _lastSpoken)
                {
                    if (now - kv.Value < RepeatWindow) continue;
                    if (dead == null) dead = new List<string>();
                    dead.Add(kv.Key);
                }
                if (dead != null)
                {
                    for (int i = 0; i < dead.Count; i++) _lastSpoken.Remove(dead[i]);
                }
            }
            catch { }
        }

        /// <summary>插件卸载/换场景时清空（避免旧画面文本残留导致新画面文字被当成「一直在」而不念）。</summary>
        internal static void Reset()
        {
            try
            {
                _live.Clear();
                _lastSpoken.Clear();
                _nextScanAt = 0f;
            }
            catch { }
        }

        private static void Log(string msg)
        {
            try { A11yHost.Log?.LogInfo(LogPrefix + " " + msg); } catch { }
        }

        private static void LogOnce(string where, Exception e)
        {
            try
            {
                if (!_loggedErrors.Add(where)) return;
                A11yHost.Log?.LogWarning(LogPrefix + " " + where + " 异常（同类只记一次）: " +
                                        e.GetType().Name + ": " + e.Message);
            }
            catch { }
        }
    }
}
