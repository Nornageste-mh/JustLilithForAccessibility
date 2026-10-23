using System;
using System.Collections.Generic;

namespace JustLilithA11y
{
    /// <summary>
    /// 选项朗读 + 数字键选择。
    ///
    /// === 本作实查依据 ===
    ///   · 剧情内选项：DialogueNode.HasOptions（DialogueNode.cs:319）/ node.options（234）
    ///     → DialogueOption.text（DialogueOption.cs:17）/ nextId（31）
    ///     选中：DialogueManager.SelectOption(int optionIndex)（DialogueManager.cs:3760），**0 基**
    ///   · 玩家台词选项（Lilith 说完后玩家选话）：PlayerLineController.IsAnyOptionsShowing（4170）
    ///     → _texts（3357）读标签；选中：PlayerLineController.OnButtonClicked(int index)（5366），0 基
    ///
    /// === 三条硬纪律 ===
    ///   1) **不删选项、不改选项数据**：只读 text 并复核现有列表，一个都不动。
    ///   2) **不碰游戏计时**：本类不调用 SkipDialogue / CompleteCurrentNode，也不改任何 duration。
    ///      超时了游戏自己走，我们只会让「选中」这条通路多一个数字键入口。
    ///   3) 数字键只在「当前确实有待选列表」时吃键（1..9），其余时候完全不拦。
    /// </summary>
    internal static class Choices
    {
        private const string LogPrefix = "[Choices]";
        private const string BufferTag = "lilith.choices";
        private const int MaxSlots = 9;

        private static readonly List<string> _labels = new List<string>(MaxSlots);
        private static Action<int> _select;          // 入参 1 基
        private static string _tag = "";
        private static string _fingerprint = "";
        private static float _shownAt;
        private static int _announcedSuffix = 0;

        internal static bool HasPending { get { return _labels.Count > 0 && _select != null; } }
        internal static int Count { get { return _labels.Count; } }
        internal static string CurrentFingerprint { get { return _fingerprint; } }

        // =====================================================================

        /// <summary>
        /// 给出一份待选列表并朗读编号。标签已由调用方清洗过（TextProc.Clean）。
        /// tag 用来区分来源（"node" 剧情选项 / "plc" 玩家台词选项），切换来源时自动清掉旧的。
        /// </summary>
        internal static void Show(List<string> labels, string tag, Action<int> selectOneBased)
        {
            try
            {
                if (labels == null || labels.Count == 0 || selectOneBased == null) { Clear(); return; }

                // 同一份列表重复喂进来（轮询每帧都会喂）→ 不重复朗读
                string fp = tag + "#" + string.Join("|", labels.ToArray());
                if (fp == _fingerprint) return;

                _labels.Clear();
                for (int i = 0; i < labels.Count && _labels.Count < MaxSlots; i++)
                {
                    string s = labels[i] ?? "";
                    _labels.Add(s);
                }
                _tag = tag ?? "";
                _select = selectOneBased;
                _fingerprint = fp;
                _shownAt = UnityEngine.Time.unscaledTime;
                _announcedSuffix = 0;

                SpeakList();
                A11yHost.Diag(LogPrefix + " shown " + _labels.Count + " options from " + _tag);
            }
            catch (Exception e) { LogOnce("show", e); }
        }

        /// <summary>
        /// 拼「有 N 个选项。选项 1：xx；选项 2：yy。按数字键 1 到 N 选择。」
        ///
        /// 为什么用「选项 N：」这种重复前缀：读屏用户是按编号记的，
        /// 前置编号能让「1、你为什么…」和「2、我为什么…」在听觉上拉开，不会连成一串。
        /// </summary>
        private static void SpeakList()
        {
            int n = _labels.Count;
            var sb = new System.Text.StringBuilder();
            sb.Append('有').Append(n).Append("个选项。");
            for (int i = 0; i < n; i++)
            {
                if (i > 0) sb.Append('；');
                sb.Append("选项 ").Append(i + 1).Append('：').Append(_labels[i]);
            }
            sb.Append('。');
            if (n == 1) sb.Append("按数字键 1 选择。");
            else sb.Append("按数字键 1 到 ").Append(n).Append(" 选择。");

            string text = sb.ToString();
            bool read = true;
            try { if (Plugin.CfgReadChoices != null) read = Plugin.CfgReadChoices.Value; } catch { }

            if (read) Repeat.Push(text, BufferTag, true);   // 打断上一句：选项比台词更需要立刻听到
            else Repeat.Note(text);
        }

        /// <summary>每帧：吃数字键。由 Plugin 的 TickUiNav 调用（导航模式开关无关，选项随时可点）。</summary>
        internal static void Update()
        {
            try
            {
                if (!HasPending) return;

                bool keys = true;
                try { if (Plugin.CfgChoiceHotkeys != null) keys = Plugin.CfgChoiceHotkeys.Value; } catch { }
                if (!keys) return;

                // 列表放出来已经超过 N 秒 → 认为这次对话早就走了，别让陈旧列表继续吃数字键。
                // 默认 60 秒；打开「对话/选项不自动消失」后放宽到 10 分钟 ——
                // 因为气泡不再自动消失时，玩家可能盯着同一组选项想很久（用户需求 m01949 ③）。
                float expire = 60f;
                try { if (Plugin.CfgBubblePersist != null && Plugin.CfgBubblePersist.Value) expire = 600f; } catch { }
                if (UnityEngine.Time.unscaledTime - _shownAt > expire)
                {
                    A11yHost.Diag(LogPrefix + " 待选列表超时，自动作废。");
                    Clear();
                    return;
                }

                int slots = Math.Min(MaxSlots, _labels.Count);

                // ★ 焦点在输入框里时不抢数字键：设置面板的生日/昵称等输入框要能正常打字，
                //   不能因为「上一个对话还留着待选列表」就把玩家输入的 "2" 当成选项。
                if (IsTypingInInputField()) return;

                for (int i = 1; i <= slots; i++)
                {
                    if (!KeyEdge.DigitPressed(i)) continue;
                    Choose(i);
                    return;
                }
            }
            catch (Exception e) { LogOnce("update", e); }
        }

        /// <summary>焦点是否在可输入文本框里（TMP_InputField / 旧版 InputField）。</summary>
        private static bool IsTypingInInputField()
        {
            try
            {
                UnityEngine.EventSystems.EventSystem es = UnityEngine.EventSystems.EventSystem.current;
                if (es == null) return false;
                UnityEngine.GameObject go = es.currentSelectedGameObject;
                if (go == null) return false;
                if (go.GetComponent<TMPro.TMP_InputField>() != null) return true;
                if (go.GetComponent<UnityEngine.UI.InputField>() != null) return true;
                return false;
            }
            catch { return false; }
        }

        /// <summary>1 基选择。越界给提示，不做事。</summary>
        internal static void Choose(int oneBased)
        {
            try
            {
                if (!HasPending)
                {
                    Repeat.Say("现在没有选项。", true);
                    return;
                }
                if (oneBased < 1 || oneBased > _labels.Count)
                {
                    Repeat.Say("没有第 " + oneBased + " 个选项。", true);
                    return;
                }

                string label = _labels[oneBased - 1];
                Action<int> cb = _select;
                int index = oneBased;

                // 先播报再清列表再回调：清完列表后 if 回调里又立刻弹新选项，编号不会被旧播报盖住
                Repeat.Say("已选择：" + label, true);
                Clear();
                try { if (cb != null) cb(index); }
                catch (Exception e) { LogOnce("callback", e); }
            }
            catch (Exception e) { LogOnce("choose", e); }
        }

        /// <summary>
        /// 沉默键（默认 0）：放弃这一次选择。
        /// **只清我们自己的待选列表**，绝不去动游戏的倒计时/计时器 ——
        /// 让它自己走到超时的沉默分支，分支结果才和原版一致。
        /// </summary>
        internal static void ChooseSilence()
        {
            try
            {
                if (!HasPending)
                {
                    Repeat.Say("现在没有选项。", true);
                    return;
                }
                Repeat.Say("沉默。", true);
                A11yHost.Diag(LogPrefix + " silence (tag=" + _tag + ", n=" + _labels.Count + ")");
                Clear();
            }
            catch (Exception e) { LogOnce("silence", e); }
        }

        internal static void Clear()
        {
            _labels.Clear();
            _select = null;
            _tag = "";
            _fingerprint = "";
            try { Repeat.Drop(BufferTag); } catch { }
        }

        /// <summary>只清某个来源的列表（例如玩家台词选项收起了，但剧情选项还在）。</summary>
        internal static void ClearIfTag(string tag)
        {
            try
            {
                if (!HasPending) return;
                if (!string.Equals(_tag, tag, StringComparison.Ordinal)) return;
                Clear();
            }
            catch { }
        }

        // =====================================================================

        private static readonly HashSet<string> _loggedErrors = new HashSet<string>();

        private static void LogOnce(string where, Exception e)
        {
            try
            {
                if (!_loggedErrors.Add(where)) return;
                A11yHost.Log?.LogWarning(LogPrefix + " " + where + " 异常（同类只记一次）：" + e.GetType().Name + ": " + e.Message);
            }
            catch { }
        }
    }
}
