using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UI.TraySettingNew;

namespace LilithProbe
{
    /// <summary>
    /// 只读运行时探针 v2：可见性（CanvasGroup 链）过滤后的控件/文本、游戏自带 HotkeyService 默认绑定、
    /// 对话事件（只订阅一次）、音频播放状态。写 BepInEx\LogOutput.log。
    /// </summary>
    [BepInPlugin("justlilith.probe", "LilithProbe", "0.0.3.0")]
    public class ProbePlugin : BasePlugin
    {
        internal static ManualLogSource Log2;

        public override void Load()
        {
            Log2 = Log;
            string path = Path.Combine(Paths.GameRootPath, "probe_runtime.log");
            try { File.Delete(path); } catch { }
            W("probe build 0.0.4");
            W("unity=" + Application.unityVersion + " gameVersion=" + Application.version + " product=" + Application.productName);
            W("dataPath=" + Application.dataPath + " screen=" + Screen.width + "x" + Screen.height);
            W("inputSystemAssemblyLoaded=" + (Type.GetType("UnityEngine.InputSystem.InputSystem, Unity.InputSystem") != null));
            try
            {
                var k = Input.GetKey(KeyCode.LeftControl);
                W("legacy Input.GetKey OK (LeftControl=" + k + ")");
            }
            catch (Exception ex) { W("legacy Input.GetKey THROWS: " + ex.GetType().Name + ": " + ex.Message); }

            try { DumpHotkeys(); } catch (Exception ex) { W("[hotkey] dump error " + ex.GetType().Name + ": " + ex.Message); }

            TryPatch("Say", new Type[] { typeof(string), typeof(LilithActionType), typeof(string), typeof(float), typeof(int), typeof(bool) }, nameof(SayPrefix));
            TryPatch("AdvanceDialogue", Type.EmptyTypes, nameof(AdvancePrefix));
            TryPatch("SelectOption", new Type[] { typeof(int) }, nameof(SelectPrefix));

            try
            {
                var runner = AddComponent<ProbeRunner>();
                W("[probe] runner added: " + (runner != null));
            }
            catch (Exception ex) { W("[probe] AddComponent FAILED: " + ex.GetType().Name + ": " + ex.Message); }
        }

        internal static void W(string s)
        {
            try { Log2.LogInfo(s); } catch { }
            try { File.AppendAllText(Path.Combine(Paths.GameRootPath, "probe_runtime.log"), s + "\n", Encoding.UTF8); } catch { }
        }

        /// <summary>游戏自带热键系统的默认绑定（Win32 VK 体系，见 HotkeyService/HotkeyCombo），用于核对冲突。</summary>
        private static void DumpHotkeys()
        {
            W("[hotkey] IsSupported=" + Safe(() => HotkeyService.IsSupported.ToString()));
            foreach (HotkeyAction a in Enum.GetValues(typeof(HotkeyAction)))
            {
                try
                {
                    var d = HotkeyService.Get(a);
                    if (d == null) { W("[hotkey] " + a + " descriptor=null"); continue; }
                    var def = d.Default;
                    var cur = HotkeyService.GetCombo(a);
                    W("[hotkey] action=" + a + " id=" + d.Id + " item=" + d.SettingItemId +
                      " default(mods=" + def.Mods + ",key=" + def.Key + ",'" + SafeStr(() => def.ToDisplayString()) + "')" +
                      " current(mods=" + cur.Mods + ",key=" + cur.Key + ",'" + SafeStr(() => cur.ToDisplayString()) + "')");
                }
                catch (Exception ex) { W("[hotkey] " + a + " error " + ex.GetType().Name + ": " + ex.Message); }
            }
        }

        private static string Safe(Func<string> f) { try { return f(); } catch (Exception ex) { return "ERR:" + ex.GetType().Name; } }
        internal static string SafeStr(Func<string> f) { try { return f() ?? ""; } catch { return "ERR"; } }

        private static void TryPatch(string methodName, Type[] args, string prefixName)
        {
            try
            {
                var t = typeof(DialogueManager);
                var m = args.Length == 0
                    ? t.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    : t.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly, null, args, null);
                if (m == null) { W("[harmony] method not found: " + methodName); return; }
                var pfx = typeof(ProbePlugin).GetMethod(prefixName, BindingFlags.Static | BindingFlags.NonPublic);
                new Harmony("justlilith.probe").Patch(m, prefix: new HarmonyMethod(pfx));
                var info = Harmony.GetPatchInfo(m);
                W("[harmony] patched " + methodName + " ok, prefixes=" + (info == null ? 0 : info.Prefixes.Count));
            }
            catch (Exception ex)
            {
                W("[harmony] patch " + methodName + " FAILED: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void SayPrefix() { try { _sayHits++; if (_sayHits <= 40) W("[hit] DialogueManager.Say #" + _sayHits); } catch { } }
        private static void AdvancePrefix() { try { _advanceHits++; if (_advanceHits <= 20) W("[hit] AdvanceDialogue #" + _advanceHits); } catch { } }
        private static void SelectPrefix() { try { _selectHits++; if (_selectHits <= 20) W("[hit] SelectOption #" + _selectHits); } catch { } }

        internal static int _sayHits, _advanceHits, _selectHits;
        internal static bool _subscribed;
    }

    public class ProbeRunner : MonoBehaviour
    {
        private int _dumps;
        private float _next;
        private int _done;

        private void Awake()
        {
            ProbePlugin.W("[probe] runner Awake");
            _next = Time.unscaledTime + 3f;
        }

        private void Update()
        {
            try
            {
                if (Input.GetKeyDown(KeyCode.F9)) OpenTraySetting();
                if (Input.GetKeyDown(KeyCode.P)) DumpScanAndGroups();
                if (Input.GetKeyDown(KeyCode.F7)) DumpSettingViews();
                if (Input.GetKeyDown(KeyCode.F10)) DumpUiNavState();
                if (Input.GetKeyDown(KeyCode.F11)) DumpHeldKeys();
                if (Input.GetKeyDown(KeyCode.F12)) SimNavStep();
                if (Input.GetKeyDown(KeyCode.F4)) SimDumpLabels();
                if (Input.GetKeyDown(KeyCode.F3)) DumpUiNavMembers();
                if (Input.GetKeyDown(KeyCode.F2)) SimActivate();
                if (Input.GetKeyDown(KeyCode.F1)) SimPlatformActivate();
                if (Input.GetKeyDown(KeyCode.F8)) SimClickLilith();

                if (Input.GetKeyDown(KeyCode.F5)) SimNavGroup(1);
                if (Input.GetKeyDown(KeyCode.F6)) SimNavGroup(-1);
            }
            catch (Exception ex) { ProbePlugin.W("[hotkey-op] " + ex.GetType().Name + ": " + ex.Message); }

            if (Time.unscaledTime < _next || _done >= 8) return;
            _next = Time.unscaledTime + 6f;
            _done++;
            try { Dump(); } catch (Exception ex) { ProbePlugin.W("[dump-error] " + ex.GetType().Name + ": " + ex.Message); }
            if (_done >= 8)
                ProbePlugin.W("=== probe loop done, sayHits=" + ProbePlugin._sayHits + " advanceHits=" + ProbePlugin._advanceHits + " selectHits=" + ProbePlugin._selectHits + " ===");
        }

        // ---- F12 / F5 / F6：绕过键盘注入限制，直接驱动 UiNav 的「下一项 / 切组」状态机 ----
        // 自动化环境注入不了方向键（daemon 只认字母/数字/tab/enter/space/escape，
        // 本进程 SendInput 被静默丢弃），所以用反射直接改 _index 再调用 Announce。
        // 这样验的是「标签解析 + 朗读」这条链路；方向键分支本身靠代码走查。
        private static Type FindA11yType(string name)
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var t = a.GetType(name, false);
                    if (t != null) return t;
                }
                catch { }
            }
            return null;
        }

        private static bool _methodsDumped;

        private static int LCount(object list)
        {
            if (list == null) return 0;
            var p = list.GetType().GetProperty("Count");
            return p == null ? 0 : Convert.ToInt32(p.GetValue(list));
        }
        private static object LItem(object list, int i)
        {
            if (list == null) return null;
            var m = list.GetType().GetMethod("get_Item", new[] { typeof(int) });
            return m == null ? null : m.Invoke(list, new object[] { i });
        }

        // P：强制重扫导航并打印分组/条目，用于点击 Lilith 后检查是否出现新面板
        private static void DumpScanAndGroups()
        {
            const BindingFlags F = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
            try
            {
                var t = FindA11yType("JustLilithA11y.UiNav");
                if (t == null) { ProbePlugin.W("[scan] UiNav type not found"); return; }
                var scan = t.GetMethod("Scan", F, null, Type.EmptyTypes, null);
                ProbePlugin.W("[scan] Scan method=" + (scan != null));
                try { scan?.Invoke(null, null); } catch (Exception e) { ProbePlugin.W("[scan] invoke-err " + e.GetType().Name + " " + (e.InnerException?.Message ?? "")); }
                var groups = t.GetProperty("Groups", F)?.GetValue(null);
                ProbePlugin.W("[scan] groups=" + LCount(groups));
                for (int gi = 0; gi < LCount(groups) && gi < 20; gi++)
                {
                    var g = LItem(groups, gi);
                    var gt = g.GetType();
                    var root = gt.GetProperty("Root", F)?.GetValue(g) as Component;
                    var its = gt.GetProperty("Items", F)?.GetValue(g);
                    ProbePlugin.W("[scan] G" + gi + " root=" + (root == null ? "?" : root.gameObject.name)
                                  + " path=" + (root == null ? "?" : PathOf(root.transform)) + " items=" + LCount(its));
                    for (int k = 0; k < LCount(its) && k < 20; k++)
                    {
                        var s = LItem(its, k) as Selectable;
                        ProbePlugin.W("[scan]   G" + gi + " #" + (k + 1) + " " + (s == null ? "null" : s.gameObject.name));
                    }
                }
                var tmps = Resources.FindObjectsOfTypeAll(Il2CppType.Of<TMP_Text>());
                int cnt = 0;
                foreach (var o in tmps)
                {
                    var tt = o.TryCast<TMP_Text>();
                    if (tt == null) continue;
                    if (!tt.gameObject.activeInHierarchy) continue;
                    if (AlphaOf(tt.transform) <= 0.05f) continue;
                    var v = tt.text;
                    if (string.IsNullOrEmpty(v)) continue;
                    if (cnt++ >= 25) break;
                    ProbePlugin.W("[scan-text] " + PathOf(tt.transform) + " => " + v);
                }
            }
            catch (Exception ex) { ProbePlugin.W("[scan-error] " + ex.GetType().Name + ": " + ex.Message); }
        }
        private static void SimNavStep()
        {
            try
            {
                var t = FindA11yType("JustLilithA11y.UiNav");
                if (t == null) { ProbePlugin.W("[simnav] UiNav 类型未找到"); return; }
                const BindingFlags F = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
                bool active = (bool)(t.GetField("_active", F)?.GetValue(null) ?? false);
                object items = t.GetProperty("Items", F)?.GetValue(null) ?? t.GetField("Items", F)?.GetValue(null);
                FieldInfo idxF = t.GetField("_index", F);
                if (idxF == null || items == null) { ProbePlugin.W("[simnav] 取不到 _index/Items"); return; }
                var list = items as System.Collections.IList;
                int n = (list != null) ? list.Count : 0;
                int old = (int)idxF.GetValue(null);
                int now = n > 0 ? (old + 1) % n : 0;
                idxF.SetValue(null, now);
                MethodInfo ann = t.GetMethod("Announce", F, null, new[] { typeof(string) }, null);
                ProbePlugin.W("[simnav] active=" + active + " items=" + n + " index " + old + "->" + now
                              + " announce=" + (ann != null));
                if (ann != null && active) ann.Invoke(null, new object[] { "" });
            }
            catch (Exception ex) { ProbePlugin.W("[simnav-error] " + ex.GetType().Name + ": " + ex.Message); }
        }

        // F3：dump UiNav 的静态字段/属性，确认索引与条目列表的真实成员名
        private static void DumpUiNavMembers()
        {
            try
            {
                var t = FindA11yType("JustLilithA11y.UiNav");
                if (t == null) { ProbePlugin.W("[members] UiNav 类型未找到"); return; }
                const BindingFlags F = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
                ProbePlugin.W("[members] type=" + t.FullName + " asm=" + t.Assembly.GetName().Name);
                foreach (var f in t.GetFields(F))
                {
                    object v = null;
                    try { if (f.IsStatic) v = f.GetValue(null); } catch { }
                    string sv = v == null ? "null" : v.ToString();
                    if (sv.Length > 60) sv = sv.Substring(0, 60) + "…";
                    ProbePlugin.W("[members] field " + f.Name + " : " + f.FieldType.Name + " = " + sv);
                }
                foreach (var p in t.GetProperties(F))
                {
                    string sv = "?";
                    try { if (p.GetGetMethod() != null && p.GetGetMethod().IsStatic) { var v = p.GetValue(null); sv = v == null ? "null" : v.ToString(); } }
                    catch { sv = "<err>"; }
                    if (sv.Length > 60) sv = sv.Substring(0, 60) + "…";
                    ProbePlugin.W("[members] prop " + p.Name + " : " + p.PropertyType.Name + " = " + sv);
                }
                foreach (var m in t.GetMethods(F))
                {
                    if (m.Name != "Announce" && m.Name != "Describe" && m.Name != "SwitchGroup"
                        && m.Name.IndexOf("Item", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var names = new System.Text.StringBuilder();
                    var ps = m.GetParameters();
                    for (int i = 0; i < ps.Length; i++) { if (i > 0) names.Append(','); names.Append(ps[i].ParameterType.Name); }
                    ProbePlugin.W("[members] method " + (m.IsStatic ? "static " : "") + m.Name + "(" + names + ") -> " + m.ReturnType.Name);
                }
            }
            catch (Exception ex) { ProbePlugin.W("[members-error] " + ex.GetType().Name + ": " + ex.Message); }
        }

        // F4：逐项打印 Items 的标签（Describe）+ 质量/来源，用于替代键盘走读验证标签质量
        private static void SimDumpLabels()
        {
            try
            {
                var t = FindA11yType("JustLilithA11y.UiNav");
                if (t == null) { ProbePlugin.W("[labels] UiNav 类型未找到"); return; }
                const BindingFlags F = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
                object items = t.GetProperty("Items", F)?.GetValue(null) ?? t.GetField("Items", F)?.GetValue(null);
                var list = items as System.Collections.IList;
                if (list == null) { ProbePlugin.W("[labels] Items 为空"); return; }
                MethodInfo describe = t.GetMethod("Describe", F, null, new[] { typeof(Selectable) }, null);
                MethodInfo current = t.GetMethod("CurrentItem", F, null, Type.EmptyTypes, null);
                try { DumpSwitchRow(); } catch { }
                FieldInfo idxF = t.GetField("_index", F);
                ProbePlugin.W("[labels] 共 " + list.Count + " 项, describe=" + (describe != null)
                              + " current=" + (current != null) + " index=" + (idxF != null));
                for (int i = 0; i < list.Count; i++)
                {
                    var sel = list[i] as Selectable;
                    string desc = "<null-item>";
                    if (describe != null && sel != null)
                    {
                        try { desc = (string)describe.Invoke(null, new object[] { sel }); }
                        catch (Exception de) { desc = "<desc-err " + de.GetType().Name + ": " + de.Message + ">"; }
                    }
                    string path = sel != null ? sel.gameObject.name : "null";
                    bool inter = false; try { if (sel != null) inter = sel.interactable; } catch { }
                    if (idxF != null) idxF.SetValue(null, i);
                    ProbePlugin.W("[labels] #" + (i + 1) + " name=" + path + " interactable=" + inter + " describe=[" + desc + "]");
                }                if (idxF != null) idxF.SetValue(null, 0);
            }
            catch (Exception ex) { ProbePlugin.W("[labels-error] " + ex.GetType().Name + ": " + ex.Message); }
        }

        // F2：激活当前导航项（Button.onClick），用于切 tab / 试开关，不依赖鼠标
        private static void SimActivate()
        {
            try
            {
                var t = FindA11yType("JustLilithA11y.UiNav");
                if (t == null) { ProbePlugin.W("[act] UiNav 类型未找到"); return; }
                const BindingFlags F = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
                var cur = t.GetMethod("CurrentItem", F, null, Type.EmptyTypes, null)?.Invoke(null, null) as Selectable;
                var idxF = t.GetField("_index", F);
                int idx = idxF == null ? -1 : (int)idxF.GetValue(null);
                var b = cur as Button;
                ProbePlugin.W("[act] index=" + idx + " name=" + (cur == null ? "null" : cur.gameObject.name) + " button=" + (b != null));
                if (b != null) b.onClick.Invoke();
            }
            catch (Exception ex) { ProbePlugin.W("[act-error] " + ex.GetType().Name + ": " + ex.Message); }
        }
        // F1：直接调用平台层 Activate(Selectable)（Enter 的同一路径，能处理 tab 这类非 Button 控件）
        private static void SimPlatformActivate()
        {
            try
            {
                var t = FindA11yType("JustLilithA11y.UiNav");
                if (t == null) { ProbePlugin.W("[actp] UiNav 类型未找到"); return; }
                const BindingFlags F = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
                var cur = t.GetMethod("CurrentItem", F, null, Type.EmptyTypes, null)?.Invoke(null, null) as Selectable;
                if (cur == null) { ProbePlugin.W("[actp] 当前项为空"); return; }
                MethodInfo act = t.GetMethod("Activate", F, null, new[] { typeof(Selectable) }, null);
                if (act == null)
                {
                    foreach (var m in t.GetMethods(F))
                    {
                        if (m.Name.IndexOf("Activ", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        var ps = m.GetParameters();
                        ProbePlugin.W("[actp-cand] " + m.Name + "(" + (ps.Length > 0 ? ps[0].ParameterType.Name : "") + ")");
                        if (act == null && ps.Length == 1 && ps[0].ParameterType == typeof(Selectable)) act = m;
                    }
                }
                ProbePlugin.W("[actp] name=" + cur.gameObject.name + " method=" + (act == null ? "null" : act.Name));
                if (act != null) act.Invoke(null, new object[] { cur });
            }
            catch (Exception ex) { ProbePlugin.W("[actp-error] " + ex.GetType().Name + ": " + ex.Message); }
        }
        // F8：点一下桌面上的 Lilith 本体按钮（Lilith/Canvas/Button），把带选项的 PlayerLine 菜单叫出来，
        // 用于验证「选项朗读 + 数字键/导航选择」
        private static void SimClickLilith()
        {
            try
            {
                var go = GameObject.Find("Lilith/Canvas/Button");
                ProbePlugin.W("[click] Lilith/Canvas/Button = " + (go != null));
                if (go == null) return;
                var b = go.GetComponent<UnityEngine.UI.Button>();
                ProbePlugin.W("[click] Button=" + (b != null) + " onClick=" + (b != null && b.onClick != null));
                if (b != null) b.onClick.Invoke();
            }
            catch (Exception ex) { ProbePlugin.W("[click-error] " + ex.GetType().Name + ": " + ex.Message); }
        }
        // F13：打印「你的生日」这一行的控件细节（Toggle / 子物体 active / sprite 名），
        // 用于判定 SwitchBtn 的开关状态到底能不能读到
        private static void DumpSwitchRow()
        {
            try
            {
                var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Button>());
                Button target = null;
                foreach (var o in all)
                {
                    var b = o.TryCast<Button>();
                    if (b == null) continue;
                    var n = b.gameObject.name;
                    if (n == "SwitchBtn") { target = b; break; }
                }
                ProbePlugin.W("[switch] found=" + (target != null));
                if (target == null) return;
                var go = target.gameObject;
                var tr = go.transform;
                for (int up = 0; up < 5 && tr != null; up++)
                {
                    ProbePlugin.W("[switch] up" + up + " name=" + tr.name + " active=" + tr.gameObject.activeInHierarchy);
                    var tg = tr.GetComponent<Toggle>();
                    if (tg != null) ProbePlugin.W("[switch]   Toggle.isOn=" + tg.isOn + " interactable=" + tg.interactable);
                    var rt = tr.TryCast<RectTransform>();
                    if (rt != null)
                    {
                        for (int i = 0; i < tr.childCount; i++)
                        {
                            var c = tr.GetChild(i);
                            var tg2 = c.GetComponent<Toggle>();
                            ProbePlugin.W("[switch]   child " + c.name + " active=" + c.gameObject.activeInHierarchy
                                          + (tg2 != null ? " Toggle.isOn=" + tg2.isOn : ""));
                        }
                    }
                    tr = tr.parent;
                }
            }
            catch (Exception ex) { ProbePlugin.W("[switch-error] " + ex.GetType().Name + ": " + ex.Message); }
        }
        private static void SimNavGroup(int dir)
        {
            try
            {
                var t = FindA11yType("JustLilithA11y.UiNav");
                if (t == null) { ProbePlugin.W("[simnav] UiNav 类型未找到"); return; }
                const BindingFlags F = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
                MethodInfo m = t.GetMethod("SwitchGroup", F, null, new[] { typeof(int) }, null);
                ProbePlugin.W("[simnav] SwitchGroup(" + dir + ") found=" + (m != null));
                if (m != null) m.Invoke(null, new object[] { dir });
            }
            catch (Exception ex) { ProbePlugin.W("[simnav-error] " + ex.GetType().Name + ": " + ex.Message); }
        }

        private static void OpenTraySetting()
        {
            try
            {
                var views = Resources.FindObjectsOfTypeAll(Il2CppType.Of<TraySettingNewView>());
                ProbePlugin.W("[op] TraySettingNewView 实例数=" + views.Length);
                if (views.Length == 0) return;
                var v = views[0].TryCast<TraySettingNewView>();
                var go = v.gameObject;
                ProbePlugin.W("[op] 调用前 IsVisible=" + v.IsVisible + " activeInHierarchy=" + go.activeInHierarchy + " path=" + PathOf(go.transform));

                var t = typeof(TraySettingNewView);
                string[] tryNames = new string[] { "Show", "SetVisible", "OnOpenTraySettingsRequested" };
                foreach (var name in tryNames)
                {
                    MethodInfo mi = null;
                    foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        if (m.Name != name) continue;
                        if (m.GetParameters().Length == 0) { mi = m; break; }
                        if (mi == null) mi = m;
                    }
                    if (mi == null) { ProbePlugin.W("[op] 无方法 " + name); continue; }
                    var ps = mi.GetParameters();
                    var args = new object[ps.Length];
                    for (int i = 0; i < ps.Length; i++)
                    {
                        if (ps[i].ParameterType == typeof(bool)) args[i] = true;
                        else args[i] = null;
                    }
                    try
                    {
                        mi.Invoke(v, args);
                        ProbePlugin.W("[op] 调用 " + name + "(参数数=" + ps.Length + ") ok; 之后 IsVisible=" + v.IsVisible + " activeInHierarchy=" + go.activeInHierarchy);
                    }
                    catch (Exception ex) { ProbePlugin.W("[op] 调用 " + name + " 异常 " + ex.GetType().Name + ": " + ex.Message); }
                }
            }
            catch (Exception ex) { ProbePlugin.W("[op] OpenTraySetting 异常 " + ex.GetType().Name + ": " + ex.Message); }
        }

        private static void DumpSettingViews()
        {
            try
            {
                var views = Resources.FindObjectsOfTypeAll(Il2CppType.Of<TraySettingNewView>());
                ProbePlugin.W("[op] TraySettingNewView 实例数=" + views.Length);
                for (int i = 0; i < views.Length && i < 6; i++)
                {
                    var v = views[i].TryCast<TraySettingNewView>();
                    var go = v.gameObject;
                    ProbePlugin.W("[op] #" + i + " activeInHierarchy=" + go.activeInHierarchy + " activeSelf=" + go.activeSelf + " path=" + PathOf(go.transform));
                }
                var t = typeof(TraySettingNewView);
                var ms = new List<string>();
                foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    if (m.DeclaringType == t && m.GetParameters().Length <= 1) ms.Add(m.Name);
                ProbePlugin.W("[op] TraySettingNewView 方法: " + string.Join(",", ms));
            }
            catch (Exception ex) { ProbePlugin.W("[op] DumpSettingViews 异常 " + ex.GetType().Name + ": " + ex.Message); }
        }

        private static Type FindModType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var t = asm.GetType(fullName, false);
                    if (t != null) return t;
                }
                catch { }
            }
            return null;
        }

        private static void DumpUiNavState()
        {
            try
            {
                var t = FindModType("JustLilithA11y.UiNav");
                if (t == null) { ProbePlugin.W("[navstate] 未找到 JustLilithA11y.UiNav 类型"); return; }
                var sb = new StringBuilder();
                sb.Append("[navstate] type=" + t.FullName);
                foreach (var f in t.GetFields(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (f.IsStatic)
                    {
                        object v = null;
                        try { v = f.GetValue(null); } catch { }
                        string s = v == null ? "null" : v.ToString();
                        if (s.Length > 120) s = s.Substring(0, 120) + "...";
                        sb.Append(" | " + f.Name + "=" + s);
                    }
                    else
                    {
                        sb.Append(" | #" + f.Name);
                    }
                }
                ProbePlugin.W(sb.ToString());
            }
            catch (Exception ex) { ProbePlugin.W("[navstate] 异常 " + ex.GetType().Name + ": " + ex.Message); }
        }

        private static void DumpHeldKeys()
        {
            var sb = new StringBuilder("[held]");
            KeyCode[] ks = new KeyCode[] { KeyCode.UpArrow, KeyCode.DownArrow, KeyCode.LeftArrow, KeyCode.RightArrow,
                KeyCode.Return, KeyCode.KeypadEnter, KeyCode.Space, KeyCode.PageUp, KeyCode.PageDown,
                KeyCode.Home, KeyCode.End, KeyCode.Tab, KeyCode.LeftControl, KeyCode.LeftAlt, KeyCode.LeftShift, KeyCode.Equals };
            foreach (var k in ks)
            {
                try
                {
                    if (Input.GetKey(k)) sb.Append(" " + k);
                }
                catch { }
            }
            ProbePlugin.W(sb.ToString());
        }

        private void Dump()
        {
            _dumps++;
            var sb = new StringBuilder();
            sb.AppendLine("================ DUMP #" + _dumps + " scene=" + SceneManager.GetActiveScene().name + " ================");

            // EventSystem
            try
            {
                var ess = Resources.FindObjectsOfTypeAll(Il2CppType.Of<EventSystem>());
                foreach (var o in ess)
                {
                    var es = o.TryCast<EventSystem>();
                    if (es == null) continue;
                    bool sn = false;
                    try { sn = es.sendNavigationEvents; } catch { }
                    sb.AppendLine("[eventsystem] " + PathOf(es.transform) + " sendNavigationEvents=" + sn +
                                  " current=" + (es.currentSelectedGameObject == null ? "null" : es.currentSelectedGameObject.name));
                }
            }
            catch (Exception ex) { sb.AppendLine("[eventsystem] error " + ex.Message); }

            // 可见 TMP 文本 + alpha
            try
            {
                var texts = Resources.FindObjectsOfTypeAll(Il2CppType.Of<TMP_Text>());
                int total = 0, visible = 0;
                foreach (var o in texts)
                {
                    TMP_Text t = null;
                    try { t = o.TryCast<TMP_Text>(); } catch { }
                    if (t == null) continue;
                    if (!t.gameObject.activeInHierarchy) continue;
                    total++;
                    string s = "";
                    try { s = t.text; } catch { }
                    if (string.IsNullOrEmpty(s)) continue;
                    float a = AlphaOf(t.transform);
                    bool vis = a > 0.05f && t.transform.lossyScale.x > 0.0001f;
                    if (vis) visible++;
                    if (vis && visible <= 90)
                        sb.AppendLine("[text] alpha=" + a.ToString("0.00") + " path=" + PathOf(t.transform) + " value=" + Trunc(s));
                }
                sb.AppendLine("[text] activeNonEmpty=" + total + " visible=" + visible);
            }
            catch (Exception ex) { sb.AppendLine("[text] error " + ex.GetType().Name + ": " + ex.Message); }

            // 可见控件（Selectable）+ 标签
            try
            {
                var sel = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Selectable>());
                int total = 0, visible = 0;
                foreach (var o in sel)
                {
                    Selectable s = null;
                    try { s = o.TryCast<Selectable>(); } catch { }
                    if (s == null) continue;
                    if (!s.gameObject.activeInHierarchy) continue;
                    total++;
                    float a = AlphaOf(s.transform);
                    bool vis = a > 0.05f && s.transform.lossyScale.x > 0.0001f;
                    if (!vis) continue;
                    visible++;
                    if (visible <= 80)
                    {
                        bool inter = false;
                        try { inter = s.interactable; } catch { }
                        string label = "";
                        try
                        {
                            var tt = s.GetComponent<TMP_Text>();
                            if (tt == null) tt = s.GetComponentInChildren<TMP_Text>(true);
                            if (tt != null) label = tt.text;
                        }
                        catch { }
                        sb.AppendLine("[selectable] alpha=" + a.ToString("0.00") + " name=" + s.gameObject.name + " type=" + s.GetType().Name +
                                      " interactable=" + inter + " label=" + Trunc(label) + " path=" + PathOf(s.transform));
                    }
                }
                sb.AppendLine("[selectable] activeTotal=" + total + " visible=" + visible);
            }
            catch (Exception ex) { sb.AppendLine("[selectable] error " + ex.GetType().Name + ": " + ex.Message); }

            // 对话气泡
            try
            {
                var bubbles = Resources.FindObjectsOfTypeAll(Il2CppType.Of<DialogueBubbleUI>());
                foreach (var o in bubbles)
                {
                    var b = o.TryCast<DialogueBubbleUI>();
                    if (b == null) continue;
                    float a = AlphaOf(b.transform);
                    sb.AppendLine("[bubble] alpha=" + a.ToString("0.00") + " active=" + b.gameObject.activeInHierarchy + " path=" + PathOf(b.transform));
                    foreach (var tt in b.GetComponentsInChildren<TMP_Text>(true))
                    {
                        string s = "";
                        try { s = tt.text; } catch { }
                        sb.AppendLine("  [bubble-text] alpha=" + AlphaOf(tt.transform).ToString("0.00") + " name=" + tt.gameObject.name + " value=" + Trunc(s));
                    }
                }
            }
            catch (Exception ex) { sb.AppendLine("[bubble] error " + ex.GetType().Name + ": " + ex.Message); }

            // PlayerLine 选项按钮（可见）
            try
            {
                var pls = Resources.FindObjectsOfTypeAll(Il2CppType.Of<PlayerLineController>());
                foreach (var o in pls)
                {
                    var pl = o.TryCast<PlayerLineController>();
                    if (pl == null) continue;
                    sb.AppendLine("[playerline] path=" + PathOf(pl.transform) + " active=" + pl.gameObject.activeInHierarchy);
                    int shown = 0;
                    foreach (var btn in pl.GetComponentsInChildren<Button>(true))
                    {
                        float a = AlphaOf(btn.transform);
                        if (a <= 0.05f) continue;
                        string label = "";
                        var tt = btn.GetComponentInChildren<TMP_Text>(true);
                        if (tt != null) label = tt.text;
                        shown++;
                        if (shown <= 12) sb.AppendLine("  [option] alpha=" + a.ToString("0.00") + " path=" + PathOf(btn.transform) + " label=" + Trunc(label));
                    }
                    sb.AppendLine("  [option] visibleCount=" + shown);
                }
            }
            catch (Exception ex) { sb.AppendLine("[playerline] error " + ex.GetType().Name + ": " + ex.Message); }

            // 音频
            try
            {
                var aus = Resources.FindObjectsOfTypeAll(Il2CppType.Of<AudioSource>());
                int playing = 0;
                foreach (var o in aus)
                {
                    var au = o.TryCast<AudioSource>();
                    if (au == null) continue;
                    bool p = false;
                    try { p = au.isPlaying; } catch { }
                    if (p) { playing++; if (playing <= 5) sb.AppendLine("[audio] playing path=" + PathOf(au.transform) + " clip=" + ProbePlugin.SafeStr(() => au.clip == null ? "null" : au.clip.name)); }
                }
                sb.AppendLine("[audio] playingCount=" + playing);
            }
            catch (Exception ex) { sb.AppendLine("[audio] error " + ex.GetType().Name + ": " + ex.Message); }

            // 开场/字幕载体排查：根对象 + 指定路径组件 + 全部 TMP（含 inactive）关键词匹配
            try
            {
                var trs = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Transform>());
                var roots = new List<string>();
                foreach (var o in trs)
                {
                    var tr = o.TryCast<Transform>();
                    if (tr == null || tr.parent != null) continue;
                    if (!tr.gameObject.activeInHierarchy) continue;
                    if (roots.Count < 60) roots.Add(tr.name + "(children=" + tr.childCount + ")");
                }
                sb.AppendLine("[roots] " + string.Join(" | ", roots));
            }
            catch (Exception ex) { sb.AppendLine("[roots] error " + ex.GetType().Name + ": " + ex.Message); }

            try
            {
                var trs2 = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Transform>());
                int shown = 0;
                foreach (var o in trs2)
                {
                    var tr = o.TryCast<Transform>();
                    if (tr == null) continue;
                    string p = PathOf(tr);
                    if (p.IndexOf("Opening", StringComparison.OrdinalIgnoreCase) < 0 &&
                        p.IndexOf("Subtitle", StringComparison.OrdinalIgnoreCase) < 0 &&
                        p.IndexOf("Caption", StringComparison.OrdinalIgnoreCase) < 0 &&
                        p.IndexOf("Frame", StringComparison.OrdinalIgnoreCase) < 0 &&
                        p.IndexOf("Photo", StringComparison.OrdinalIgnoreCase) < 0 &&
                        p.IndexOf("Dream", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (shown >= 40) break;
                    shown++;
                    var comps = new List<string>();
                    foreach (var c in tr.GetComponents<Component>())
                    {
                        if (c == null) { comps.Add("null"); continue; }
                        string n = c.GetType().Name;
                        if (n == "TMP_Text" || n == "TextMeshProUGUI" || n == "TextMeshPro")
                        {
                            var tt = c.TryCast<TMP_Text>();
                            string s = "";
                            try { s = tt == null ? "" : tt.text; } catch { }
                            n += "('" + Trunc(s) + "')";
                        }
                        comps.Add(n);
                    }
                    sb.AppendLine("[inspect] active=" + tr.gameObject.activeInHierarchy + " alpha=" + AlphaOf(tr).ToString("0.00") +
                                  " path=" + p + " comps=" + string.Join(",", comps));
                }
            }
            catch (Exception ex) { sb.AppendLine("[inspect] error " + ex.GetType().Name + ": " + ex.Message); }

            try
            {
                var texts2 = Resources.FindObjectsOfTypeAll(Il2CppType.Of<TMP_Text>());
                int hits = 0;
                foreach (var o in texts2)
                {
                    TMP_Text t = null;
                    try { t = o.TryCast<TMP_Text>(); } catch { }
                    if (t == null) continue;
                    string s = "";
                    try { s = t.text; } catch { }
                    if (string.IsNullOrEmpty(s)) continue;
                    if (s.IndexOf("哥哥", StringComparison.Ordinal) < 0 && s.IndexOf("失望", StringComparison.Ordinal) < 0 &&
                        s.IndexOf("谢谢", StringComparison.Ordinal) < 0 && s.IndexOf("最喜欢", StringComparison.Ordinal) < 0) continue;
                    hits++;
                    sb.AppendLine("[kw-tmp] active=" + t.gameObject.activeInHierarchy + " alpha=" + AlphaOf(t.transform).ToString("0.00") +
                                  " path=" + PathOf(t.transform) + " value=" + Trunc(s));
                }
                sb.AppendLine("[kw-tmp] hits=" + hits + " (all TMP incl. inactive)");

                var legacy = Resources.FindObjectsOfTypeAll(Il2CppType.Of<UnityEngine.UI.Text>());
                int lg = 0, lgActive = 0;
                foreach (var o in legacy)
                {
                    var t = o.TryCast<UnityEngine.UI.Text>();
                    if (t == null) continue;
                    lg++;
                    if (t.gameObject.activeInHierarchy) lgActive++;
                }
                sb.AppendLine("[legacy-text] count=" + lg + " active=" + lgActive);
            }
            catch (Exception ex) { sb.AppendLine("[kw-tmp] error " + ex.GetType().Name + ": " + ex.Message); }

            // DialogueManager 只订阅一次
            try
            {
                var dms = Resources.FindObjectsOfTypeAll(Il2CppType.Of<DialogueManager>());
                sb.AppendLine("[dm] instances=" + dms.Length);
                if (!ProbePlugin._subscribed)
                {
                    foreach (var o in dms)
                    {
                        var dm = o.TryCast<DialogueManager>();
                        if (dm == null) continue;
                        var add = dm.GetType().GetMethod("add_OnDialogueStart", BindingFlags.Public | BindingFlags.Instance);
                        if (add == null) { sb.AppendLine("[dm] add_OnDialogueStart NOT FOUND"); continue; }
                        try
                        {
                            var cb = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<DialogueNode>>(new Action<DialogueNode>(OnStart));
                            add.Invoke(dm, new object[] { cb });
                            ProbePlugin._subscribed = true;
                            sb.AppendLine("[dm] subscribed OnDialogueStart once");
                            break;
                        }
                        catch (Exception ex) { sb.AppendLine("[dm] subscribe FAILED: " + ex.GetType().Name + ": " + ex.Message); }
                    }
                }
            }
            catch (Exception ex) { sb.AppendLine("[dm] error " + ex.GetType().Name + ": " + ex.Message); }

            sb.AppendLine("================ END DUMP #" + _dumps + " ================");
            ProbePlugin.W(sb.ToString());
        }

        private static void OnStart(DialogueNode node)
        {
            try
            {
                if (node == null) { ProbePlugin.W("[event] OnDialogueStart node=null"); return; }
                string text = "", speaker = "", soundId = "";
                int lineId = 0;
                try { text = node.text; } catch { }
                try { speaker = node.speaker; } catch { }
                try { soundId = node.soundId; } catch { }
                try { lineId = node.lineId; } catch { }
                ProbePlugin.W("[event] OnDialogueStart speaker=" + speaker + " lineId=" + lineId + " soundId=" + soundId + " text=" + Trunc(text));
            }
            catch (Exception ex) { ProbePlugin.W("[event] OnStart error " + ex.Message); }
        }

        internal static float AlphaOf(Transform t)
        {
            float a = 1f;
            try
            {
                while (t != null)
                {
                    var cg = t.GetComponent<CanvasGroup>();
                    if (cg != null) a *= Mathf.Clamp01(cg.alpha);
                    t = t.parent;
                }
            }
            catch { }
            return a;
        }

        internal static string Trunc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", "\\r").Replace("\n", "\\n");
            return s.Length <= 60 ? s : s.Substring(0, 60) + "…";
        }

        private static string PathOf(Transform t)
        {
            try
            {
                var parts = new List<string>(8);
                while (t != null) { parts.Add(t.name); t = t.parent; }
                parts.Reverse();
                return string.Join("/", parts);
            }
            catch { return "?"; }
        }
    }
}











