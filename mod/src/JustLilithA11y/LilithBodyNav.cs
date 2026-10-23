// 不/存在的莉莉丝 无障碍补丁 —— 逐作层「身体部位导航组」
//
// 需求来源（用户 m01949）：莉莉丝的头/脸/胸口/身体/左右手/裙子/腿要能当成导航项，
// 回车触发对应交互。
//
// 为什么用「隐藏的虚拟控件」而不是给平台层加接口：
//   平台层 UiNav 只认 `Selectable`（扫描 = Resources.FindObjectsOfTypeAll<Selectable>()，
//   且要求 1) 在 Canvas 下 2) 有 RectTransform 3) isActiveAndEnabled 4) 可见性通过
//   CanvasGroup.alpha > 0.05 5) 射线能从自己身上打回来）。
//   游戏里那 8 个部位对象（`Lilith/Coliders` 下的 Head/Face/…）只是 Collider2D + CharacterHitArea，
//   根本不是 Selectable，也不在 Canvas 下；而且实机测到它们 active=False、collider bounds=0x0，
//   连 raycast 都用不了。所以这里**主动造一个 Canvas**，放 8 个「看不见但可被扫描到」的 Button：
//     - CanvasGroup.alpha = 1（平台层的可见性门槛看它，必须为 1）
//     - Image.color.a = 0（视觉上完全透明，玩家看不到，但 raycastTarget 必须保持 true，
//       否则平台层「射线打得到自己」那一步会失败，控件会被当成不可见剔除）
//     - 每个按钮 8x8 像素，竖在窗口左下角边缘（占据 80x8 像素，不挡任何真实 UI 的命中区）
//     - 回车 → Button.onClick.Invoke() → 我们挂的监听 → 调 LilithApi.TriggerPart(部位)
//
// ★ 关键实现细节（编译探针实测，2026-10-22）：
//   Il2CppInterop 里 `UnityEngine.Events.UnityAction` **不是** C# 委托，而是包装类，
//   但它带 `implicit operator UnityAction(Action)`。所以正确写法是：
//       Action a = () => { ... };
//       UnityEngine.Events.UnityAction ua = a;      // 隐式转换，能编译
//       btn.onClick.AddListener(ua);                // 能编译
//   直接写 `btn.onClick.AddListener(() => ...)` 会因为 lambda 没有自然类型而编不过（C# 9）。
//
// ★ 命名冲突：Assembly-CSharp 有全局 `CharacterController`，UnityEngine 里也有一个；
//   本工程没引用 UnityEngine.PhysicsModule，所以不冲突（详见 LilithApi.cs 头注释）。

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace JustLilithA11y
{
    internal static class LilithBodyNav
    {
        private const string LogPrefix = "[Body]";

        /// <summary>一个部位导航项：枚举类型 + Coliders 子对象名（锚点）+ 中文标签 + 对象名后缀。</summary>
        private struct Part
        {
            internal CharacterHitAreaType Type;
            internal string Anchor;
            internal string Label;
            internal string Suffix;
        }

        /// <summary>
        /// 8 个部位。顺序按「从上到下」排（头→脸→胸口→身体→手→裙子→腿），
        /// 方向键上下移动时符合身体直觉。
        /// 锚点名来自实机日志：`Lilith/Coliders` 下 Head/Face/Chest/Body/HandLeft/HandRight/Skirt/Leg。
        /// </summary>
        private static readonly Part[] Parts = new Part[]
        {
            new Part { Type = CharacterHitAreaType.Head,  Anchor = "Head",     Label = "头",   Suffix = "Head"      },
            new Part { Type = CharacterHitAreaType.Face,  Anchor = "Face",     Label = "脸",   Suffix = "Face"      },
            new Part { Type = CharacterHitAreaType.Chest, Anchor = "Chest",    Label = "胸口", Suffix = "Chest"     },
            new Part { Type = CharacterHitAreaType.Body,  Anchor = "Body",     Label = "身体", Suffix = "Body"      },
            new Part { Type = CharacterHitAreaType.Hand,  Anchor = "HandLeft", Label = "左手", Suffix = "HandLeft"  },
            new Part { Type = CharacterHitAreaType.Hand,  Anchor = "HandRight",Label = "右手", Suffix = "HandRight" },
            new Part { Type = CharacterHitAreaType.Skirt, Anchor = "Skirt",    Label = "裙子", Suffix = "Skirt"     },
            new Part { Type = CharacterHitAreaType.Leg,   Anchor = "Leg",      Label = "腿",   Suffix = "Leg"       },
        };

        private static GameObject _root;
        private static GameObject _container;
        private static Canvas _canvas;
        private static float _nextBuildTry;
        private static int _buildCount;
        private static int _clickCount;
        private static readonly HashSet<string> _errLogged = new HashSet<string>();

        /// <summary>部位中文标签（UiNav.Game.cs 的 GameLabelOf 也查这张表）。</summary>
        internal static string LabelOfSuffix(string suffix)
        {
            if (string.IsNullOrEmpty(suffix)) return "";
            for (int i = 0; i < Parts.Length; i++)
            {
                if (string.Equals(Parts[i].Suffix, suffix, StringComparison.OrdinalIgnoreCase))
                    return Parts[i].Label;
            }
            return "";
        }

        /// <summary>按对象名后缀找部位并触发（供将来热键/调试用）。</summary>
        internal static bool TriggerBySuffix(string suffix)
        {
            for (int i = 0; i < Parts.Length; i++)
            {
                if (string.Equals(Parts[i].Suffix, suffix, StringComparison.OrdinalIgnoreCase))
                {
                    return LilithApi.TriggerPart(Parts[i].Type, Parts[i].Anchor);
                }
            }
            return false;
        }

        internal static void Update()
        {
            try
            {
                bool enabled = true;
                try { if (Plugin.CfgBodyNav != null) enabled = Plugin.CfgBodyNav.Value; }
                catch { }

                if (!enabled)
                {
                    DestroyCanvas("配置关闭了身体部位导航组");
                    return;
                }

                EnsureCanvas();
            }
            catch (Exception e) { LogOnce("update", e); }
        }

        // =====================================================================
        // 建 Canvas
        // =====================================================================

        private static void EnsureCanvas()
        {
            // 场景切换会把我们的 Canvas 一起销毁（Unity 的「假 null」语义会让 _root != null 为假），
            // 所以每帧检查、必要时重建；不做 DontDestroyOnLoad：换场景后重新建在新场景里，
            // 平台层的扫描（只扫当前场景）才能扫到。
            if (_root != null && _canvas != null) return;

            float now = Time.unscaledTime;
            if (now < _nextBuildTry) return;
            _nextBuildTry = now + 2f;

            BuildCanvas();
        }

        private static void BuildCanvas()
        {
            try
            {
                var go = new GameObject(LilithApi.BodyCanvasName);

                _canvas = go.AddComponent<Canvas>();
                _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                // 覆盖排序 + 极高的 sortingOrder：保证平台层的 EventSystem.RaycastAll 第一个命中的
                // 就是我们（否则会被游戏自己的面板挡住 → 控件被判不可见 → 不参与导航）。
                _canvas.overrideSorting = true;
                _canvas.sortingOrder = 32000;

                // 没有 GraphicRaycaster，这个 Canvas 里的 Image 不会参与射线检测，
                // 平台层的「射线能打到自己」判定必然失败。
                go.AddComponent<GraphicRaycaster>();

                // ★ 平台层的可见性门槛读的是 CanvasGroup.alpha（<= 0.05 判不可见），
                //   所以这里必须是 1，隐藏只能靠 Image 自身的 alpha=0。
                var group = go.AddComponent<CanvasGroup>();
                group.alpha = 1f;
                group.interactable = true;
                group.blocksRaycasts = true;

                var rootRect = go.GetComponent<RectTransform>();
                if (rootRect != null)
                {
                    rootRect.anchorMin = Vector2.zero;
                    rootRect.anchorMax = Vector2.one;
                    rootRect.offsetMin = Vector2.zero;
                    rootRect.offsetMax = Vector2.zero;
                }

                // ★★ 关键：平台层按「Canvas 下**最顶层子物体**」分组（UiNav.GroupOf）。
                //    上一版把 8 个 Button 直接挂在 Canvas 下 → 每个 Button 各自成一组，
                //    实机扫出「10 组：LilithPart_Leg(1) LilithPart_Skirt(1) …」，
                //    组名还退化成「面板1..8」。所以必须在 Canvas 与按钮之间加**一层容器**：
                //    容器的名字就是平台上看到的组名来源 → 必须是 LilithApi.BodyCanvasName
                //    （GameGroupName 认它 → 「莉莉丝交互」），8 个按钮全部做它的子物体。
                var container = new GameObject(LilithApi.BodyCanvasName + "Container");
                container.transform.SetParent(go.transform, false);

                // 容器自己也要能通过平台的可见性判定：RectTransform 有非零尺寸（否则矩形求交失败）、
                // 在屏内（锚在左下角 + 300x100 一定在屏内）、CanvasGroup.alpha=1。
                var containerRect = container.AddComponent<RectTransform>();
                containerRect.anchorMin = Vector2.zero;
                containerRect.anchorMax = Vector2.zero;
                containerRect.pivot = Vector2.zero;
                containerRect.sizeDelta = new Vector2(300f, 100f);
                containerRect.anchoredPosition = Vector2.zero;

                var containerGroup = container.AddComponent<CanvasGroup>();
                containerGroup.alpha = 1f;
                containerGroup.interactable = true;
                containerGroup.blocksRaycasts = true;

                int hooked = 0;
                for (int i = 0; i < Parts.Length; i++)
                {
                    if (CreateButton(container.transform, Parts[i], i)) hooked++;
                }

                _root = go;
                _container = container;
                _buildCount++;
                Log(LogPrefix + " 已创建身体部位导航组（" + Parts.Length + " 项，成功挂接 " + hooked +
                    " 项，组容器=" + container.name + "，第 " + _buildCount + " 次创建）");
                try
                {
                    A11yHost.Diag(LogPrefix + " canvas=" + LilithApi.BodyCanvasName +
                                  " sortingOrder=32000 items=" + Parts.Length);
                }
                catch { }
            }
            catch (Exception e)
            {
                LogOnce("build-canvas", e);
                try { if (_root != null) UnityEngine.Object.Destroy(_root); } catch { }
                _root = null;
                _canvas = null;
            }
        }

        private static bool CreateButton(Transform parent, Part part, int index)
        {
            try
            {
                var go = new GameObject(LilithApi.PartPrefix + part.Suffix);
                go.transform.SetParent(parent, false);

                var rect = go.AddComponent<RectTransform>();
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.zero;
                rect.pivot = Vector2.zero;
                rect.sizeDelta = new Vector2(8f, 8f);
                // 竖排在窗口左下角边缘：8 项 * 10px 高 = 80px，宽 8px，完全避开游戏 UI 的主要命中区
                rect.anchoredPosition = new Vector2(6f, 6f + 10f * index);

                var img = go.AddComponent<Image>();
                img.color = new Color(1f, 1f, 1f, 0f); // 完全透明：玩家看不见
                img.raycastTarget = true;              // 但必须参与射线，否则平台层判不可见

                var btn = go.AddComponent<Button>();
                btn.targetGraphic = img;
                btn.transition = Selectable.Transition.None; // 不要颜色变化（本来就是透明的）
                btn.interactable = true;

                HookClick(btn, part);
                return true;
            }
            catch (Exception e)
            {
                LogOnce("create-" + part.Suffix, e);
                return false;
            }
        }

        /// <summary>
        /// 挂点击回调。Il2CppInterop 的 UnityAction 是包装类（带 implicit operator Action），
        /// 所以必须先落到 System.Action 再隐式转换（见文件头说明）。
        /// </summary>
        private static void HookClick(Button btn, Part part)
        {
            try
            {
                Action managed = () => OnPartClicked(part);
                UnityEngine.Events.UnityAction ua = managed;
                btn.onClick.AddListener(ua);
                Log(LogPrefix + " 已挂接 onClick：" + part.Suffix + " → " + part.Label);
            }
            catch (Exception e)
            {
                LogOnce("hook-" + part.Suffix, e);
                LogWarn(LogPrefix + " 挂接 onClick 失败：" + part.Suffix + "（该部位回车不会有反应）");
            }
        }

        private static void OnPartClicked(Part part)
        {
            try
            {
                _clickCount++;
                Log(LogPrefix + " 导航激活部位「" + part.Label + "」→ " + part.Type + " @ " + part.Anchor +
                    "（第 " + _clickCount + " 次）");
                bool ok = LilithApi.TriggerPart(part.Type, part.Anchor);
                if (!ok)
                {
                    LilithApi.Say("这个部位现在没有反应。");
                }
            }
            catch (Exception e) { LogOnce("on-click", e); }
        }

        private static void DestroyCanvas(string reason)
        {
            if (_root == null) return;
            try
            {
                UnityEngine.Object.Destroy(_root);
                Log(LogPrefix + " 已销毁身体部位导航组（" + reason + "）");
            }
            catch (Exception e) { LogOnce("destroy", e); }
            _root = null;
            _canvas = null;
        }

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
            _root = null;
            _canvas = null;
            _nextBuildTry = 0f;
            _clickCount = 0;
        }
    }
}

