using System;
using HarmonyLib;

namespace JustLilithA11y
{
    /// <summary>
    /// Harmony 补丁（**默认全部不挂**，只有「其它 / 挂载 Harmony 补丁」打开时才挂）。
    ///
    /// === 为什么默认不挂（本作第一版的设计决定）===
    /// 本作是 IL2CPP。BepInEx 的 interop 生成的 Assembly-CSharp 里，DialogueManager / DialogueNode
    /// 这些类型是**托管代理**，每个方法体都是
    ///     `IL2CPP.il2cpp_runtime_invoke(NativeMethodInfoPtr_..., ptr, args, ref exc)`
    /// 把调用转发进 native。Harmony 能打的是「托管代理方法」这一层 —— 它一定生效（转发之前/之后都会跑到），
    /// 但它比事件晚、比事件脏：
    ///   · DialogueManager.Say 也用于「没有气泡的临时台词」（ForceSay / 系统提示），拦下来容易多念；
    ///   · 一个节点可能 Say 多次（打字机重播、快进重发），要靠去重兜底；
    ///   · 补丁一旦打错（参数名对不上），Harmony 抛异常但**其他补丁仍然生效**，症状极隐蔽（见 G2）。
    ///
    /// === 为什么还是留着 ===
    /// 事件订阅（Reader 里的反射挂 4 个 OnDialogue*）如果因为 il2cpp 委托互操作失败而全线失效，
    /// 本文件就是第三条腿：不必等改代码重发一版，打开配置就能验证「到底是事件挂了还是抓不到数据」。
    /// 打开后 Reader 会把它当 source="say-patch" 记录，日志里能直接看出走的是哪条路径。
    ///
    /// === 两条最容易踩的坑（沿用流水线纪律）===
    ///   G1  迭代器方法**绝不能**用「Prefix 返回 false 跳过」—— 它会返回 null，
    ///       而调用方是 StartCoroutine(...)。只能改 ref 参数。
    ///   G2  Harmony 后缀的**参数名必须和运行时一致**（本文件的参数名逐个来自反编译实查的
    ///       DialogueManager.cs 方法签名：text / emotion / duration / lineId / textIsFinal）。
    ///       拿不准就改用 [HarmonyArgument(n)] 按位置取。
    ///
    /// 补丁点一览（全部来自反编译实查，不是猜的）：
    ///   挂载点                                                        作用
    ///   DialogueManager.Say(string, string, float, int, bool)          配音/无配音台词都从这里过 → Reader.OnSaidText
    ///       （DialogueManager.cs:3705）
    ///   DialogueManager.Say(string, LilithActionType, string, float, int, bool)
    ///       （DialogueManager.cs:3687，带动作的版本）
    /// 不用补丁的地方（有更稳的挂载点）：
    ///   推进剧情 / 选项选择  → 订阅 DialogueManager.OnDialogueAdvance / OnOptionSelected
    ///   菜单 / 设置键盘导航   → UiNav 每帧扫描 Selectable（逐作层在 UiNav.Game.cs 补标签）
    /// </summary>
    internal static class Patches
    {
        /// <summary>按配置决定挂不挂；返回实际挂载的补丁组数量。</summary>
        internal static int Apply(Harmony harmony)
        {
            if (harmony == null) return 0;

            bool on = false;
            try { on = Plugin.CfgHarmonyPatches != null && Plugin.CfgHarmonyPatches.Value; }
            catch { }

            if (!on)
            {
                SafeLog("[Patches] Harmony 补丁未挂载（配置「挂载 Harmony 台词补丁」= 关）。" +
                        "台词走事件订阅 / 每帧轮询，见 Reader 日志里的 path=。");
                return 0;
            }

            try
            {
                harmony.PatchAll(typeof(SayPlain));
                SafeLog("[Patches] 已挂载 DialogueManager.Say(string,string,float,int,bool) 后缀。");
                int n = 1;
                try
                {
                    harmony.PatchAll(typeof(SayWithAction));
                    SafeLog("[Patches] 已挂载 DialogueManager.Say(string,LilithActionType,...) 后缀。");
                    n = 2;
                }
                catch (Exception e2)
                {
                    SafeLog("[Patches] 第二个 Say 重载挂载失败（不影响第一个）：" + e2.GetType().Name + ": " + e2.Message);
                }
                return n;
            }
            catch (Exception e)
            {
                // ★ PatchAll 抛异常时其他补丁仍然生效 —— 必须 LogError 而不是吞掉
                SafeLog("[Patches] PatchAll 失败: " + e.GetType().Name + ": " + e.Message);
                return 0;
            }
        }

        private static void SafeLog(string msg)
        {
            try { A11yHost.Log?.LogInfo(msg); } catch { }
        }

        /// <summary>
        /// DialogueManager.Say(string text, string emotion = "", float duration = 3f,
        ///                      int lineId = 0, bool textIsFinal = false)   （DialogueManager.cs:3705）
        ///
        /// 参数名逐个照抄反编译（G2）。只取 text / lineId —— 其余参数名也必须写对，
        /// 因为 Harmony 是按名字绑定的。
        /// </summary>
        [HarmonyPatch(typeof(DialogueManager), "Say",
            new Type[] { typeof(string), typeof(string), typeof(float), typeof(int), typeof(bool) })]
        internal static class SayPlain
        {
            [HarmonyPostfix]
            private static void Post(string text, int lineId)
            {
                try { Reader.OnSaidText(text, lineId); }
                catch (Exception e) { SafeLog("[Patches] Say 后缀异常（只记一次不刷屏）: " + e.GetType().Name + ": " + e.Message); }
            }
        }

        /// <summary>
        /// DialogueManager.Say(string text, LilithActionType actionType, string emotion = "",
        ///                      float duration = 3f, int lineId = 0, bool textIsFinal = false)
        /// （DialogueManager.cs:3687，带表情动作的版本）
        ///
        /// LilithActionType 在全局命名空间（Assembly-CSharp），这里直接按类型引用；
        /// 万一运行时类型名不一致，PatchAll 会抛异常并被 Apply 捕获，不会拖垮其他补丁。
        /// </summary>
        [HarmonyPatch(typeof(DialogueManager), "Say",
            new Type[] { typeof(string), typeof(LilithActionType), typeof(string), typeof(float), typeof(int), typeof(bool) })]
        internal static class SayWithAction
        {
            [HarmonyPostfix]
            private static void Post(string text, int lineId)
            {
                try { Reader.OnSaidText(text, lineId); }
                catch (Exception e) { SafeLog("[Patches] Say(动作版) 后缀异常: " + e.GetType().Name + ": " + e.Message); }
            }
        }
    }
}
