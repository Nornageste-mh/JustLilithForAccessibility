// 不/存在的莉莉丝 无障碍补丁 —— 逐作层「打开菜单」快捷键 + 功能菜单常驻
//
// 需求来源（用户 m01949）：①普通状态进不了导航（游戏菜单默认不显示）→ 要给一个「打开菜单」快捷键；
// ②菜单存在时间很短 → 菜单不要自动隐藏、快捷键随时调出。
//
// 做法：
//   - 快捷键默认 Ctrl+Alt+Shift+O（0.2.0.1 起；旧默认 Ctrl+Alt+O。HardKey 解析复用平台层 Hotkey）。
//     为什么是 O：游戏自带 9 条热键全是 Ctrl+Alt+单字母
//     （M=music K=alarm T=timer P=pomodoro R=memo_simple B=memo_detailed N=memo_scratch S=mute L=click_through，
//      见实机日志 [Hotkey] 段），O 空闲；加 Shift 进一步拉开距离。导航开关键是 Ctrl+Alt+主键盘加号，不冲突。
//   - 开关动作走 LilithApi.OpenMenu/CloseMenu（CharacterController.ToggleSetting → SettingView.Show 兜底）。
//   - 「不自动隐藏」用保活实现：只在**我们自己打开、且用户没有主动关闭**的期间生效；
//     检测到菜单又从可见变不可见时重新打开（有冷却与次数上限，绝不无限刷）。
//     一旦对话开始就停止保活 —— 剧情演出优先，不能为了保住菜单跟游戏状态机抢。
//
// ★ 为什么是「待确认」状态机而不是同步判成败：
//   打开菜单可能带淡入动画/延迟一帧生效，Show() 返回那一刻 CanvasGroup.alpha 可能还是 0。
//   如果同步判成败，就会误判成「没打开」而不进保活。所以按了快捷键先进入 pending，
//   接下来 1.2 秒内每帧看 alpha；看到可见才朗读「功能菜单已打开」并开始保活，超时才报失败。
//
// ★ 已知取舍：如果玩家自己用鼠标点了菜单上的关闭按钮，我们分不清「你关的」和「游戏自动关的」，
//   会重新打开一次。本作窗口是 WS_EX_TRANSPARENT（单击穿透），玩家点不到游戏窗口内的按钮，
//   所以实机上基本不会遇到；万一以后窗口变成可点击，请把「功能菜单不自动隐藏」关掉。

using System;
using System.Collections.Generic;
using UnityEngine;

namespace JustLilithA11y
{
    internal static class LilithMenu
    {
        private const string LogPrefix = "[Menu]";

        /// <summary>保活上限：就算游戏一直想把菜单关掉，也不会无限重开（防日志/CPU 打满）。</summary>
        private const int MaxReshow = 500;
        /// <summary>两次重开之间的最小间隔，避免和游戏的显示/隐藏动画抢成每帧一次。</summary>
        private const float ReshowCooldown = 1.5f;

        /// <summary>菜单关掉后至少等这么久才考虑保活重开（Lead 定的 30 秒，避免和游戏的显示抖动抢）。</summary>
        private const float ClosedBeforeReopen = 30f;
        /// <summary>按下快捷键后等菜单真正可见的最长时间。</summary>
        private const float PendingTimeout = 1.2f;

        private static bool _weOpened;
        private static int _reshowCount;
        private static int _reshowFailures;
        private static float _lastReshowAt;
        private static int _reshowLogs;
        private static float _closedSinceAt;   // 首次观察到「菜单不可见」的时刻（0 = 未记录）
        private static int _focusHoldLogs;
        private static readonly HashSet<string> _errLogged = new HashSet<string>();

        // 打开动作的「待确认」状态（见文件头说明）
        private static bool _pendingOpen;
        private static float _pendingDeadline;
        private static string _pendingHow = "";
        private static bool _pendingKeepAlive;   // true = 这次打开是为了保活重开，不再朗读

        // 快捷键解析缓存：不要在每帧重新解析字符串（Hotkey.Parse 会分配）
        private static bool _keyResolved;
        private static string _keyFor;
        private static Hotkey.Spec _keySpec;
        private static bool _keyOk;

        internal static void Update()
        {
            try
            {
                if (Plugin.CfgMenuKey == null) return;
                PendingCheck();
                if (MenuKeyDown()) OnHotkey();
                KeepAlive();
            }
            catch (Exception e) { LogOnce("update", e); }
        }

        // =====================================================================
        // 快捷键
        // =====================================================================

        private static bool MenuKeyDown()
        {
            string spec = Plugin.CfgMenuKey.Value ?? "";
            if (!_keyResolved || !string.Equals(_keyFor, spec, StringComparison.Ordinal))
            {
                _keyResolved = true;
                _keyFor = spec;
                string why = "";
                _keyOk = Hotkey.Parse(spec, out _keySpec, out why);
                if (_keyOk)
                {
                    Log(LogPrefix + " 打开菜单快捷键 = " + spec);
                }
                else
                {
                    LogWarn(LogPrefix + " 打开菜单快捷键 \"" + spec + "\" 不可识别（" + why +
                            "），本快捷键停用。写法示例：Ctrl+Alt+Shift+O / Ctrl+Alt+Plus / F2");
                }
            }
            if (!_keyOk) return false;
            try { return Hotkey.Down(_keySpec); }
            catch (Exception e) { LogOnce("key-down", e); return false; }
        }

        // =====================================================================
        // 开 / 关
        // =====================================================================

        private static void OnHotkey()
        {
            if (_pendingOpen) return; // 上一次还在等结果，别连点

            if (LilithApi.MenuVisible())
            {
                bool ok = LilithApi.CloseMenu(out string how);
                _weOpened = false;
                _reshowCount = 0;
                _reshowFailures = 0;
                LilithApi.Say(ok ? "功能菜单已关闭。" : "没能关闭功能菜单，请查看日志。");
                Log(LogPrefix + " 用户按快捷键关闭菜单 → ok=" + ok + "（" + how + "）");
                return;
            }

            BeginOpen(false);
        }

        /// <summary>发起打开，并进入「待确认」状态。</summary>
        private static void BeginOpen(bool forKeepAlive)
        {
            // forKeepAlive=true → 只允许走游戏自己的 CharacterController.ToggleSetting()（不碰 SettingView）。
            bool immediate = LilithApi.OpenMenu(out string how, forKeepAlive);

            if (immediate && LilithApi.MenuVisible())
            {
                // 立即生效：直接完成
                OnOpened(how, forKeepAlive);
                return;
            }

            _pendingOpen = true;
            _pendingDeadline = Time.unscaledTime + PendingTimeout;
            _pendingHow = how;
            _pendingKeepAlive = forKeepAlive;
            Log(LogPrefix + " 已发出打开菜单请求（" + how + "），等待确认可见…");
        }

        private static void PendingCheck()
        {
            if (!_pendingOpen) return;

            if (LilithApi.MenuVisible())
            {
                _pendingOpen = false;
                OnOpened(_pendingHow, _pendingKeepAlive);
                return;
            }

            if (Time.unscaledTime < _pendingDeadline) return;

            _pendingOpen = false;
            if (_pendingKeepAlive)
            {
                _reshowFailures++;
                LogWarn(LogPrefix + " 保活重开菜单超时未生效（第 " + _reshowFailures + " 次），继续观察");
            }
            else
            {
                LilithApi.Say("没能打开功能菜单，请查看日志。");
                LogWarn(LogPrefix + " 打开菜单超时未生效（" + _pendingHow + "），alpha=" +
                        LilithApi.MenuAlpha().ToString("0.00"));
            }
        }

        private static void OnOpened(string how, bool forKeepAlive)
        {
            _weOpened = true;
            _closedSinceAt = 0f;
            _focusHoldLogs = 0;
            _reshowCount = 0;
            _reshowFailures = 0;
            _lastReshowAt = Time.unscaledTime;
            if (!forKeepAlive) LilithApi.Say("功能菜单已打开。");
            Log(LogPrefix + (forKeepAlive ? " 保活重开成功" : " 菜单已打开") + "（" + how +
                "，alpha=" + LilithApi.MenuAlpha().ToString("0.00") + "）");
        }

        // =====================================================================
        // 保活（菜单不自动隐藏）
        // =====================================================================

        private static void KeepAlive()
        {
            if (!_weOpened) return;
            if (_pendingOpen) return; // 正在等一次打开的结果，别叠加

            bool keep = true;
            try { if (Plugin.CfgMenuKeepAlive != null) keep = Plugin.CfgMenuKeepAlive.Value; }
            catch { }
            if (!keep) { _weOpened = false; return; }

            // 剧情优先：对话一开始就停止保活，避免和游戏状态机抢面板
            bool dialogue = false;
            try { dialogue = Reader.DialogueActive; } catch { }
            if (dialogue)
            {
                _weOpened = false;
                Log(LogPrefix + " 对话已开始 → 停止菜单保活");
                return;
            }

            if (LilithApi.MenuVisible()) { _closedSinceAt = 0f; return; }

            // ★ 用户主动关闭的识别：本作窗口单击穿透（点不到窗口里的按钮），所以玩家关闭菜单只有两条路：
            //   ① 再按一次我们的快捷键 —— 那条在 OnHotkey 里已经把 _weOpened 清掉了，走不到这里；
            //   ② 在**导航模式里**选中「关闭/取消」并回车 —— 平台层的 Activate 我们看不到，
            //      但那一刻导航模式是开着的。所以：菜单不可见 + 导航模式开着 = 大概率是用户关的 →
            //      停止保活，别把它重新弹出来跟用户较劲。
            //   （游戏自己在导航模式里自动关菜单的概率很低；就算误判，代价只是不再保活。）
            bool navActive = false;
            try { navActive = UiNav.Active; } catch { }
            if (navActive)
            {
                _weOpened = false;
                Log(LogPrefix + " 菜单已关闭且当前在导航模式 → 判定为用户主动关闭，停止保活");
                return;
            }

            // ★ 重开条件（Lead 定，2026-10-23）：
            //   ① 菜单已经关闭 ≥ 30 秒（`ClosedBeforeReopen`）——短暂的显示/隐藏抖动不去抢；
            //   ② 游戏窗口当前**没有焦点**（`WindowFocused()` 为假）——本作窗口是
            //      WS_EX_NOACTIVATE + 单击穿透，玩家真正在用时通常是「别处有焦点、游戏在后台」，
            //      此时把菜单保持可见才有意义；一旦玩家把焦点给它（准备操作），我们就别再自作主张。
            //   ③ 只走 CharacterController.ToggleSetting()（`toggleOnly: true`），**绝不调 SettingView.Show()**：
            //      实测 SettingView.Toggle() 完整往返才收子面板，保活里直接 Show() 会打乱玩家展开的视图。
            //   「Settings 失焦自动关」是游戏自己的行为 —— 我们只在它关掉之后、且玩家不在操作时补回来。
            float closedFor = Time.unscaledTime - _closedSinceAt;
            if (_closedSinceAt <= 0f) { _closedSinceAt = Time.unscaledTime; return; }
            if (closedFor < ClosedBeforeReopen) return;
            if (WindowFocused())
            {
                if (_focusHoldLogs < 3)
                {
                    _focusHoldLogs++;
                    Log(LogPrefix + " 菜单已关 " + closedFor.ToString("0") +
                        " 秒，但游戏窗口有焦点 → 暂不重开（玩家正在操作）");
                }
                return;
            }

            if (_reshowCount >= MaxReshow)
            {
                _weOpened = false;
                Log(LogPrefix + " 菜单已被隐藏 " + _reshowCount + " 次，停止保活（上限 " + MaxReshow + "）");
                return;
            }
            if (_reshowFailures >= 3)
            {
                _weOpened = false;
                LogWarn(LogPrefix + " 连续 3 次重开都没生效 → 停止保活（游戏可能换了面板实现）");
                return;
            }

            float now = Time.unscaledTime;
            if (now - _lastReshowAt < ReshowCooldown) return;
            _lastReshowAt = now;
            _reshowCount++;

            if (_reshowLogs < 5) _reshowLogs++;
            if (_reshowLogs < 5) Log(LogPrefix + " 菜单被隐藏 → 重新打开（第 " + _reshowCount + " 次）");
            else { try { A11yHost.Diag(LogPrefix + " 重开 #" + _reshowCount); } catch { } }

            BeginOpen(true);
        }

        // =====================================================================
        // 窗口焦点（保活重开的判据之一）
        // =====================================================================

        /// <summary>
        /// 游戏窗口当前是否有键鼠焦点。
        /// 依据：本作窗口是 WS_EX_NOACTIVATE + 单击穿透，玩家「在用」的状态通常是焦点在别处；
        /// 保活只在没有焦点时补菜单，避免和玩家正要点的东西抢。
        /// 失败一律返回 true（当作有焦点 → 不重开，宁可不补也别打扰）。
        /// </summary>
        private static bool WindowFocused()
        {
            try
            {
                IntPtr fg = GetForegroundWindow();
                if (fg == IntPtr.Zero) return true;
                uint pid;
                GetWindowThreadProcessId(fg, out pid);
                if (pid == 0) return true;
                return pid == (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            }
            catch { return true; }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        // =====================================================================
        // 小工具
        // =====================================================================

        private static void Log(string msg)
        {
            try { A11yHost.Log?.LogInfo(msg); } catch { }
        }

        private static void LogWarn(string msg)
        {
            try { A11yHost.Log?.LogWarning(msg); } catch { }
        }

        private static void LogOnce(string where, Exception e)
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
            _weOpened = false;
            _reshowCount = 0;
            _reshowFailures = 0;
            _reshowLogs = 0;
            _lastReshowAt = 0f;
            _closedSinceAt = 0f;
            _focusHoldLogs = 0;
            _pendingOpen = false;
            _pendingHow = "";
            _keyResolved = false;
            _keyFor = null;
            _keyOk = false;
        }
    }
}



