using System;
using System.Reflection;
using TMPro;
using UnityEngine;

namespace JustLilithA11y
{
    /// <summary>
    /// 对话朗读。
    ///
    /// === 为什么是「事件优先 + 轮询兜底」两条路一起写 ===
    /// DialogueManager 有四个**实例**事件（实查签名，全在 DialogueManager.cs）：
    ///   Il2CppSystem.Action&lt;DialogueNode&gt; OnDialogueStart      (2559)
    ///   Il2CppSystem.Action&lt;DialogueNode&gt; OnDialogueAdvance    (2574)
    ///   Il2CppSystem.Action                 OnDialogueEnd         (2604)
    ///   Il2CppSystem.Action&lt;DialogueOption&gt; OnOptionSelected  (2619)
    /// 订阅它们最省、最准。但 IL2CPP 下「把托管委托塞进 il2cpp 委托字段」这件事
    /// 尚未在本作实机验证过，而且**改错会顶掉游戏自己的订阅者**
    /// （DialogueBubbleUI/PlayerLineController 也在监听），一旦顶掉，气泡就不刷新了。
    /// 所以这里的做法是：
    ///   1) 用 Il2CppInterop 的 DelegateSupport 把托管委托转成原生委托；
    ///   2) 用 Delegate.Combine 把**原有订阅者保留在前面**再写回；
    ///   3) 写回后立刻用 GetInvocationList 数一遍：条目数必须 ≥ 原有条目数 + 1，
    ///      否则判定「这个游戏/这套 interop 不支持托管订阅」→ **把原值写回去**并永久关闭事件路径；
    ///   4) 无论事件成不成，轮询路径一直开着。两条路都靠「文本去重」避免重复朗读。
    ///
    /// === 轮询读哪里 ===
    /// 节点：DialogueManager.IsDialogueActive(3072) / CurrentNode(3116)。
    /// 正文兜底：DialogueBubbleUI._dialogueText（TextMeshProUGUI，DialogueBubbleUI.cs:323）。
    ///   为什么要兜底：DialogueNode.text 是**节点文本**，而游戏里有的台词是运行时用
    ///   DialogueManager.Say(text, ...)（3705）直接灌进气泡的，末必对应一个 node.text。
    ///   气泡里的 TMP 文本才是玩家真正看到的东西，所以当 node.text 拿不到时读它。
    ///   读之前必须等打字机停（DialogueManager.IsBubbleTyping()，3551），否则会读到半句话。
    ///
    /// === 有配音就不重复朗读（依据）===
    /// DialogueManager.ResolveNodeSoundId(DialogueNode)（3979）是游戏自己解析配音 id 的函数；
    /// 另有 DialogueLineRepository.TryGetVoiceSoundId(int lineId, out string soundId)（190）
    /// 按台词行号查配音库。两者任一给得出非空 soundId，就认为「游戏会播语音」→ 只进重读缓冲不朗读。
    /// 三条都失败（异常/查询不到）才算「判定不了」，此时按 Plugin.CfgSpeakWhenUnknown 的意愿朗读。
    /// </summary>
    internal static class Reader
    {
        private const string LogPrefix = "[Reader]";
        private const string ChoicesTag = "lilith.choices";

        // ---- 引用 ----
        private static DialogueManager _dm;
        private static DialogueBubbleUI _bubble;
        private static PlayerLineController _plc;
        private static float _nextScanAt;
        private static int _scanCount;

        // ---- 事件路径状态 ----
        // IL2CPP interop 里事件被生成为 add_Xxx / remove_Xxx **方法**（没有同名属性），
        // 且 add_ 是原生「追加」语义 —— 所以不需要读旧委托、不需要 Combine、也不需要还原。
        // 只要记住「挂到了哪个实例」：实例换了（场景重载 / 单例重建）就重挂一次。
        private static bool _eventsTried;
        private static bool _eventsOn;
        private static DialogueManager _hookedDm;
        private static object _cbStart, _cbAdvance, _cbEnd, _cbOption;


        // ---- 去重 / 进度 ----
        private static string _lastKey = "";
        private static float _lastKeyAt;
        private static int _lastNodeId = int.MinValue;
        private static int _lastSpokenNodeId = int.MinValue;
        private static string _lastSpokenText = "";
        private static string _pendingPlcFingerprint = "";
        private static float _plcDiagAt;                 // 选项诊断日志节流（每秒最多一条）
        private static string _lastSpeakerName = "";
        private static string _lastNodeTextClean = "";

        // ---- 统计（日志用，实机校准配音判定）----
        private static int _lines, _linesVoiced, _linesRead, _linesUnknown;
        private static int _speakerLogs;
        private static readonly System.Collections.Generic.HashSet<string> _speakersSeen =
            new System.Collections.Generic.HashSet<string>();

        internal static bool EventsActive { get { return _eventsOn; } }
        internal static int LineCount { get { return _lines; } }

        // =====================================================================
        // 每帧
        // =====================================================================

        internal static void Update()
        {
            try
            {
                EnsureRefs();
                if (_dm == null) return;

                bool active = false;
                try { active = _dm.IsDialogueActive; } catch { }
                if (!active)
                {
                    if (_lastNodeId != int.MinValue)
                    {
                        _lastNodeId = int.MinValue;
                        OnDialogueEnded();
                    }
                    return;
                }

                PollNode();
                PollBubbleText();
                PollPlayerLineOptions();
            }
            catch (Exception e) { LogOnce("update", e); }
        }

        /// <summary>抓引用。找不到就每隔 2 秒再试（场景切换时对象会重建）。</summary>
        private static void EnsureRefs()
        {
            float now = Time.unscaledTime;
            if (_dm != null && _bubble != null && _plc != null) return;
            if (now < _nextScanAt) return;
            _nextScanAt = now + 2f;
            _scanCount++;

            try
            {
                if (_dm == null)
                {
                    _dm = UnityEngine.Object.FindObjectOfType<DialogueManager>();
                    if (_dm != null)
                    {
                        Log("找到 DialogueManager（第 " + _scanCount + " 次扫描）。");
                        // 场景重载 / 单例重建时，旧实例上的订阅会随之消失；
                        // 这里解开「只试一次」的闩锁，让新实例上重新挂一遍。
                        _eventsTried = false;
                        _eventsOn = false;
                        _hookedDm = null;
                        TryInstallEvents();
                    }
                }
            }
            catch (Exception e) { LogOnce("find-dm", e); }

            try
            {
                if (_bubble == null) _bubble = UnityEngine.Object.FindObjectOfType<DialogueBubbleUI>();
            }
            catch (Exception e) { LogOnce("find-bubble", e); }

            try
            {
                if (_plc == null) _plc = UnityEngine.Object.FindObjectOfType<PlayerLineController>();
            }
            catch (Exception e) { LogOnce("find-plc", e); }
        }

        // =====================================================================
        // 路径一：事件订阅
        //
        // 实机教训（Lead 探针验证，2026-10-22）：
        //   IL2CPP interop 把 C# event 生成为 `add_OnDialogueStart` / `remove_OnDialogueStart`
        //   **方法**，`typeof(DialogueManager).GetProperty("OnDialogueStart")` 永远返回 null。
        //   正确写法（探针 ProbePlugin.cs:380-387 实测可收到事件）：
        //       var add = typeof(DialogueManager).GetMethod("add_" + name, BindingFlags.Public|BindingFlags.Instance);
        //       var cb  = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<DialogueNode>>(new Action<DialogueNode>(OnStartNode));
        //       add.Invoke(_dm, new object[] { cb });
        //   add_ 是原生追加语义 → 不需要读旧值、不需要 Combine、不需要写回校验。
        // =====================================================================

        private static void TryInstallEvents()
        {
            if (_dm == null) return;
            // 已经挂在这个实例上就什么都不做（重复 add 会让同一句念两遍）
            if (_eventsTried && ReferenceEquals(_hookedDm, _dm)) return;

            _eventsTried = true;
            _hookedDm = _dm;

            int ok = 0;

            try
            {
                _cbStart = Il2CppInterop.Runtime.DelegateSupport
                    .ConvertDelegate<Il2CppSystem.Action<DialogueNode>>(new Action<DialogueNode>(OnStartNode));
                ok += AddHandler("OnDialogueStart", _cbStart);
            }
            catch (Exception e) { LogOnce("sub-start", e); }

            try
            {
                _cbAdvance = Il2CppInterop.Runtime.DelegateSupport
                    .ConvertDelegate<Il2CppSystem.Action<DialogueNode>>(new Action<DialogueNode>(OnAdvanceNode));
                ok += AddHandler("OnDialogueAdvance", _cbAdvance);
            }
            catch (Exception e) { LogOnce("sub-advance", e); }

            try
            {
                _cbEnd = Il2CppInterop.Runtime.DelegateSupport
                    .ConvertDelegate<Il2CppSystem.Action>(new Action(OnEndEvent));
                ok += AddHandler("OnDialogueEnd", _cbEnd);
            }
            catch (Exception e) { LogOnce("sub-end", e); }

            try
            {
                _cbOption = Il2CppInterop.Runtime.DelegateSupport
                    .ConvertDelegate<Il2CppSystem.Action<DialogueOption>>(new Action<DialogueOption>(OnOptionSelected));
                ok += AddHandler("OnOptionSelected", _cbOption);
            }
            catch (Exception e) { LogOnce("sub-option", e); }

            _eventsOn = ok > 0;
            if (_eventsOn)
                Log("事件订阅生效 " + ok + "/4 条（主路径=事件；轮询仍作为兜底并行运行）。");
            else
                Log("事件订阅全部失败 → 降级为每帧轮询（CurrentNode + 气泡文本）。功能不受影响。");
            A11yHost.Diag(LogPrefix + " events=" + _eventsOn + " ok=" + ok);
        }

        /// <summary>调 add_Xxx(我们的委托)。返回 1=成功，0=失败。</summary>
        private static int AddHandler(string eventName, object nativeCb)
        {
            try
            {
                MethodInfo add = typeof(DialogueManager).GetMethod("add_" + eventName,
                    BindingFlags.Public | BindingFlags.Instance);
                if (add == null)
                {
                    Log("事件方法不存在：add_" + eventName + "（诊断日志，功能由轮询兜底）");
                    return 0;
                }
                add.Invoke(_dm, new object[] { nativeCb });
                A11yHost.Diag(LogPrefix + " add_" + eventName + " ok");
                return 1;
            }
            catch (Exception e)
            {
                LogOnce("add-" + eventName, e);
                return 0;
            }
        }

        /// <summary>调 remove_Xxx(我们的委托)（插件卸载时尽量不留悬挂委托）。</summary>
        private static void RemoveHandler(string eventName, object nativeCb)
        {
            try
            {
                if (_dm == null || nativeCb == null) return;
                MethodInfo rem = typeof(DialogueManager).GetMethod("remove_" + eventName,
                    BindingFlags.Public | BindingFlags.Instance);
                if (rem != null) rem.Invoke(_dm, new object[] { nativeCb });
            }
            catch (Exception e) { LogOnce("remove-" + eventName, e); }
        }

        // ---- 事件处理器（异常绝不能穿过原生边界）----

        private static void OnStartNode(DialogueNode node)
        {
            try { AnnounceNode(node, "event"); } catch (Exception e) { LogOnce("ev-start", e); }
        }

        private static void OnAdvanceNode(DialogueNode node)
        {
            try { AnnounceNode(node, "event"); } catch (Exception e) { LogOnce("ev-advance", e); }
        }

        private static void OnEndEvent()
        {
            try { OnDialogueEnded(); } catch (Exception e) { LogOnce("ev-end", e); }
        }

        private static void OnOptionSelected(DialogueOption opt)
        {
            try
            {
                string t = "";
                try { if (opt != null) t = TextProc.Clean(opt.text); } catch { }
                A11yHost.Diag(LogPrefix + " OnOptionSelected text=" + t);
                // 选项已经真的被选中了 → 待选列表作废（编号不再有效，避免念旧列表）
                Choices.Clear();
            }
            catch (Exception e) { LogOnce("ev-option", e); }
        }

        // =====================================================================
        // 路径二：轮询
        // =====================================================================

        private static void PollNode()
        {
            DialogueNode node = null;
            try { node = _dm.CurrentNode; } catch (Exception e) { LogOnce("poll-node", e); return; }
            if (node == null) return;

            int id = int.MinValue;
            try { id = node.id; } catch { }
            if (id == _lastNodeId) return;
            _lastNodeId = id;
            AnnounceNode(node, "poll");
        }

        /// <summary>气泡文本兜底：node.text 拿不到时，读玩家真正看到的那段 TMP 文本。</summary>
        private static void PollBubbleText()
        {
            if (_bubble == null) return;

            bool typing = true;
            try { typing = DialogueManager.IsBubbleTyping(); } catch { }
            if (typing) return;                       // 打字机没停，读到的是半句话

            string text = ReadBubbleBody();
            if (!TextProc.ShouldRead(text)) return;
            if (text == _lastSpokenText && _lastNodeId == _lastSpokenNodeId) return;

            // 气泡里是玩家真正看到的那句；若与刚才念过的完全一样，Announce 里的去重会拦掉。
            Announce(text, "", false, "bubble");
            _lastSpokenNodeId = _lastNodeId;
        }

        /// <summary>
        /// 从气泡里取出**正文**。
        ///
        /// 实机实测：`Lilith/Canvas/DialogueBubble` 下有一个 TMP 显示说话人（内容就是「莉莉丝」），
        /// 正文在另一个 TMP 节点里 —— 所以不能只取 `_dialogueText`（它可能正好是说话人格子）。
        /// 取法：把气泡子树里所有 TMP 文本收起来，
        ///   ① 若有一段和当前 node.text 完全相同 → 就是它（最可靠）；
        ///   ② 否则丢掉「等于说话人名」的短段，取最长的一段；
        ///   ③ 都没有就退回 `_dialogueText`。
        /// </summary>
        private static string ReadBubbleBody()
        {
            string fallback = "";
            try
            {
                TextMeshProUGUI main = _bubble._dialogueText;
                if (main != null) fallback = TextProc.Clean(main.text);
            }
            catch (Exception e) { LogOnce("poll-bubble", e); }

            try
            {
                TMP_Text[] tmps = _bubble.transform.GetComponentsInChildren<TMP_Text>(true);
                string best = "";
                for (int i = 0; i < tmps.Length; i++)
                {
                    TMP_Text tmp = tmps[i];
                    if (tmp == null) continue;
                    string s = "";
                    try { s = TextProc.Clean(tmp.text); } catch { continue; }
                    if (!TextProc.ShouldRead(s)) continue;
                    // ① 和刚刚处理过的那句 node.text 完全相同 → **这条气泡不再重复念**。
                    //   实机踩过：事件路径已经把这一句念/记过了（有配音时只进重读缓冲），
                    //   气泡兜底又拿同一句话走一遍 Announce，而两条路的去重 key 不同
                    //   （事件带 lineId、气泡不带），结果有配音的台词被朗读一遍、无配音的念两遍。
                    if (_lastNodeTextClean.Length > 0 && s == _lastNodeTextClean) return "";
                    if (_lastSpeakerName.Length > 0 && s == _lastSpeakerName) continue;       // 说话人格子
                    if (s.Length > best.Length) best = s;                                     // ②
                }
                if (best.Length > 0) return best;
            }
            catch { }

            return fallback;   // ③
        }

        /// <summary>
        /// 玩家台词选项（PlayerLineController）：Lilith 说完后由玩家选台词。
        /// 依据：PlayerLineController.IsAnyOptionsShowing（4170）、_texts（3357）、
        /// OnButtonClicked(int index)（5366，就是游戏自己的按钮处理器）。
        /// </summary>
        private static void PollPlayerLineOptions()
        {
            bool showing = false;
            PlayerLineController plc = _plc;
            if (plc == null) { try { plc = UnityEngine.Object.FindObjectOfType<PlayerLineController>(); _plc = plc; } catch { } }

            // 三条判据取并集（实机下 IsAnyOptionsShowing 不一定为真：
            // 它是静态 getter，只在「有实例正在显示选项」时才置位；
            // 而玩家台词选项还会走自定义模式（_isCustomMode）。
            // 只要任意一条为真就当「选项正在显示」）。
            try { if (PlayerLineController.IsAnyOptionsShowing) showing = true; } catch { }
            if (plc != null)
            {
                try { if (plc._isShowing) showing = true; } catch { }
                try { if (plc._isCustomMode) showing = true; } catch { }
            }

            if (!showing)
            {
                if (_pendingPlcFingerprint.Length > 0)
                {
                    _pendingPlcFingerprint = "";
                    Choices.ClearIfTag("plc");
                }
                _plcDiagAt = 0f;
                return;
            }

            if (Choices.HasPending) return;

            if (plc == null) return;

            var labels = new System.Collections.Generic.List<string>(8);
            var realIndex = new System.Collections.Generic.List<int>(8);   // 过滤后也要能点回“真正那一条”
            int rawCount = 0;
            try
            {
                var texts = plc._texts;
                var buttons = plc._buttons;
                int n = texts != null ? texts.Count : 0;
                rawCount = n;
                for (int i = 0; i < n; i++)
                {
                    TextMeshProUGUI tmp = null;
                    try { tmp = texts[i]; } catch { continue; }
                    if (tmp == null) { continue; }

                    // 可见性：实机实测同一批 Button 里混着预制体占位（activeInHierarchy=true
                    // 但父级 CanvasGroup alpha=0 或整组没显示）。只认「真正看得见」的。
                    UnityEngine.UI.Button btn = null;
                    try { if (buttons != null && i < buttons.Count) btn = buttons[i]; } catch { }
                    bool vis = true;
                    try
                    {
                        if (btn != null) vis = IsVisibleInUI(btn.transform) && IsVisibleInUI(tmp.transform);
                        else vis = IsVisibleInUI(tmp.transform);
                    }
                    catch { vis = false; }
                    if (!vis) continue;

                    // 标签来源：优先 plc._texts[i]（游戏自己填的正文字段），
                    // 实机证据：`[Reader] plc showing=True texts=2 labels=0 []` ——
                    // 有这个 TMP 但此刻 text 还是空的（选项刚弹出、文本下一帧才写），
                    // 所以再加一层「从按钮子树里找第一个像人话的 TMP」的兜底。
                    string t = ReadOptionLabel(tmp, btn);
                    if (!TextProc.ShouldRead(t)) continue;
                    if (LooksLikePrefabJunk(t)) continue;      // "Button" / "112" / "ButtonButton" …
                    if (labels.Contains(t)) continue;

                    if (labels.Count >= 9) break;
                    labels.Add(t);
                    realIndex.Add(i);
                }
            }
            catch (Exception e) { LogOnce("plc-read", e); return; }

            // 诊断：开着「界面诊断日志」时每秒最多一条，实机确认选项到底有没有被抓到。
            try
            {
                float nowDiag = Time.unscaledTime;
                if (nowDiag >= _plcDiagAt)
                {
                    _plcDiagAt = nowDiag + 1f;
                    A11yHost.Diag(LogPrefix + " plc showing=" + showing +
                                  " texts=" + rawCount + " labels=" + labels.Count +
                                  " [" + string.Join("|", labels.ToArray()) + "]");
                }
            }
            catch { }

            if (labels.Count == 0) return;

            string fp = "plc#" + labels.Count + "#" + string.Join("|", labels.ToArray());
            if (fp == _pendingPlcFingerprint) return;
            _pendingPlcFingerprint = fp;

            PlayerLineController target = plc;
            var idx = realIndex;
            Choices.Show(labels, "plc", oneBased =>
            {
                int at = oneBased - 1;
                if (at < 0 || at >= idx.Count) return;
                try { target.OnButtonClicked(idx[at]); }   // OnButtonClicked 是 0-based 的按钮序号
                catch (Exception e) { LogOnce("plc-click", e); }
            });
        }

        // =====================================================================
        // 可见性 / 预制体垃圾文本
        // =====================================================================

        /// <summary>
        /// 这个节点在画面上真的看得见吗？
        /// 判据：沿途父级都 active，且途经的 CanvasGroup.alpha 都 &gt; 0.05。
        /// 依据：实机实测选项按钮里混着 alpha=0 的预制体占位（activeInHierarchy 却是 true）。
        /// </summary>
        private static bool IsVisibleInUI(Transform t)
        {
            try
            {
                for (int up = 0; up < 12 && t != null; up++)
                {
                    if (!t.gameObject.activeInHierarchy) return false;
                    CanvasGroup cg = null;
                    try { cg = t.GetComponent<CanvasGroup>(); } catch { }
                    if (cg != null)
                    {
                        float a = 1f;
                        try { a = cg.alpha; } catch { }
                        if (a <= 0.05f) return false;
                    }
                    t = t.parent;
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>预制体里写死的垃圾文本：类型名、纯数字、同一个词重复两遍。</summary>
        private static readonly System.Collections.Generic.HashSet<string> _junkWords =
            new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Button", "ButtonButton", "Text", "Label", "Item", "Option", "Options",
                "Toggle", "Slider", "Content", "Placeholder", "New Text", "Sample", "Example",
            };

        /// <summary>
        /// 读一条选项的标签：先 plc._texts[i]，空/垃圾时退到按钮子树里第一个「像人话」的 TMP。
        /// 依据：实机日志 `plc showing=True texts=2 labels=0 []` —— 选项刚弹出时
        /// _texts[i].text 可能还是空串（下一帧才写），此时按钮子树里的 TMP 已有文字。
        /// 绝不使用 GameObject 名（"Button"/"Button (1)" 是预制体名，不是选项文本）。
        /// </summary>
        private static string ReadOptionLabel(TMPro.TextMeshProUGUI own, UnityEngine.UI.Button btn)
        {
            try
            {
                if (own != null)
                {
                    string s = TextProc.Clean(own.text);
                    if (TextProc.ShouldRead(s) && !LooksLikePrefabJunk(s)) return s;
                }
            }
            catch { }

            try
            {
                if (btn != null)
                {
                    var kids = btn.GetComponentsInChildren<TMPro.TMP_Text>(true);
                    for (int k = 0; k < kids.Length; k++)
                    {
                        var kid = kids[k];
                        if (kid == null) continue;
                        string s = TextProc.Clean(kid.text);
                        if (!TextProc.ShouldRead(s)) continue;
                        if (LooksLikePrefabJunk(s)) continue;
                        return s;
                    }
                }
            }
            catch { }
            return "";
        }

        private static bool LooksLikePrefabJunk(string s)
        {
            if (string.IsNullOrEmpty(s)) return true;
            string t = s.Trim();
            if (t.Length == 0) return true;
            if (_junkWords.Contains(t)) return true;

            bool allDigits = true;
            for (int i = 0; i < t.Length; i++)
            {
                char c = t[i];
                if (!char.IsDigit(c) && c != '.' && c != ' ') { allDigits = false; break; }
            }
            if (allDigits) return true;

            // 同一个 ASCII 词重复（ButtonButton）——只对英文成立，中文「好的好的」是正常台词，不能拦
            if (t.Length >= 4 && (t.Length % 2) == 0)
            {
                int half = t.Length / 2;
                bool ascii = true;
                for (int i = 0; i < t.Length; i++) { if (t[i] > 127) { ascii = false; break; } }
                if (ascii && string.CompareOrdinal(t, 0, t, half, half) == 0) return true;
            }
            return false;
        }

        // =====================================================================
        // 一条台词的处理
        // =====================================================================

        /// <summary>
        /// 说话人：DialogueNode.speaker 是**英文 key**（实机实测是 "lilith"），不是中文名。
        /// 这里做 key → 中文名的映射；映射不到的 key 原样保留（宁可用 key，
        /// 也不要凭空编个中文名骗玩家），并在日志里留样例供实机校准。
        /// </summary>
        private static string SpeakerOf(string raw)
        {
            string s = "";
            try { s = TextProc.Speaker(raw); } catch { }
            if (s.Length == 0) return "";
            try
            {
                string k = s.Trim().ToLowerInvariant();
                switch (k)
                {
                    case "lilith": return "莉莉丝";
                    // TODO(实机校准)：本作实测只出现过 speaker="lilith"。
                    //   若日志里出现别的 key（例如 narrator / player / 其他角色名），
                    //   按反编译的 DialogueNode 数据补映射，不要靠猜。
                    default: return s;
                }
            }
            catch { return s; }
        }

        private static void AnnounceNode(DialogueNode node, string source)
        {
            if (node == null) return;

            string speaker = "";
            int lineId = 0;
            try { lineId = node.lineId; } catch { }
            try { speaker = SpeakerOf(node.speaker); } catch { }

            string text = "";
            try { text = TextProc.Clean(node.text); } catch { }
            if (!TextProc.ShouldRead(text))
            {
                // node.text 为空 → 交给气泡兜底（PollBubbleText）
                try { text = ResolveLineText(node); } catch { }
            }

            bool voiced;
            bool unknown = !TryHasVoice(node, out voiced);

            _lastNodeTextClean = text;   // 气泡兜底时用来在气泡子树里认出「正文那个 TMP」

            if (TextProc.ShouldRead(text))
            {
                Announce(text, speaker, voiced, source, lineId);
            }
            else
            {
                A11yHost.Diag(LogPrefix + " node id=" + SafeId(node) + " has no inline text; bubble fallback pending.");
            }

            // 选项：node 自带选项（Lilith 的问题）
            try { HandleNodeOptions(node); } catch (Exception e) { LogOnce("node-options", e); }

            // 统计 + 说话人取样（实机校准用）
            _lines++;
            if (unknown) _linesUnknown++;
            else if (voiced) _linesVoiced++;
            else _linesRead++;
            if (speaker.Length > 0 && _speakerLogs < 12 && _speakersSeen.Add(speaker))
            {
                _speakerLogs++;
                Log("说话人样例（实机校准用）：\"" + speaker + "\"");
            }
            if ((_lines % 25) == 0)
            {
                Log("统计：共 " + _lines + " 句；有配音 " + _linesVoiced +
                    "，无配音 " + _linesRead + "，判定不了 " + _linesUnknown + "。");
            }
        }

        private static void HandleNodeOptions(DialogueNode node)
        {
            bool has = false;
            try { has = node.HasOptions; } catch { }
            if (!has) return;

            var labels = new System.Collections.Generic.List<string>(8);
            try
            {
                var opts = node.options;
                int n = opts != null ? opts.Count : 0;
                for (int i = 0; i < n && labels.Count < 9; i++)
                {
                    DialogueOption o = null;
                    try { o = opts[i]; } catch { continue; }
                    if (o == null) continue;
                    string t = "";
                    try { t = TextProc.Clean(o.text); } catch { }
                    // 这里**不能跳项**：index 直接决定 SelectOption(i)，跳一个就会点错选项。
                    // 文本不行就退回「第 N 个选项」，位置永远和游戏对齐。
                    bool ok = TextProc.ShouldRead(t) && !LooksLikePrefabJunk(t);
                    labels.Add(ok ? t : ("第 " + (i + 1) + " 个选项"));
                }
            }
            catch (Exception e) { LogOnce("node-opt-read", e); return; }

            if (labels.Count == 0) return;

            DialogueManager dm = _dm;
            Choices.Show(labels, "node", oneBased =>
            {
                try { dm.SelectOption(oneBased - 1); }   // SelectOption 是 0-based（DialogueManager.cs:3760）
                catch (Exception e) { LogOnce("dm-select", e); }
            });
        }

        private static string ResolveLineText(DialogueNode node)
        {
            try
            {
                int lineId = node.lineId;
                if (lineId <= 0) return "";
                DialogueLineEntry entry;
                if (!DialogueLineRepository.TryGetEntry(lineId, out entry) || entry == null) return "";
                return TextProc.Clean(entry.text);
            }
            catch (Exception e) { LogOnce("line-repo", e); return ""; }
        }

        /// <summary>
        /// 有配音吗？返回 false 表示**判定不了**（而不是「确定没有」）。
        /// </summary>
        private static bool TryHasVoice(DialogueNode node, out bool voiced)
        {
            voiced = false;
            try
            {
                string sid = DialogueManager.ResolveNodeSoundId(node);
                if (!string.IsNullOrEmpty(sid)) { voiced = true; return true; }
            }
            catch (Exception e) { LogOnce("resolve-sound", e); }

            try
            {
                string sid = node.soundId;
                if (!string.IsNullOrEmpty(sid)) { voiced = true; return true; }
            }
            catch (Exception e) { LogOnce("node-sound", e); }

            try
            {
                int lineId = node.lineId;
                if (lineId > 0)
                {
                    string sid;
                    if (DialogueLineRepository.TryGetVoiceSoundId(lineId, out sid) && !string.IsNullOrEmpty(sid))
                    {
                        voiced = true;
                        return true;
                    }
                    return true;   // 查询成功且确实没有配音 → 确定无配音
                }
            }
            catch (Exception e) { LogOnce("voice-db", e); }

            return false;          // 全都问不出来 → 判定不了
        }

        /// <summary>真正决定「念 / 只记」的地方。</summary>
        private static void Announce(string text, string speaker, bool voiced, string source, int lineId = 0)
        {
            string clean = TextProc.Clean(text);
            if (!TextProc.ShouldRead(clean)) return;

            // 去重：同一条 text + lineId 在 15 秒内只处理一次。
            // 为什么要 15 秒这么长：实机实测同一句会被事件、每帧轮询、气泡兜底三条路重复喂进来，
            // 而「同一句台词在 15 秒内再次出现」在对话里几乎只会是重复喂，不是玩家真的又听一遍。
            float now = Time.unscaledTime;
            string key = clean + "\u0001" + lineId;
            if (key == _lastKey && (now - _lastKeyAt) < 15f) return;
            _lastKey = key;
            _lastKeyAt = now;

            _lastSpeakerName = speaker;

            bool sayName = true;
            try { sayName = Plugin.CfgSpeakName == null || Plugin.CfgSpeakName.Value; } catch { }
            string plain = (sayName && speaker.Length > 0) ? (speaker + "：" + clean) : clean;

            bool read = false;
            if (voiced)
            {
                read = false;                      // 游戏语音会播，别重复念
            }
            else
            {
                bool cfgRead = true, cfgUnknown = true;
                try { if (Plugin.CfgReadUnvoiced != null) cfgRead = Plugin.CfgReadUnvoiced.Value; } catch { }
                try { if (Plugin.CfgSpeakWhenUnknown != null) cfgUnknown = Plugin.CfgSpeakWhenUnknown.Value; } catch { }
                read = cfgRead || cfgUnknown;
            }

            if (read)
            {
                Repeat.Say(plain, true);
                _lastSpokenText = clean;
                A11yHost.Diag(LogPrefix + " SAY[" + source + "]" + (voiced ? " (voiced)" : "") + " " + clean);
            }
            else
            {
                Repeat.Note(plain);               // 只进重读缓冲：按 F…/重读键还能听一遍
                A11yHost.Diag(LogPrefix + " NOTE[" + source + "] " + clean);
            }
        }

        private static void OnDialogueEnded()
        {
            try
            {
                Choices.Clear();
                _lastSpokenText = "";
                _lastSpokenNodeId = int.MinValue;
                _pendingPlcFingerprint = "";
                // ★ 必须把 _lastNodeId 归零：Update 里的判据是「不活跃 && _lastNodeId != int.MinValue」，
                //   原来这里不重置，导致对话结束后**每帧**都进来打一条 "dialogue ended." 把日志刷满。
                _lastNodeId = int.MinValue;
                A11yHost.Diag(LogPrefix + " dialogue ended.");
            }
            catch (Exception e) { LogOnce("ended", e); }
        }

        // =====================================================================
        // 供 Patches / Plugin 调用
        // =====================================================================

        /// <summary>Harmony 补丁（可选，默认关）拦到 DialogueManager.Say 时调用。</summary>
        internal static void OnSaidText(string text, int lineId)
        {
            try
            {
                string t = TextProc.Clean(text);
                if (!TextProc.ShouldRead(t)) return;
                bool voiced = false;
                try
                {
                    if (lineId > 0)
                    {
                        string sid;
                        if (DialogueLineRepository.TryGetVoiceSoundId(lineId, out sid) && !string.IsNullOrEmpty(sid)) voiced = true;
                    }
                }
                catch { }
                Announce(t, "", voiced, "say-patch");
            }
            catch (Exception e) { LogOnce("say-patch", e); }
        }

        internal static void OnDialogueStarted()
        {
            Choices.Clear();
            _lastNodeId = int.MinValue;
            _lastSpokenText = "";
        }

        /// <summary>
        /// 重读键（默认 Backspace）。不自己记台词，直接交给平台层的重读缓冲 Repeat ——
        /// 那样「刚念的」和「被选项挤掉的」都在同一条时间线上，按一下就能听回上一句。
        /// </summary>
        internal static void RepeatLast()
        {
            try { Repeat.Again(); }
            catch (Exception e) { LogOnce("repeat-last", e); }
        }

        /// <summary>一行统计，进日志给实机校准用（不刷屏）。</summary>
        internal static string StatsLine()
        {
            return "lines=" + _lines + " voiced=" + _linesVoiced + " read=" + _linesRead +
                   " unknown=" + _linesUnknown + " events=" + (_eventsOn ? "on" : "off");
        }

        internal static void Shutdown()
        {
            try
            {
                // 插件卸载时把我们的委托 remove_ 掉，尽量不留悬挂引用（游戏对象自己会清理，这里只是干净）
                if (_eventsOn && _dm != null)
                {
                    RemoveHandler("OnDialogueStart", _cbStart);
                    RemoveHandler("OnDialogueAdvance", _cbAdvance);
                    RemoveHandler("OnDialogueEnd", _cbEnd);
                    RemoveHandler("OnOptionSelected", _cbOption);
                }
            }
            catch (Exception e) { LogOnce("shutdown", e); }
            _eventsOn = false;
            _eventsTried = false;
            _hookedDm = null;
            _cbStart = _cbAdvance = _cbEnd = _cbOption = null;
            _dm = null; _bubble = null; _plc = null;
            Choices.Clear();
        }

        private static int SafeId(DialogueNode node)
        {
            try { return node.id; } catch { return int.MinValue; }
        }

        // =====================================================================
        // 日志
        // =====================================================================

        private static readonly System.Collections.Generic.HashSet<string> _loggedErrors =
            new System.Collections.Generic.HashSet<string>();

        private static void Log(string msg)
        {
            try { A11yHost.Log?.LogInfo(msg); } catch { }
        }

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
