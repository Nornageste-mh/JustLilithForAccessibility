// 不/存在的莉莉丝 无障碍补丁 —— 逐作层「游戏内交互桥」
//
// 这个文件只做一件事：把「游戏自己的功能」暴露给读屏补丁 ——
//   ① 功能菜单（SettingView）的显示/隐藏与当前是否可见；
//   ② 莉莉丝身体的 8 个可交互部位（CharacterHitAreaType）的定向点击。
//
// 为什么需要这一层（实机取证，2026-10-22）：
//   - 本作窗口带 WS_EX_NOACTIVATE + WS_EX_TRANSPARENT，游戏进程内拿不到键盘焦点，
//     OS 层 SetCursorPos 也返回 False（Lead 探针实测）→ **一切交互必须走游戏内 API**，
//     不能靠注入鼠标/键盘。
//   - Lead 探针实测可用并且已经跑通：
//       CharacterInteractionHandler.SetInteractionEnabled(true)
//       CharacterInteractionHandler.HandleMouseDown() + HandleMouseUp()
//       → 真的触发了对话（OnDialogueStart lineId=2100109「唔……别偷看……」）
//       SettingView.Show() → CanvasGroup.alpha=1.00，18 秒内没有自动隐藏
//   - `CharacterInteractionHandler.DelayedSingleClick(CharacterHitAreaType, Vector3)`
//     （反编译 CharacterInteractionHandler.cs:2563，public）是**点位定向**点击：
//     比 HandleMouseDown/Up（按当前鼠标位置算）更准，因为它直接指定部位。
//     它的返回类型 UniTaskVoid 在 Cysharp.Threading.Tasks 程序集里，为了让本工程不必
//     额外引用 UniTask.dll，这里用缓存的 MethodInfo 反射调用（只反射一次）。
//
// 类型都能强类型直用（反编译实查：全部是 Assembly-CSharp 里的全局命名空间 public 类型）：
//   CharacterController : SingletonBehaviour<CharacterController>（有 ToggleSetting(), 3395）
//   SettingView : MonoBehaviour（有 Show()/Hide()/Toggle()/_canvasGroup）
//   CharacterInteractionHandler : SingletonBehaviour<...>（SetInteractionEnabled 2111,
//       HandleMouseDown 2290, HandleMouseUp 2301, DelayedSingleClick 2563）
//   CharacterHitAreaType { Unknown, Head, Face, Body, Chest, Hand, Skirt, Leg }
//
// ★ 命名冲突坑：Assembly-CSharp 里有一个全局 `CharacterController`，UnityEngine 里也有一个
//   （UnityEngine.PhysicsModule）。本工程没有引用 PhysicsModule，所以不会 CS0104 冲突；
//   万一以后有人加了引用，把这里全部换成 `global::CharacterController` 即可。

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace JustLilithA11y
{
    internal static class LilithApi
    {
        internal const string LogPrefix = "[Lilith]";

        /// <summary>身体部位导航组的 Canvas 根对象名（UiNav.Game.cs 靠它给组名）。</summary>
        internal const string BodyCanvasName = "LilithA11yBodyNav";
        /// <summary>身体部位按钮的对象名前缀（UiNav.Game.cs 靠它给标签）。</summary>
        internal const string PartPrefix = "LilithPart_";
        /// <summary>导航里这一组显示的名字。</summary>
        internal const string BodyGroupName = "莉莉丝交互";

        // ---- 单例/组件引用（场景切换会重建，所以每 2 秒最多重抓一次）----
        private static CharacterController _cc;
        private static CharacterInteractionHandler _handler;
        private static SettingView _settingView;
        private static float _nextRescanAt;

        // ---- 交互开关：只在我们真的要点击时才打开（避免在剧情演出中强行打开）----
        private static bool _interactionEnabled;
        private static float _nextInteractionTryAt;

        private static readonly HashSet<string> _errLogged = new HashSet<string>();

        // =====================================================================
        // 引用抓取
        // =====================================================================

        internal static SettingView MenuView { get { EnsureRefs(); return _settingView; } }
        internal static CharacterInteractionHandler Handler { get { EnsureRefs(); return _handler; } }

        private static void EnsureRefs()
        {
            try
            {
                if (Time.unscaledTime < _nextRescanAt) return;
                _nextRescanAt = Time.unscaledTime + 2f;

                if (!Alive(_settingView)) _settingView = FirstOf<SettingView>();
                if (!Alive(_handler))
                {
                    _handler = FirstOf<CharacterInteractionHandler>();
                    _interactionEnabled = false; // 新实例 = 交互开关状态未知，下次点击时重新打开
                    if (_handler != null) HookClickDiagnostics(_handler);
                }
                if (!Alive(_cc)) _cc = FirstOf<CharacterController>();
            }
            catch (Exception e) { LogOnce("ensure-refs", e); }
        }

        private static bool Alive(UnityEngine.Object o)
        {
            // Unity 的「假 null」语义：对象被销毁后 == null 为真，所以不能只用 ReferenceEquals
            return o != null;
        }

        /// <summary>
        /// 取场景里第一个该类型对象（含未激活的）。
        /// ★ 不用 Resources.FindObjectsOfTypeAll 的 LINQ：平台层与逐作层都不用 LINQ，省依赖也省分配。
        /// </summary>
        private static T FirstOf<T>() where T : UnityEngine.Object
        {
            try
            {
                var all = Resources.FindObjectsOfTypeAll<T>();
                if (all == null) return null;
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i] != null) return all[i];
                }
            }
            catch (Exception e) { LogOnce("find-" + typeof(T).Name, e); }
            return null;
        }

        // =====================================================================
        // 功能菜单（SettingView）
        //
        // 反编译实查：SettingView 没有 IsVisible 属性（ViewPanelBase 才有，但 SettingView 不继承它），
        // 所以可见性用 `_canvasGroup.alpha` 判断（探针实测 Show 之后 alpha=1.00）。
        // 打开优先走 CharacterController.ToggleSetting()（:3395，游戏自己的入口，会处理状态机/动画），
        // 失败再退 SettingView.Show()。
        // =====================================================================

        internal static float MenuAlpha()
        {
            try
            {
                var sv = MenuView;
                if (sv == null) return 0f;
                var cg = sv._canvasGroup;
                if (cg != null) return cg.alpha;
                return sv.gameObject.activeInHierarchy ? 1f : 0f;
            }
            catch (Exception e) { LogOnce("menu-alpha", e); return 0f; }
        }

        internal static bool MenuVisible()
        {
            try
            {
                var sv = MenuView;
                if (sv == null) return false;
                if (!sv.gameObject.activeInHierarchy) return false;
                return MenuAlpha() > 0.5f;
            }
            catch (Exception e) { LogOnce("menu-visible", e); return false; }
        }

        /// <summary>打开功能菜单。返回是否（看起来）成功；how 写清实际走的哪条路，便于日志定位。</summary>
        internal static bool OpenMenu(out string how, bool toggleOnly = false)
        {
            how = "";
            try
            {
                if (MenuVisible()) { how = "已经打开"; return true; }

                var cc = _cc;
                if (cc != null)
                {
                    try
                    {
                        cc.ToggleSetting();
                        how = "CharacterController.ToggleSetting";
                    }
                    catch (Exception e) { LogOnce("toggle-setting", e); }
                }

                // toggleOnly（保活重开专用）：只走游戏自己的 CharacterController.ToggleSetting()，
                // **绝不调 SettingView.Show()**。依据（Lead 实机，2026-10-23）：
                //   SettingView.Toggle() 走完「开-关-开-关」全流程时，游戏不会帮我们收掉已展开的
                //   某个子面板；保活里直接 Show() 会把玩家正展开的视图打乱。所以保活只按一下开关。
                if (!MenuVisible() && !toggleOnly)
                {
                    var sv = MenuView;
                    if (sv != null)
                    {
                        try
                        {
                            sv.Show();
                            how = (how.Length > 0 ? how + " 失败 → " : "") + "SettingView.Show";
                        }
                        catch (Exception e) { LogOnce("settingview-show", e); }
                    }
                }

                bool ok = MenuVisible();
                Log(LogPrefix + " 打开功能菜单 via " + (how.Length > 0 ? how : "(无可用入口)") +
                    " → visible=" + ok + " alpha=" + MenuAlpha().ToString("0.00"));
                return ok;
            }
            catch (Exception e) { LogOnce("open-menu", e); return false; }
        }

        /// <summary>关闭功能菜单。</summary>
        internal static bool CloseMenu(out string how)
        {
            how = "";
            try
            {
                if (!MenuVisible()) { how = "本来就没开"; return true; }

                var sv = MenuView;
                if (sv != null)
                {
                    try
                    {
                        sv.Hide();
                        how = "SettingView.Hide";
                    }
                    catch (Exception e) { LogOnce("settingview-hide", e); }
                }

                if (MenuVisible() && _cc != null)
                {
                    try
                    {
                        _cc.ToggleSetting();
                        how = (how.Length > 0 ? how + " 失败 → " : "") + "CharacterController.ToggleSetting";
                    }
                    catch (Exception e) { LogOnce("toggle-setting-close", e); }
                }

                bool ok = !MenuVisible();
                Log(LogPrefix + " 关闭功能菜单 via " + (how.Length > 0 ? how : "(无可用入口)") +
                    " → hidden=" + ok);
                return ok;
            }
            catch (Exception e) { LogOnce("close-menu", e); return false; }
        }

        // =====================================================================
        // 身体部位：定向交互
        // =====================================================================

        /// <summary>部位 → 反编译里 Coliders 子对象的名字（实机日志：Head/Face/Chest/Body/HandLeft/HandRight/Skirt/Leg）。</summary>
        internal static string PartObjectName(CharacterHitAreaType type)
        {
            switch (type)
            {
                case CharacterHitAreaType.Head: return "Head";
                case CharacterHitAreaType.Face: return "Face";
                case CharacterHitAreaType.Body: return "Body";
                case CharacterHitAreaType.Chest: return "Chest";
                case CharacterHitAreaType.Hand: return "HandLeft"; // 手只有一个枚举值，默认用左手对象做锚点
                case CharacterHitAreaType.Skirt: return "Skirt";
                case CharacterHitAreaType.Leg: return "Leg";
                default: return "";
            }
        }

        /// <summary>身体部位的中文标签（导航项朗读用）。</summary>
        internal static string PartLabel(CharacterHitAreaType type)
        {
            switch (type)
            {
                case CharacterHitAreaType.Head: return "头";
                case CharacterHitAreaType.Face: return "脸";
                case CharacterHitAreaType.Body: return "身体";
                case CharacterHitAreaType.Chest: return "胸口";
                case CharacterHitAreaType.Hand: return "手";
                case CharacterHitAreaType.Skirt: return "裙子";
                case CharacterHitAreaType.Leg: return "腿";
                default: return "";
            }
        }

        /// <summary>
        /// 触发某个部位的交互。
        ///
        /// ★ 2026-10-23 实机结论（Lead 验证）：只调 `DelayedSingleClick(area, anchor)` 时，
        ///   锚点已经正确（Head → 0.03,-3.47）但**游戏没有任何可观测反应**（没有台词事件）。
        ///   而探针实验证明 `HandleMouseDown()+HandleMouseUp()` 是能真的触发对话的
        ///   （`OnDialogueStart lineId=2100109 text=唔……别偷看……`）。
        ///   所以这里改成「完整交互链 + 逐条日志 + 定时兜底」：
        ///     ① SetInteractionEnabled(true)
        ///     ② EnterInteractionState()（public，反编译 :2173）
        ///     ③ DelayedSingleClick(area, anchor)（精确部位，反编译 :2563）
        ///     ④ 1.5 秒内没等到台词（Reader.DialogueStartCount 没变）→ HandleMouseDown()+HandleMouseUp()
        ///     ⑤ 还没反应 → RaycastMouse() 读一次结果；再不行用 TryGlanceAt(anchor) 抛个反应
        ///   ④⑤ 由 Update() 按时间片推进（不阻塞主线程，也不在事件回调里 sleep）。
        /// </summary>
        internal static bool TriggerPart(CharacterHitAreaType type, string anchorName = null)
        {
            try
            {
                var h = Handler;
                if (h == null)
                {
                    LogWarn(LogPrefix + " 触发部位失败：找不到 CharacterInteractionHandler");
                    Say("现在没法与莉莉丝互动。");
                    return false;
                }

                EnsureInteractionEnabled(h);

                // ② 对话占用时先推进对话，不触发部位。
                //    实机证据（2026-10-23 自检，Lead 指派实验）：对话空闲（dmActive=False）时
                //    触发 Skirt 确实产生了真实反应 —— 1 条新台词 + voiceClip 由 LazyWave 变成
                //    2000010_cn，延迟 318 毫秒；而 dmActive=True 时相同操作 0 台词、voice 不变。
                //    所以部位反应被对话状态门控；对话中按回车应当「继续对话」而不是「摸一把」，
                //    这样既符合玩家直觉，也不会打断剧情。
                if (DialogueBusy())
                {
                    bool advanced = false;
                    try
                    {
                        var dm = FirstOf<DialogueManager>();
                        if (dm != null)
                        {
                            dm.AdvanceDialogue();
                            advanced = true;
                        }
                    }
                    catch (Exception e) { LogOnce("gate-advance", e); }

                    Log(LogPrefix + " 部位触发被门控：对话进行中 → " +
                        (advanced ? "已推进对话" : "推进对话失败") + "（" + type + "）");
                    Say(advanced ? "对话进行中，已继续对话。" : "对话进行中。");
                    return true;   // 视为「本次操作有响应」，避免上层再念「没有反应」
                }

                Vector3 pos = PartWorldPosition(type, anchorName, h);
                Log(LogPrefix + " 触发部位 " + type +
                    (string.IsNullOrEmpty(anchorName) ? "" : "/" + anchorName) +
                    "（锚点世界坐标 " + pos.x.ToString("0.00") + "," + pos.y.ToString("0.00") + "）");
                LogDiagState("触发前 " + type);

                // ② 交互状态（进状态机，很多部位反应要求先在里面）
                try
                {
                    h.EnterInteractionState();
                    Log(LogPrefix + " EnterInteractionState 已调用");
                }
                catch (Exception e) { LogOnce("enter-interaction-state", e); }

                // ③ 先把系统光标移到该部位的屏幕坐标，等两帧，再走游戏**原生**点击路径。
                //    依据（2026-10-23 A/B 定性，Lead 判定）：
                //      游戏是按自己的 Input.mousePosition 做 RaycastMouse 的；
                //      我们直接调 HandleMouseDown/Up 时鼠标在别处（日志 mouse=810,267），
                //      所以 RaycastMouse()=null、OnSingleClickHitArea=Unknown —— 事件链通但没命中部位。
                //      DelayedSingleClick 本身也不派发事件（只做延迟判定）。
                //    → 唯一接近真实点击的做法：把光标挪到部位上，再让游戏自己按下/抬起。
                MoveCursorToWorld(pos, type);
                _stageArmed = true;
                _stageAt = Time.unscaledTime + 0.12f;   // ≈2 帧，给 Input.mousePosition 更新留时间
                _stageType = type;
                _stagePos = pos;
                _stageHandler = h;
                return true;
            }
            catch (Exception e) { LogOnce("trigger-part", e); return false; }
        }

        // ---------------- 分阶段：光标就位 → 原生按下/抬起 → 精确单击 → 兜底 ----------------
        private static bool _stageArmed;
        private static float _stageAt;
        private static CharacterHitAreaType _stageType;
        private static Vector3 _stagePos;
        private static CharacterInteractionHandler _stageHandler;

        private static void UpdateStage()
        {
            if (!_stageArmed) return;
            try
            {
                if (Time.unscaledTime < _stageAt) return;
                _stageArmed = false;

                var h = _stageHandler != null ? _stageHandler : Handler;
                if (h == null) return;

                try
                {
                    Vector3 mp = Input.mousePosition;
                    Log(LogPrefix + " 光标就位检查：Input.mousePosition=" +
                        mp.x.ToString("0") + "," + mp.y.ToString("0"));
                }
                catch { }
                LogDiagState("光标就位 " + _stageType);

                // 游戏原生路径：按下 → 读它自己的命中 → 抬起
                int before = Reader.DialogueStartCount;
                try
                {
                    h.HandleMouseDown();
                    RaycastHit2D hit = h.RaycastMouse();
                    string hitName = hit.collider != null ? hit.collider.gameObject.name : "null";
                    Log(LogPrefix + " 原生点击：已 HandleMouseDown，RaycastMouse=" + hitName +
                        "，_pressHitArea=" + SafeArea(h));
                    h.HandleMouseUp();
                    Log(LogPrefix + " 原生点击：已 HandleMouseUp（新台词 " +
                        (Reader.DialogueStartCount - before) + " 条）");
                }
                catch (Exception e) { LogOnce("stage-native-click", e); }

                // 精确单击（保留：无副作用，多一条腿）
                try { InvokeDelayedSingleClick(h, _stageType, _stagePos); } catch { }

                // 兜底链（仅在原生点击没执行过时才用；1.5 秒后如果还是没有新台词，再退鼠标/注视）
                ArmFallback(_stageType, _stagePos, h, false, false);
            }
            catch (Exception e) { LogOnce("stage-update", e); _stageArmed = false; }
        }

        private static string SafeArea(CharacterInteractionHandler h)
        {
            try { return h._pressHitArea.ToString(); } catch { return "?"; }
        }

        /// <summary>
        /// 把系统光标移到某个世界坐标在屏幕上的位置。
        /// 关键点：Unity 的屏幕坐标原点在左下角、桌面坐标系原点在左上角，必须翻转 y，
        /// 并且要把「游戏窗口客户区左上角」加到桌面上（否则多显示器/窗口化时会偏）。
        /// </summary>
        private static void MoveCursorToWorld(Vector3 world, CharacterHitAreaType type)
        {
            try
            {
                Vector3 sp = Vector3.zero;
                Camera used = null;
                Vector3 best = Vector3.zero;
                Camera bestCam = null;
                try
                {
                    var cams = Camera.allCameras;
                    if (cams != null)
                    {
                        for (int i = 0; i < cams.Length; i++)
                        {
                            var cam = cams[i];
                            if (cam == null) continue;
                            Vector3 p = cam.WorldToScreenPoint(world);
                            if (p.z <= 0f) continue;
                            bool inside = p.x >= 0f && p.x <= Screen.width && p.y >= 0f && p.y <= Screen.height;
                            if (inside) { best = p; bestCam = cam; break; }
                            if (bestCam == null) { best = p; bestCam = cam; }
                        }
                    }
                }
                catch (Exception e) { LogOnce("move-cursor-cams", e); }

                if (bestCam == null) { LogWarn(LogPrefix + " 光标移动失败：没有可用相机"); return; }
                sp = best;
                used = bestCam;
                Log(LogPrefix + " 部位屏幕坐标 " + type + " → " +
                    sp.x.ToString("0") + "," + sp.y.ToString("0") + "（相机 " + used.name +
                    "，屏幕 " + Screen.width + "x" + Screen.height + "）");

                int dx = (int)System.Math.Round(sp.x);
                int dy = (int)System.Math.Round(Screen.height - sp.y); // 翻转 y

                // 加上窗口客户区原点（Unity 的全屏/无边框窗口一般是 0,0，但仍按真实值算）
                int ox, oy;
                if (TryGetClientOrigin(out ox, out oy)) { dx += ox; dy += oy; }

                bool ok = false;
                try { ok = SetCursorPos(dx, dy); } catch (Exception e) { LogOnce("set-cursor-pos", e); }
                Log(LogPrefix + " 已调用 SetCursorPos(" + dx + "," + dy + ") → 返回 " + ok);
                if (!ok)
                {
                    try { MouseMoveAbsolute(dx, dy); Log(LogPrefix + " SetCursorPos 失败 → 已改用 mouse_event 绝对移动"); }
                    catch (Exception e) { LogOnce("mouse-event-move", e); }
                }
            }
            catch (Exception e) { LogOnce("move-cursor-to-world", e); }
        }

        // ---- Win32（只为了把光标挪到部位上；不装钩子、不拦键）----
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT p);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);

        private const uint MouseEventMove = 0x0001;
        private const uint MouseEventAbsolute = 0x8000;

        private static bool TryGetClientOrigin(out int ox, out int oy)
        {
            ox = 0; oy = 0;
            try
            {
                IntPtr hwnd = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
                if (hwnd == IntPtr.Zero) return false;
                POINT p = new POINT();
                p.X = 0; p.Y = 0;
                if (!ClientToScreen(hwnd, ref p)) return false;
                ox = p.X; oy = p.Y;
                return true;
            }
            catch (Exception e) { LogOnce("client-origin", e); return false; }
        }

        /// <summary>mouse_event 的绝对移动（0..65535 归一化）。SetCursorPos 被拦时用。</summary>
        private static void MouseMoveAbsolute(int x, int y)
        {
            int sw = Math.Max(1, Screen.currentResolution.width);
            int sh = Math.Max(1, Screen.currentResolution.height);
            uint nx = (uint)Math.Round(x * 65535.0 / Math.Max(1, sw - 1));
            uint ny = (uint)Math.Round(y * 65535.0 / Math.Max(1, sh - 1));
            mouse_event(MouseEventMove | MouseEventAbsolute, nx, ny, 0, UIntPtr.Zero);
        }

        // ---------------- 交互兜底状态机 ----------------
        // 用「台词开始次数」判断游戏有没有反应：Reader 在 OnDialogueStart 里自增。
        private static bool _fallbackArmed;
        private static float _fallbackAt;
        private static CharacterHitAreaType _fallbackType;
        private static Vector3 _fallbackPos;
        private static int _fallbackStartCount;
        private static bool _fallbackDidMouse;
        private static bool _fallbackAllowMouse = true;
        private static string _voiceBefore = "";
        private const float FallbackDelay = 1.5f;

        /// <summary>
        /// 是否正被对话占用（对话活跃或气泡还在打字）。
        /// 用途：部位交互前的门控（实机证明对话中部位反应不会发生）。
        /// </summary>
        private static bool DialogueBusy()
        {
            try
            {
                var dm = FirstOf<DialogueManager>();
                if (dm != null && dm.IsDialogueActive) return true;
            }
            catch { }
            try { if (DialogueManager.IsBubbleTyping()) return true; }
            catch { }
            return false;
        }

        /// <summary>
        /// 当前语音音源正在用的 clip 名（读不到返回空串）。
        /// 用途：部位反应的「成功」不只表现为新台词，也可能只有一段语音/音效
        /// （实机 2026-10-23：命中区已经正确派发 Head/Chest/Hand/Skirt，但没有 OnDialogueStart，
        ///  voiceClip 却从 2100110_cn 变成 2051236_cn）→ 用 clip 变化作为第二判据。
        /// </summary>
        internal static string VoiceClipName()
        {
            try
            {
                var am = FirstOf<AudioManager>();
                if (am == null) return "";
                var src = am.source_Voice;
                if (src == null || src.clip == null) return "";
                return src.clip.name;
            }
            catch { return ""; }
        }

        private static void ArmFallback(CharacterHitAreaType type, Vector3 pos,
                                        CharacterInteractionHandler h, bool delayedOk,
                                        bool allowMouseFallback = true)
        {
            try
            {
                _fallbackArmed = true;
                _triggerAt = Time.unscaledTime;
                _fallbackAt = Time.unscaledTime + FallbackDelay;
                _fallbackType = type;
                _fallbackPos = pos;
                _fallbackStartCount = Reader.DialogueStartCount;
                _fallbackDidMouse = false;
                _fallbackAllowMouse = allowMouseFallback;
                _voiceBefore = VoiceClipName();
            }
            catch (Exception e) { LogOnce("arm-fallback", e); }
        }

        private static float _triggerAt;

        /// <summary>每帧调用（Plugin 的 tick 里，跟 LilithMenu/LilithBodyNav 一起）。</summary>
        internal static void Update()
        {
            // 光标就位 → 原生点击的分阶段推进（见 UpdateStage）。
            UpdateStage();

            // 交互成功的判定放在每帧（不是只在兜底时间点），这样能记下「多少毫秒后游戏说话」，
            // 好区分到底是 DelayedSingleClick 的延迟反应还是兜底路径起效。
            if (_fallbackArmed)
            {
                try
                {
                    int now = Reader.DialogueStartCount;
                    if (now > _fallbackStartCount)
                    {
                        _fallbackArmed = false;
                        Log(LogPrefix + " 部位交互成功（" + _fallbackType + " 触发了 " +
                            (now - _fallbackStartCount) + " 条新台词，延迟 " +
                            ((Time.unscaledTime - _triggerAt) * 1000f).ToString("0") + " 毫秒，" +
                            (_fallbackDidMouse ? "在鼠标兜底之后" : "未用到兜底") + "）");
                    }
                    else
                    {
                        // 第二判据：没有新台词，但语音音源换了 clip → 也算有反应（实机见过这种情况）。
                        string vc = VoiceClipName();
                        if (vc.Length > 0 && vc != _voiceBefore)
                        {
                            _fallbackArmed = false;
                            Log(LogPrefix + " 部位交互成功（" + _fallbackType + " 有语音反应：" +
                                _voiceBefore + " → " + vc + "，延迟 " +
                                ((Time.unscaledTime - _triggerAt) * 1000f).ToString("0") + " 毫秒，" +
                                (_fallbackDidMouse ? "在鼠标兜底之后" : "未用到兜底") + "）");
                        }
                    }
                }
                catch (Exception e) { LogOnce("success-detect", e); }
            }

            if (!_fallbackArmed) return;
            try
            {
                if (Time.unscaledTime < _fallbackAt) return;

                // 已经有新台词 / 已有语音反应了 → 上面那块已经处理过；这里只是二次保险
                int now = Reader.DialogueStartCount;
                if (now > _fallbackStartCount)
                {
                    _fallbackArmed = false;
                    return;
                }
                string vcNow = VoiceClipName();
                if (vcNow.Length > 0 && vcNow != _voiceBefore)
                {
                    _fallbackArmed = false;
                    return;
                }

                var h = Handler;
                if (h == null) { _fallbackArmed = false; return; }

                if (!_fallbackAllowMouse)
                {
                    // 阶段里已经走过游戏原生点击（光标就位 → HandleMouseDown/Up），
                    // 再补一次鼠标点击会把同一个部位**重复触发**（实机见过同部位 2 条事件），
                    // 所以这里只结束兜底，不再重复点。
                    _fallbackArmed = false;
                    Log(LogPrefix + " 兜底：原生点击已执行但 " + FallbackDelay.ToString("0.0") +
                        " 秒内没观测到台词/语音 —— 该部位在当前场景/状态下可能没有对话反应（游戏行为）。");
                    return;
                }

                if (!_fallbackDidMouse)
                {
                    // ④ 鼠标按下/抬起：探针已验证能触发对话（不区分部位）
                    _fallbackDidMouse = true;
                    _fallbackAt = Time.unscaledTime + FallbackDelay;
                    _fallbackStartCount = Reader.DialogueStartCount;
                    LogDiagState("兜底④前 " + _fallbackType);
                    try
                    {
                        h.HandleMouseDown();
                        h.HandleMouseUp();
                        Log(LogPrefix + " 兜底④：DelayedSingleClick 没有反应 → 已调用 HandleMouseDown/HandleMouseUp");
                    }
                    catch (Exception e) { LogOnce("mouse-down-up", e); }
                    LogDiagState("兜底④后 " + _fallbackType);
                    return;
                }

                // ⑤ 最后再试一次「看过去」的反应，并读一次 RaycastMouse 供日志定位
                _fallbackArmed = false;
                try
                {
                    var hit = h.RaycastMouse();
                    Log(LogPrefix + " 兜底⑤：RaycastMouse 结果 = " + (hit ? hit.ToString() : "null"));
                }
                catch (Exception e) { LogOnce("raycast-mouse", e); }

                try
                {
                    // TryGlanceAt 在 CharacterController 上（CharacterController.cs:3574），
                    // 不是 CharacterInteractionHandler 的方法。
                    var cc = CharacterController.instance;
                    if (cc != null)
                    {
                        cc.TryGlanceAt(_fallbackPos, null);
                        Log(LogPrefix + " 兜底⑤：已调用 CharacterController.TryGlanceAt(" +
                            _fallbackPos.x.ToString("0.00") + "," + _fallbackPos.y.ToString("0.00") + ")");
                    }
                    else
                    {
                        LogWarn(LogPrefix + " 兜底⑤：CharacterController 实例为空，跳过 TryGlanceAt");
                    }
                }
                catch (Exception e) { LogOnce("try-glance-at", e); }

                LogWarn(LogPrefix + " 兜底链走完，部位 " + _fallbackType + " 可能没有配置交互反应（游戏行为，不是补丁问题）");
            }
            catch (Exception e) { LogOnce("fallback-update", e); _fallbackArmed = false; }
        }


        private static void EnsureInteractionEnabled(CharacterInteractionHandler h)
        {
            if (_interactionEnabled) return;
            if (Time.unscaledTime < _nextInteractionTryAt) return;
            _nextInteractionTryAt = Time.unscaledTime + 2f;
            try
            {
                h.SetInteractionEnabled(true);
                _interactionEnabled = true;
                Log(LogPrefix + " SetInteractionEnabled(true) 已调用（部位交互需要它）");
            }
            catch (Exception e) { LogOnce("set-interaction-enabled", e); }
        }

        /// <summary>
        /// 部位的锚点世界坐标。
        /// 反编译/实机：8 个部位对象在 `Lilith/Coliders` 下，名字 Head/Face/Chest/Body/HandLeft/HandRight/Skirt/Leg。
        ///
        /// ★ 2026-10-23 修正（Lead 实机证据）：`Lilith/Coliders` 父节点默认 `activeSelf=False`，
        ///   这时 8 个部位的 transform 全在同一个点（日志里 Head 锚点是 0.00,-4.18 —— 那是莉莉丝的
        ///   身体原点，不是头）。把该父节点临时激活后，Collider2D 立刻有真实 bounds
        ///   （Head 0.33x0.16、Skirt 0.92x0.61、Leg 0.33x0.86 …）。
        ///   所以锚点取 `Collider2D.bounds.center`：**临时激活 → 读 bounds → 立刻还原 active 状态**，
        ///   不把游戏自己的碰撞体状态改掉（只在读到真实锚点时更准，读不到退回 transform.position）。
        ///   结果按部位缓存 5 秒，避免每按一次回车就折腾一次 active 状态。
        /// </summary>
        private struct AnchorEntry
        {
            public Vector3 Pos;
            public float At;
            public bool FromCollider;
        }

        private static readonly Dictionary<string, AnchorEntry> _anchorCache =
            new Dictionary<string, AnchorEntry>(StringComparer.Ordinal);
        private const float AnchorCacheSeconds = 5f;

        private static Vector3 PartWorldPosition(CharacterHitAreaType type, string anchorName,
                                                 CharacterInteractionHandler h)
        {
            try
            {
                Transform coliders = FindDeep(h.transform, "Coliders");
                if (coliders != null)
                {
                    // ① 调用方点名了锚点（左手/右手这种同枚举不同对象的情况）→ 优先用它
                    if (!string.IsNullOrEmpty(anchorName))
                    {
                        Vector3 p;
                        if (TryResolveAnchor(anchorName, coliders, out p)) return p;
                    }

                    string want = PartObjectName(type);
                    if (type == CharacterHitAreaType.Hand)
                    {
                        // 「手」有左右两个对象；没点名时取两者中点更稳
                        Vector3 l, r;
                        bool hasL = TryResolveAnchor("HandLeft", coliders, out l);
                        bool hasR = TryResolveAnchor("HandRight", coliders, out r);
                        if (hasL && hasR) return (l + r) * 0.5f;
                        if (hasL) return l;
                        if (hasR) return r;
                    }
                    if (!string.IsNullOrEmpty(want))
                    {
                        Vector3 p;
                        if (TryResolveAnchor(want, coliders, out p)) return p;
                    }
                }

                // 退路一：扫场上的 CharacterHitArea 组件，按 Type 匹配
                try
                {
                    var areas = Resources.FindObjectsOfTypeAll<CharacterHitArea>();
                    if (areas != null)
                    {
                        for (int i = 0; i < areas.Length; i++)
                        {
                            var a = areas[i];
                            if (a == null) continue;
                            CharacterHitAreaType at;
                            try { at = a.Type; } catch { continue; }
                            if (at != type) continue;
                            Vector3 p2;
                            if (TryAnchorFromPart(a.transform, coliders, out p2)) return p2;
                            return a.transform.position;
                        }
                    }
                }
                catch (Exception e) { LogOnce("find-hit-area", e); }

                // 退路二：莉莉丝自己的位置（点在她身上，部位不精确但至少是同一个人）
                if (h != null) return h.transform.position;
            }
            catch (Exception e) { LogOnce("part-position", e); }
            return Vector3.zero;
        }

        /// <summary>按名字找部位对象并解析锚点（带 5 秒缓存）。</summary>
        private static bool TryResolveAnchor(string name, Transform colidersRoot, out Vector3 pos)
        {
            pos = Vector3.zero;
            try
            {
                float now = Time.unscaledTime;
                AnchorEntry e;
                if (_anchorCache.TryGetValue(name, out e) && now - e.At < AnchorCacheSeconds)
                {
                    pos = e.Pos;
                    return true;
                }

                Transform part = FindDeep(colidersRoot, name);
                if (part == null) return false;

                Vector3 p;
                bool fromCollider = TryAnchorFromPart(part, colidersRoot, out p);
                if (!fromCollider) p = part.position;

                _anchorCache[name] = new AnchorEntry { Pos = p, At = now, FromCollider = fromCollider };
                pos = p;
                return true;
            }
            catch (Exception e) { LogOnce("resolve-anchor-" + name, e); return false; }
        }

        /// <summary>
        /// 从部位的 Collider2D.bounds.center 取锚点。
        ///
        /// 为什么不能直接用 transform.position：`Lilith/Coliders` 默认 inactive，
        /// 那时 8 个部位对象的 transform 全叠在莉莉丝身体原点上（实机日志已验证）。
        /// getComponent 拿 Collider2D 需要该物体是激活的，所以这里临时激活父节点、
        /// 读完立刻还原 —— 不改游戏自己的碰撞体状态。
        /// </summary>
        private static bool TryAnchorFromPart(Transform part, Transform colidersRoot, out Vector3 pos)
        {
            pos = Vector3.zero;
            GameObject colidersGo = null;
            bool restoreParent = false;
            bool restorePart = false;
            try
            {
                // ① 激活 `Lilith/Coliders`：碰撞体不激活就没有真实 bounds（实测此时 bounds 全 0 尺寸）
                if (colidersRoot != null)
                {
                    colidersGo = colidersRoot.gameObject;
                    if (colidersGo != null && !colidersGo.activeSelf)
                    {
                        colidersGo.SetActive(true);
                        restoreParent = true;
                    }
                }

                // ② 部位对象自身也可能是 inactive（父节点激活不代表自己 activeSelf=true）→ 一并临时激活
                if (part != null && !part.gameObject.activeSelf)
                {
                    part.gameObject.SetActive(true);
                    restorePart = true;
                }

                // ③ 先在自己身上找碰撞体（typed：GetComponent<Collider2D>）
                if (TryBoundsOn(part, out pos)) return true;

                // ③b 递归子孙：很多部位是空节点，碰撞体挂在子物体上（inactive 的也算，用 true）
                var all = part.GetComponentsInChildren<Transform>(true);
                if (all != null)
                {
                    for (int i = 0; i < all.Length; i++)
                    {
                        Transform node = all[i];
                        if (node == null || node == part) continue;
                        if (!node.gameObject.activeSelf) node.gameObject.SetActive(true);
                        if (TryBoundsOn(node, out pos)) return true;
                    }
                }

                // ④ 诊断：把「这个部位下到底有什么组件」记一次，免得下次盲猜
                LogOnce("anchor-diag-" + (part != null ? part.name : "null"),
                        DescribeComponents(part));
            }
            catch (Exception e) { LogOnce("anchor-from-collider", e); }
            finally
            {
                if (restorePart)
                {
                    try { if (part != null) part.gameObject.SetActive(false); } catch { }
                }
                if (restoreParent)
                {
                    try { if (colidersGo != null) colidersGo.SetActive(false); } catch { }
                }
            }
            return false;
        }

        /// <summary>
        /// 反射拿到 `UnityEngine.Collider2D` 类型（不新增 Physics2DModule 编译期引用），
        /// 然后 `Resources.FindObjectsOfTypeAll(类型)` 扫全场景碰撞体，挑挂在 part 自己/子孙上的。
        ///
        /// 为什么要这样兜底：`part.GetComponents<Component>()` 在某些 IL2CPP 代理对象上可能拿不到
        /// 2D 物理组件（实机第一版就是这么静默失败的），而 FindObjectsOfTypeAll(Type) 走的是
        /// UnityEngine 自己的注册表，可靠性更高，且 inactive 的对象也在结果里。
        /// </summary>
        private static bool TryBoundsByScan(Transform part, out Vector3 pos)
        {
            pos = Vector3.zero;
            try
            {
                Type colType = Collider2DType();
                if (colType == null || part == null) return false;

                // Il2CppInterop 的 Resources.FindObjectsOfTypeAll 只收 Il2CppSystem.Type：
                // 用 Il2CppType.From(System.Type) 转换（探针 ProbePlugin.cs:591 同一用法）。
                var arr = Resources.FindObjectsOfTypeAll(Il2CppType.From(colType));
                if (arr == null) return false;

                PropertyInfo boundsProp = colType.GetProperty("bounds");
                if (boundsProp == null) return false;

                for (int i = 0; i < arr.Length; i++)
                {
                    var c = arr[i] as Component;
                    if (c == null) continue;
                    Transform t;
                    try { t = c.transform; } catch { continue; }
                    if (t == null || !IsSelfOrDescendant(t, part)) continue;

                    object boxed;
                    try { boxed = boundsProp.GetValue(c); } catch { continue; }
                    if (!(boxed is Bounds)) continue;
                    Bounds b = (Bounds)boxed;
                    if (b.size.sqrMagnitude < 1e-8f) continue;
                    pos = b.center;
                    Log(LogPrefix + " 部位锚点取自 Collider2D.bounds（全场景反查）： " + t.name +
                        " (size " + b.size.x.ToString("0.00") + "x" + b.size.y.ToString("0.00") + ")" +
                        " → " + pos.x.ToString("0.00") + "," + pos.y.ToString("0.00"));
                    return true;
                }
            }
            catch (Exception e) { LogOnce("bounds-by-scan", e); }
            return false;
        }

        private static Type _collider2dType;
        private static bool _collider2dLooked;

        private static Type Collider2DType()
        {
            if (_collider2dLooked) return _collider2dType;
            _collider2dLooked = true;
            try
            {
                var asms = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < asms.Length; i++)
                {
                    Type t = null;
                    try { t = asms[i].GetType("UnityEngine.Collider2D", false); } catch { }
                    if (t != null)
                    {
                        _collider2dType = t;
                        Log(LogPrefix + " 已定位 Collider2D 类型：" + t.Assembly.GetName().Name);
                        return t;
                    }
                }
                LogWarn(LogPrefix + " 找不到 UnityEngine.Collider2D 类型 → 部位锚点只能用 transform.position");
            }
            catch (Exception e) { LogOnce("collider2d-type", e); }
            return _collider2dType;
        }

        private static bool IsSelfOrDescendant(Transform t, Transform root)
        {
            try
            {
                Transform cur = t;
                for (int i = 0; i < 24 && cur != null; i++)
                {
                    if (cur == root) return true;
                    cur = cur.parent;
                }
            }
            catch { }
            return false;
        }

        private static Exception DescribeComponents(Transform part)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.Append("部位下没找到可用的 Collider2D，节点清单：");
                if (part == null) sb.Append(" part=null");
                else
                {
                    Transform p = part.parent;
                    sb.Append(" parent=").Append(p != null ? p.name : "null");
                    var all = part.GetComponentsInChildren<Transform>(true);
                    sb.Append(" 子孙数=").Append(all != null ? all.Length : 0);
                    for (int i = 0; i < (all != null ? all.Length : 0) && i < 8; i++)
                    {
                        Transform node = all[i];
                        if (node == null) continue;
                        bool act = false;
                        try { act = node.gameObject.activeInHierarchy; } catch { }
                        sb.Append(" [").Append(node.name).Append(act ? "" : "(inactive)").Append(":");

                        // typed 读法：GetComponents<Component>() 只会把代理装成基类 Component
                        // （实机日志里类型名全打印成 "Component"），所以改成逐个具体类型问一遍。
                        try
                        {
                            var c2 = node.GetComponent<UnityEngine.Collider2D>();
                            if (c2 != null)
                            {
                                Bounds b = c2.bounds;
                                sb.Append(c2.GetType().Name)
                                  .Append("(size ").Append(b.size.x.ToString("0.00"))
                                  .Append("x").Append(b.size.y.ToString("0.00")).Append(")");
                            }
                        }
                        catch { }
                        try
                        {
                            var rb = node.GetComponent<Rigidbody2D>();
                            if (rb != null) sb.Append("Rigidbody2D,");
                        }
                        catch { }
                        sb.Append("]");
                    }
                }
                Log(LogPrefix + " " + sb);
            }
            catch { }
            return new Exception("anchor diagnostics");
        }

        /// <summary>
        /// 在一个节点**自己身上**找 Collider2D 并读 bounds.center。
        /// 返回 false 表示：没装碰撞体，或碰撞体还处于「没生效」状态（bounds 尺寸为 0），
        /// 后者不能拿来当锚点 —— 否则又会退回 (0.00,-4.18) 那个错误坐标。
        /// </summary>
        private static bool TryBoundsOn(Transform node, out Vector3 pos)
        {
            pos = Vector3.zero;
            try
            {
                if (node == null) return false;

                // ★ typed 读法（2026-10-23 修正）：之前用 `GetComponents<Component>()` 再靠
                //   `c.GetType().Name` 判断类型，实机日志证明**永远失败**——
                //   IL2CPP 代理在这些位置上被装成基类 `Component`，类型名全打印成 "Component"，
                //   所以既不匹配 "Collider2D" 也读不到 bounds。改成强类型的
                //   `GetComponent<UnityEngine.Collider2D>()`（需要 csproj 引用 Physics2DModule）。
                var col = node.GetComponent<UnityEngine.Collider2D>();
                if (col == null) return false;

                Bounds b = col.bounds;
                // 尺寸为 0 = 碰撞体还没真正生效（inactive / 未初始化）→ 不算锚点
                if (b.size.sqrMagnitude < 1e-8f) return false;

                pos = b.center;
                Log(LogPrefix + " 部位锚点取自 Collider2D.bounds： " + node.name +
                    " (" + col.GetType().Name +
                    " size " + b.size.x.ToString("0.00") + "x" + b.size.y.ToString("0.00") + ")" +
                    " → " + pos.x.ToString("0.00") + "," + pos.y.ToString("0.00"));
                return true;
            }
            catch (Exception e) { LogOnce("bounds-on", e); }
            return false;
        }

        private static Transform FindDeep(Transform root, string name)
        {
            try
            {
                if (root == null) return null;
                if (root.name == name) return root;
                int n = root.childCount;
                for (int i = 0; i < n; i++)
                {
                    Transform found = FindDeep(root.GetChild(i), name);
                    if (found != null) return found;
                }
            }
            catch { }
            return null;
        }

        // DelayedSingleClick 返回 UniTaskVoid（Cysharp.Threading.Tasks），
        // 为了不新增对 UniTask.dll 的编译期依赖，这里缓存 MethodInfo 反射调用一次。
        private static MethodInfo _delayedClick;
        private static bool _delayedClickLooked;

        private static bool InvokeDelayedSingleClick(CharacterInteractionHandler h,
                                                    CharacterHitAreaType type, Vector3 pos)
        {
            try
            {
                if (!_delayedClickLooked)
                {
                    _delayedClickLooked = true;
                    _delayedClick = typeof(CharacterInteractionHandler).GetMethod(
                        "DelayedSingleClick",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (_delayedClick == null)
                    {
                        LogWarn(LogPrefix + " 反编译里应有 DelayedSingleClick(CharacterHitAreaType, Vector3)，" +
                                "但运行期取不到 → 部位点击退回 HandleMouseDown/Up");
                    }
                    else
                    {
                        Log(LogPrefix + " 已缓存 DelayedSingleClick → " + _delayedClick);
                    }
                }

                if (_delayedClick == null) return false;
                _delayedClick.Invoke(h, new object[] { type, pos });
                return true;
            }
            catch (Exception e) { LogOnce("delayed-single-click", e); return false; }
        }

        // =====================================================================
        // 点击诊断：订阅游戏自己的「单击」事件，用来判断我们合成的点击到底有没有被处理
        // =====================================================================
        //
        // 反编译 CharacterInteractionHandler.cs:1302/1317 有
        //   public Il2CppSystem.Action OnSingleClick;
        //   public Il2CppSystem.Action<CharacterHitAreaType> OnSingleClickHitArea;
        // 全文检索没有别的类订阅它们（所以订阅它们不会影响游戏行为），
        // 但它们是**游戏自己处理完单击后**才 Invoke 的 —— 正好当探针：
        //   事件真触发 → 说明我们合成的点击被游戏认了（没台词只是这个部位没有台词反应）；
        //   事件不触发 → 说明状态合成还不对（比如 _pressHit 必须是非空 collider）。
        private static bool _clickDiagHooked;
        private static object _cbSingleClick, _cbSingleClickArea, _cbDoubleClick, _cbHoverEnter;

        private static void HookClickDiagnostics(CharacterInteractionHandler h)
        {
            if (_clickDiagHooked || h == null) return;
            _clickDiagHooked = true;
            try
            {
                _cbSingleClick = Il2CppInterop.Runtime.DelegateSupport
                    .ConvertDelegate<Il2CppSystem.Action>(new Action(OnGameSingleClick));
                AddHandler(h, "OnSingleClick", _cbSingleClick);

                _cbSingleClickArea = Il2CppInterop.Runtime.DelegateSupport
                    .ConvertDelegate<Il2CppSystem.Action<CharacterHitAreaType>>(
                        new Action<CharacterHitAreaType>(OnGameSingleClickArea));
                AddHandler(h, "OnSingleClickHitArea", _cbSingleClickArea);

                Log(LogPrefix + " 已订阅点击诊断事件（OnSingleClick / OnSingleClickHitArea）");

                _cbDoubleClick = Il2CppInterop.Runtime.DelegateSupport
                    .ConvertDelegate<Il2CppSystem.Action>(new Action(() =>
                    {
                        try { Log(LogPrefix + " 【点击诊断】游戏触发了 OnDoubleClick"); }
                        catch (Exception e2) { LogOnce("diag-double", e2); }
                    }));
                AddHandler(h, "OnDoubleClick", _cbDoubleClick);

                _cbHoverEnter = Il2CppInterop.Runtime.DelegateSupport
                    .ConvertDelegate<Il2CppSystem.Action>(new Action(() =>
                    {
                        try { Log(LogPrefix + " 【点击诊断】游戏触发了 OnHoverEnter"); }
                        catch (Exception e2) { LogOnce("diag-hover", e2); }
                    }));
                AddHandler(h, "OnHoverEnter", _cbHoverEnter);
            }
            catch (Exception e) { LogOnce("click-diag-hook", e); }
        }

        private static void AddHandler(object target, string eventName, object nativeCb)
        {
            try
            {
                var m = target.GetType().GetMethod("add_" + eventName,
                    BindingFlags.Public | BindingFlags.Instance);
                if (m == null) { LogOnce("add-" + eventName, new Exception("找不到 add_" + eventName)); return; }
                m.Invoke(target, new object[] { nativeCb });
                A11yHost.Diag(LogPrefix + " add_" + eventName + " ok");
            }
            catch (Exception e) { LogOnce("add-" + eventName, e); }
        }

        private static void OnGameSingleClick()
        {
            try { Log(LogPrefix + " 【点击诊断】游戏触发了 OnSingleClick"); }
            catch (Exception e) { LogOnce("diag-single", e); }
        }

        private static void OnGameSingleClickArea(CharacterHitAreaType area)
        {
            try { Log(LogPrefix + " 【点击诊断】游戏触发了 OnSingleClickHitArea = " + area); }
            catch (Exception e) { LogOnce("diag-single-area", e); }
        }

        /// <summary>
        /// 打印一次「门禁状态」，用来判断游戏到底有没有接受我们的交互。
        /// 全只读：不加锁、不改状态、不调任何会改状态的 API（RaycastMouse 也只是一次射线）。
        /// </summary>
        internal static void LogDiagState(string tag)
        {
            try
            {
                var sb = new StringBuilder(160);
                sb.Append(LogPrefix).Append(" 【门禁诊断 ").Append(tag).Append("】");
                var h = Handler;
                if (h == null)
                {
                    sb.Append(" handler=null");
                }
                else
                {
                    try { sb.Append(" enabled=").Append(h.InteractionEnabled); } catch { sb.Append(" enabled=?"); }
                    try { sb.Append(" holding=").Append(h.IsPointerHoldingCharacter); } catch { }
                    try { sb.Append(" pressed=").Append(h._isPressed).Append(" hovering=").Append(h._isHovering); } catch { }
                    try
                    {
                        RaycastHit2D hit = h.RaycastMouse();
                        sb.Append(" RaycastMouse=");
                        if (hit.collider != null) sb.Append(hit.collider.gameObject.name);
                        else sb.Append("null");
                    }
                    catch { sb.Append(" RaycastMouse=?"); }
                }
                try
                {
                    Vector3 mp = Input.mousePosition;
                    sb.Append(" mouse=").Append(mp.x.ToString("0")).Append(',').Append(mp.y.ToString("0"));
                }
                catch { }
                try
                {
                    var dm = FirstOf<DialogueManager>();
                    sb.Append(" dmActive=").Append(dm != null && dm.IsDialogueActive);
                }
                catch { }
                try { sb.Append(" bubbleTyping=").Append(DialogueManager.IsBubbleTyping()); } catch { }
                try { sb.Append(" voicePlaying=").Append(AudioManager.IsVoicePlaying()); } catch { }
                try
                {
                    var am = FirstOf<AudioManager>();
                    if (am != null && am.source_Voice != null && am.source_Voice.clip != null)
                    {
                        sb.Append(" voiceClip=").Append(am.source_Voice.clip.name);
                    }
                }
                catch { }
                Log(sb.ToString());
            }
            catch (Exception e) { LogOnce("diag-state", e); }
        }

        // =====================================================================
        // 定向单击：直接摆好游戏的「按下状态」再放行它自己的 HandleMouseUp
        // =====================================================================
        //
        // 依据（反编译 CharacterInteractionHandler.cs；Il2CppInterop 把私有字段生成为公开属性，
        // 带 get 和 set，可以直接读写）：
        //   _pressHit(1074 RaycastHit2D) _pressHitArea(1087 CharacterHitAreaType)
        //   _pressScreenPos(1035 Vector2) _pressTime(1048 float) _lastClickTime(1061 float)
        //   _isPressed(996 bool) _pressInterruptedYawn(1100 bool)
        // HandleMouseDown 的全部作用就是「读这些字段、标记按下」，HandleMouseUp 再按
        // _pressHitArea 决定播哪个部位的反应。所以设置好 _pressHitArea 再调 HandleMouseUp
        // 就能做**定向**交互，完全不依赖真实鼠标位置（本作窗口单击穿透，鼠标也进不来）。
        // 每次都会把改过的字段还原，避免把游戏状态带歪。
        private static bool InvokeTargetedClick(CharacterInteractionHandler h,
                                                CharacterHitAreaType type, Vector3 worldPos)
        {
            if (h == null) return false;
            CharacterHitAreaType oldArea = CharacterHitAreaType.Unknown;
            bool oldPressed = false, oldInterrupted = false;
            Vector2 oldScreen = default(Vector2);
            float oldPressTime = 0f, oldLastClick = 0f;
            RaycastHit2D oldHit = default(RaycastHit2D);
            bool touched = false;
            try
            {
                // 备份
                try { oldArea = h._pressHitArea; } catch { }
                try { oldHit = h._pressHit; } catch { }
                try { oldPressed = h._isPressed; } catch { }
                try { oldInterrupted = h._pressInterruptedYawn; } catch { }
                try { oldScreen = h._pressScreenPos; } catch { }
                try { oldPressTime = h._pressTime; } catch { }
                try { oldLastClick = h._lastClickTime; } catch { }

                // ★ 先让游戏自己走一次 HandleMouseDown：它会补齐它自己的内部记账
                //   （_pressHit / _pressTime / 以及我们看不到的其它字段），
                //   上一次实验里「只设 _pressHitArea + HandleMouseUp」游戏**一个事件都没触发**
                //   （OnSingleClick 没响），说明 HandleMouseUp 里还有「必须是按下的状态」的前置判断。
                //   这里先按下、再把命中结果改写成我们的定向锚点，最后放行 HandleMouseUp。
                try
                {
                    h.HandleMouseDown();
                    Log(LogPrefix + " 定向单击：已先调用 HandleMouseDown（补齐按下记账）");
                }
                catch (Exception e) { LogOnce("targeted-click-down", e); }

                // 摆状态
                touched = true;
                h._pressHitArea = type;
                h._isPressed = true;
                h._pressInterruptedYawn = false;
                h._pressTime = Time.time;
                // 把上一次点击时间推远，避免被判成「双击」
                h._lastClickTime = -100f;

                // 关键：HandleMouseUp 很可能还会读 _pressHit.collider，只设 _pressHitArea 不够。
                // RaycastHit2D 的 interop 结构体没有公开构造函数（CS1729 实测），所以不能自己拼；
                // 改成让物理引擎返回一个**真实**命中：从锚点向上的射线（本作是 2D 正向角色，
                // 锚点就在碰撞体内部，Physics2D 默认 queriesStartInColliders=true 会命中自身）。
                try
                {
                    RaycastHit2D hit = Physics2D.Raycast(new Vector2(worldPos.x, worldPos.y), Vector2.up, 5f);
                    var col = hit.collider;
                    if (col != null)
                    {
                        h._pressHit = hit;
                        Log(LogPrefix + " 定向单击：已填 _pressHit（" + col.GetType().Name +
                            " @ " + worldPos.x.ToString("0.00") + "," + worldPos.y.ToString("0.00") + "）");
                    }
                    else
                    {
                        LogWarn(LogPrefix + " 定向单击：锚点处射不到 Collider2D（_pressHit 只能保持原值），" +
                                "只设 _pressHitArea");
                    }
                }
                catch (Exception e) { LogOnce("targeted-click-presshit", e); }

                try
                {
                    var cam = Camera.main;
                    if (cam != null)
                    {
                        Vector3 sp = cam.WorldToScreenPoint(worldPos);
                        h._pressScreenPos = new Vector2(sp.x, sp.y);
                    }
                }
                catch (Exception e) { LogOnce("targeted-click-screen-pos", e); }

                // 放行：游戏自己按 _pressHitArea 处理这次单击
                h.HandleMouseUp();
                return true;
            }
            catch (Exception e) { LogOnce("targeted-click", e); return false; }
            finally
            {
                if (touched)
                {
                    try { h._pressHitArea = oldArea; } catch { }
                    try { h._pressHit = oldHit; } catch { }
                    try { h._isPressed = oldPressed; } catch { }
                    try { h._pressInterruptedYawn = oldInterrupted; } catch { }
                    try { h._pressScreenPos = oldScreen; } catch { }
                    try { h._pressTime = oldPressTime; } catch { }
                    try { h._lastClickTime = oldLastClick; } catch { }
                }
            }
        }

        // =====================================================================
        // 小工具
        // =====================================================================

        /// <summary>走 Repeat 说话（逐作层纪律：不直接调 Speech.Speak）。</summary>
        internal static void Say(string text)
        {
            try { Repeat.Say(text, true); } catch (Exception e) { LogOnce("say", e); }
        }

        internal static void Log(string msg)
        {
            try { A11yHost.Log?.LogInfo(msg); } catch { }
        }

        internal static void LogWarn(string msg)
        {
            try { A11yHost.Log?.LogWarning(msg); } catch { }
        }

        internal static void LogOnce(string where, Exception e)
        {
            try
            {
                if (!_errLogged.Add(where)) return;
                A11yHost.Log?.LogWarning(LogPrefix + " " + where + " 异常（同类只记一次）: " +
                                         e.GetType().Name + ": " + e.Message);
            }
            catch { }
        }

        internal static void Reset()
        {
            _cc = null;
            _handler = null;
            _settingView = null;
            _nextRescanAt = 0f;
            _interactionEnabled = false;
            _nextInteractionTryAt = 0f;
        }
    }
}

