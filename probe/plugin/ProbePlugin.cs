using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
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
        private bool _autoDumped;
        private int _phase2;
        private float _toggleAt = -1f;
        private string _lastMenu = "";
        private int _p3Count;
        private float _p3Next;
        private int _expStep;
        private float _expNext;
        private static bool _expStarted;

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
                if (Input.GetKeyDown(KeyCode.C)) DumpCharacter();
                if (Input.GetKeyDown(KeyCode.M)) DumpMenus();

                if (!_autoDumped && Time.unscaledTime > 25f)
                {
                    _autoDumped = true;
                    ProbePlugin.W("[auto] 开始自动 dump char/menu");
                    try { DumpCharacter(); } catch (Exception ex) { ProbePlugin.W("[auto] char 异常 " + ex.GetType().Name + ": " + ex.Message); }
                    try { DumpMenus(); } catch (Exception ex) { ProbePlugin.W("[auto] menu 异常 " + ex.GetType().Name + ": " + ex.Message); }
                }

                if (_phase2 == 0 && Time.unscaledTime > 40f)
                {
                    _phase2 = 1;
                    try { DumpHitAreaDetails(); } catch (Exception ex) { ProbePlugin.W("[hit] 异常 " + ex.GetType().Name + ": " + ex.Message); }
                    try
                    {
                        var tcc = GType("CharacterController");
                        var arr = tcc == null ? null : Resources.FindObjectsOfTypeAll(Il2CppType.From(tcc));
                        ProbePlugin.W("[menu2] CharacterController instances=" + (arr == null ? "null" : arr.Length.ToString()));
                        if (arr != null && arr.Length > 0)
                        {
                            var cc = arr[0].TryCast<Component>();
                            if (cc != null)
                            {
                                var m = cc.GetType().GetMethod("ToggleSetting", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                ProbePlugin.W("[menu2] ToggleSetting method=" + (m != null));
                                if (m != null) { m.Invoke(cc, null); _toggleAt = Time.unscaledTime; }
                            }
                        }
                    }
                    catch (Exception ex) { ProbePlugin.W("[menu2] 异常 " + ex.GetType().Name + ": " + ex.Message); }
                }
                if (!_expStarted && Time.unscaledTime > 60f) { _expStarted = true; _expNext = Time.unscaledTime; }
                if (_expStarted) { try { ExpTick(); } catch (Exception ex) { ProbePlugin.W("[exp] 异常 " + ex.GetType().Name + ": " + ex.Message); } }
                if (Time.unscaledTime > 70f) { try { ModCalls(); } catch (Exception ex) { ProbePlugin.W("[mod] 异常 " + ex.GetType().Name); } }
                if (Time.unscaledTime > 95f && Time.unscaledTime > _nextMenuPoll) { _nextMenuPoll = Time.unscaledTime + 20f; ProbePlugin.W("[mod] menuPoll " + MenuState()); }
                if (Time.unscaledTime > 92f && !_navToggled) { _navToggled = true; try { CallMod("UiNav", "Toggle"); } catch (Exception ex) { ProbePlugin.W("[mod] Toggle 异常 " + ex.GetType().Name); } }
                if (Time.unscaledTime > 100f && !_labelsDumped) { _labelsDumped = true; try { SimDumpLabels(); } catch (Exception ex) { ProbePlugin.W("[mod] dump 异常 " + ex.GetType().Name); } }
                if (Time.unscaledTime > 118f && !_activated) { _activated = true; try { ProbePlugin.W("[mod] 平台激活当前项"); SimPlatformActivate(); } catch (Exception ex) { ProbePlugin.W("[mod] 激活异常 " + ex.GetType().Name); } }
                if (Time.unscaledTime > 45f && Time.unscaledTime > _p3Next && _p3Count < 10)
                {
                    _p3Next = Time.unscaledTime + 3f;
                    _p3Count++;
                    ProbePlugin.W("[p3] t=" + Time.unscaledTime.ToString("F0") + " hit=" + HitAreaStates() + " menu=" + MenuState());
                }
                if (_phase2 >= 1 && _toggleAt > 0f && Time.unscaledTime > _toggleAt + 1f)
                {
                    var st = MenuState();
                    if (st != _lastMenu) { _lastMenu = st; ProbePlugin.W("[menu2] t=" + (Time.unscaledTime - _toggleAt).ToString("F1") + "s " + st); }
                }
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

        // C：清点莉莉丝身体可点区域（CharacterHitArea）与点击处理器方法
        private static Type GType(string name)
        {
            var all = new List<System.Reflection.Assembly>();
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) all.Add(a);
            // 优先 Assembly-CSharp（游戏本体），避免命中 UnityEngine 同名类型（如 CharacterController）
            all.Sort((x, y) =>
            {
                int sx = (x.GetName().Name == "Assembly-CSharp") ? 0 : 1;
                int sy = (y.GetName().Name == "Assembly-CSharp") ? 0 : 1;
                return sx.CompareTo(sy);
            });
            foreach (var a in all)
            {
                try { var direct = a.GetType(name, false); if (direct != null) return direct; } catch { }
                Type[] ts;
                try { ts = a.GetTypes(); } catch { continue; }
                foreach (var t in ts)
                    if (t.Name == name) return t;
            }
            return null;
        }

        private static string CompList(GameObject go)
        {
            var names = new List<string>();
            foreach (var c in go.GetComponents<Component>())
            {
                if (c == null) continue;
                var n = c.GetType().Name;
                if (n == "Transform" || n == "RectTransform") continue;
                names.Add(n);
            }
            return string.Join("+", names);
        }

        private static void DumpCharacter()
        {
            try
            {
                var th = GType("CharacterHitArea");
                ProbePlugin.W("[char] CharacterHitArea type=" + (th == null ? "null" : th.FullName));
                if (th != null)
                {
                    var arr = Resources.FindObjectsOfTypeAll(Il2CppType.From(th));
                    ProbePlugin.W("[char] instances=" + arr.Length);
                    for (int i = 0; i < arr.Length && i < 20; i++)
                    {
                        var c = arr[i].TryCast<Component>();
                        if (c == null) continue;
                        var go = c.gameObject;
                        string typeVal = "?";
                        var p = th.GetProperty("Type", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (p != null && p.CanRead) { try { typeVal = p.GetValue(c)?.ToString(); } catch { } }
                        string parentChain = "";
                        var tr = go.transform;
                        for (int up = 0; up < 4 && tr != null; up++, tr = tr.parent) parentChain += (up == 0 ? "" : " < ") + tr.name;
                        ProbePlugin.W("[char] #" + i + " name=" + go.name + " type=" + typeVal
                            + " active=" + go.activeInHierarchy + " comps=" + CompList(go) + " chain=" + parentChain);
                    }
                }
                var thh = GType("CharacterInteractionHandler");
                ProbePlugin.W("[char] handler type=" + (thh == null ? "null" : thh.FullName));
                if (thh != null)
                {
                    var hs = Resources.FindObjectsOfTypeAll(Il2CppType.From(thh));
                    ProbePlugin.W("[char] handler instances=" + hs.Length);
                    foreach (var m in thh.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (m.Name.StartsWith("get_") || m.Name.StartsWith("set_") || m.Name.StartsWith("add_") || m.Name.StartsWith("remove_")) continue;
                        ProbePlugin.W("[char] H." + m.Name + "(" + string.Join(",", Array.ConvertAll(m.GetParameters(), q => q.ParameterType.Name + " " + q.Name)) + ")");
                    }
                }
                // 也清点 Lilith 根下的直接子树，找可能的可点对象
                var root = GameObject.Find("Lilith");
                if (root != null)
                {
                    var kids = new List<string>();
                    var rt = root.transform;
                    for (int i0 = 0; i0 < rt.childCount && i0 < 12; i0++)
                    {
                        var t0 = rt.GetChild(i0);
                        kids.Add(t0.name + "[" + CompList(t0.gameObject) + "]");
                        for (int i1 = 0; i1 < t0.childCount && i1 < 12; i1++)
                        {
                            var t1 = t0.GetChild(i1);
                            kids.Add("  " + t0.name + "/" + t1.name + "[" + CompList(t1.gameObject) + "] active=" + t1.gameObject.activeInHierarchy);
                        }
                    }
                    ProbePlugin.W("[char] Lilith children: " + string.Join(" | ", kids.ToArray()));
                }
            }
            catch (Exception ex) { ProbePlugin.W("[char] 异常 " + ex.GetType().Name + ": " + ex.Message); }
        }

        // 命中区细节：部位/枚举/碰撞体 bounds/世界中心/各相机屏幕坐标
        private static void DumpHitAreaDetails()
        {
            var camNames = new List<string>();
            foreach (var cam in Camera.allCameras)
            {
                if (cam == null) continue;
                camNames.Add(cam.name + "(ortho=" + cam.orthographic + ",depth=" + cam.depth + ",rect=" + cam.pixelRect.width + "x" + cam.pixelRect.height + ")");
            }
            ProbePlugin.W("[hit] cameras=" + string.Join(" | ", camNames.ToArray()));

            var th = GType("CharacterHitArea");
            if (th == null) { ProbePlugin.W("[hit] type=null"); return; }
            var arr = Resources.FindObjectsOfTypeAll(Il2CppType.From(th));
            var pType = th.GetProperty("Type", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < arr.Length && i < 20; i++)
            {
                var c = arr[i].TryCast<Component>();
                if (c == null) continue;
                var go = c.gameObject;
                string typeVal = "?";
                if (pType != null && pType.CanRead) { try { var v = pType.GetValue(c); typeVal = v == null ? "null" : v.ToString(); } catch (Exception ex) { typeVal = "ex:" + ex.GetType().Name; } }
                var col = go.GetComponent<Collider2D>();
                string bounds = "no-col";
                Vector3 world = go.transform.position;
                if (col != null) { var b = col.bounds; bounds = "c=" + b.center.x.ToString("F2") + "," + b.center.y.ToString("F2") + " s=" + b.size.x.ToString("F2") + "x" + b.size.y.ToString("F2"); world = b.center; }
                var sp = new List<string>();
                foreach (var cam in Camera.allCameras)
                {
                    if (cam == null) continue;
                    var p = cam.WorldToScreenPoint(world);
                    sp.Add(cam.name + "=" + p.x.ToString("F0") + "," + p.y.ToString("F0") + ",z" + p.z.ToString("F1"));
                }
                ProbePlugin.W("[hit] #" + i + " " + go.name + " type=" + typeVal + " active=" + go.activeInHierarchy
                    + " pos=" + go.transform.position.x.ToString("F2") + "," + go.transform.position.y.ToString("F2") + " " + bounds
                    + " screen[" + string.Join("; ", sp.ToArray()) + "] chain=" + PathOf(go.transform));
            }

            var thh = GType("CharacterInteractionHandler");
            if (thh != null)
            {
                var hs = Resources.FindObjectsOfTypeAll(Il2CppType.From(thh));
                ProbePlugin.W("[hit] handler instances=" + hs.Length);
                for (int i = 0; i < hs.Length && i < 3; i++)
                {
                    var c = hs[i].TryCast<Component>();
                    if (c == null) continue;
                    foreach (var f in thh.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (f.FieldType != typeof(bool) && f.FieldType.Name != "Single" && f.FieldType.Name != "Int32") continue;
                        string val = "?";
                        try { val = f.GetValue(c)?.ToString(); } catch { }
                        if (f.Name.IndexOf("enable", StringComparison.OrdinalIgnoreCase) >= 0 || f.Name.IndexOf("state", StringComparison.OrdinalIgnoreCase) >= 0 || f.Name.IndexOf("click", StringComparison.OrdinalIgnoreCase) >= 0)
                            ProbePlugin.W("[hit] H." + f.Name + "=" + val);
                    }
                }
            }
        }

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        // 把 Il2Cpp 对象重新包成代理类型 T（TryCast<Component> 会丢失真实类型，导致 GetMethod 找不到方法）
        private static object AsType(object il2cppObj, Type t)
        {
            if (il2cppObj == null) return null;
            var baseObj = il2cppObj as Il2CppObjectBase;
            if (baseObj == null) return null;
            try
            {
                var mi = typeof(Il2CppObjectBase).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "TryCast" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
                if (mi != null)
                {
                    var r = mi.MakeGenericMethod(t).Invoke(baseObj, null);
                    if (r != null) return r;
                }
            }
            catch { }
            try { return Activator.CreateInstance(t, baseObj.Pointer); } catch { return null; }
        }

        private static object FirstOf(string typeName)
        {
            var t = GType(typeName);
            if (t == null) { ProbePlugin.W("[exp] no type " + typeName); return null; }
            var arr = Resources.FindObjectsOfTypeAll(Il2CppType.From(t));
            if (arr.Length == 0) { ProbePlugin.W("[exp] no instance " + typeName); return null; }
            var o = AsType(arr[0], t);
            ProbePlugin.W("[exp] " + typeName + " proxy=" + (o == null ? "null" : o.GetType().Name));
            return o;
        }

        private static void InvokeTyped(object o, Type t, string name, params object[] args)
        {
            if (o == null || t == null) { ProbePlugin.W("[exp] null target for " + name); return; }
            var m = t.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (m == null) { ProbePlugin.W("[exp] no method " + name); return; }
            try { m.Invoke(o, args); ProbePlugin.W("[exp] 调用 " + name + " 成功"); }
            catch (Exception ex) { ProbePlugin.W("[exp] 调用 " + name + " 异常 " + ex.GetType().Name + ": " + ex.Message); }
        }

        private static void EnableInteraction(bool on)
        {
            var t = GType("CharacterInteractionHandler");
            var o = FirstOf("CharacterInteractionHandler");
            InvokeTyped(o, t, "SetInteractionEnabled", on);
        }

        private static Vector3 LilithPos()
        {
            try
            {
                var go = GameObject.Find("Lilith");
                if (go != null) return go.transform.position;
            }
            catch { }
            return Vector3.zero;
        }

        private static void ActivateColiders()
        {
            try
            {
                var th = GType("CharacterHitArea");
                if (th == null) return;
                var arr = Resources.FindObjectsOfTypeAll(Il2CppType.From(th));
                if (arr.Length == 0) { ProbePlugin.W("[exp] no hit areas"); return; }
                var c0 = arr[0].TryCast<Component>();
                var parent = c0 != null ? c0.transform.parent.gameObject : null;
                if (parent == null) { ProbePlugin.W("[exp] no parent"); return; }
                ProbePlugin.W("[exp] Coliders=" + parent.name + " activeSelf=" + parent.activeSelf + " -> SetActive(true)");
                parent.SetActive(true);
            }
            catch (Exception ex) { ProbePlugin.W("[exp] ActivateColiders 异常 " + ex.GetType().Name + ": " + ex.Message); }
        }

        private static Vector3 AreaCenter(string areaName)
        {
            try
            {
                var t = GType("CharacterHitArea");
                if (t == null) return Vector3.zero;
                var arr = Resources.FindObjectsOfTypeAll(Il2CppType.From(t));
                for (int i = 0; i < arr.Length; i++)
                {
                    var c = arr[i].TryCast<Component>();
                    if (c == null || c.gameObject.name != areaName) continue;
                    var col = c.gameObject.GetComponent<Collider2D>();
                    if (col != null) return col.bounds.center;
                    return c.transform.position;
                }
            }
            catch (Exception ex) { ProbePlugin.W("[exp] AreaCenter 异常 " + ex.GetType().Name); }
            return Vector3.zero;
        }

        private static void CallDelayedSingleClick(string area, Vector3 pos)
        {
            var t = GType("CharacterInteractionHandler");
            var te = GType("CharacterHitAreaType");
            var o = FirstOf("CharacterInteractionHandler");
            if (t == null || te == null || o == null) return;
            MethodInfo m = null;
            foreach (var name in new[] { "DelayedSingleClick", "DelayedSleepSingleClick" })
            {
                m = t.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (m != null) break;
            }
            if (m == null) { ProbePlugin.W("[exp] no DelayedSingleClick method"); return; }
            object ev = null;
            try { ev = System.Enum.Parse(te, area); }
            catch
            {
                try { ev = Activator.CreateInstance(te); } catch { }
            }
            ProbePlugin.W("[exp] 调用 " + m.Name + "(" + area + "," + pos + ") enum=" + (ev == null ? "null" : ev.ToString()));
            try { m.Invoke(o, new object[] { ev, pos }); ProbePlugin.W("[exp] " + m.Name + " 成功"); }
            catch (Exception ex) { ProbePlugin.W("[exp] " + m.Name + " 异常 " + ex.GetType().Name + ": " + ex.Message); }
        }

        private static string CharDesktopPoint()
        {
            try
            {
                var t = GType("CharacterController");
                var o = FirstOf("CharacterController");
                if (t == null || o == null) return "null";
                var m = t.GetMethod("TryGetCharacterDesktopPoint", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (m == null) return "no-method";
                var args = new object[] { 0, 0 };
                var ok = (bool)m.Invoke(null, args);
                var fn = GType("CharacterInteractionHandler");
                var fo = FirstOf("CharacterInteractionHandler");
                string enabled = "?";
                if (fn != null && fo != null)
                {
                    var f = fn.GetField("_interactionEnabled", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (f != null) enabled = f.GetValue(fo) + "";
                }
                return (ok ? (args[0] + "," + args[1]) : "false") + " enabled=" + enabled;
            }
            catch (Exception ex) { return "异常 " + ex.GetType().Name; }
        }

        private static string CurrentHitArea()
        {
            try
            {
                var t = GType("CharacterInteractionHandler");
                var o = FirstOf("CharacterInteractionHandler");
                if (t == null || o == null) return "null";
                var mo = t.GetMethod("OverlapMouseColliders", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var mg = t.GetMethod("GetHitArea", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (mo == null || mg == null) return "no-method";
                var cols = mo.Invoke(o, null);
                var area = mg.Invoke(o, new[] { cols });
                var f = t.GetField("_pressHitArea", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var press = f != null ? f.GetValue(o) : null;
                return "overlap=" + area + " press=" + press;
            }
            catch (Exception ex) { return "异常 " + ex.GetType().Name + ": " + ex.Message; }
        }

        private static void ShowSettingView()
        {
            var t = GType("SettingView");
            var o = FirstOf("SettingView");
            InvokeTyped(o, t, "Show");
        }

        private void ExpTick()
        {
            if (Time.unscaledTime < _expNext) return;
            switch (_expStep)
            {
                case 0:
                    ProbePlugin.W("[exp] step0 hit=" + HitAreaStates());
                    EnableInteraction(true);
                    _expNext = Time.unscaledTime + 2f;
                    break;
                case 1:
                    ProbePlugin.W("[exp] step1 after enable hit=" + HitAreaStates());
                    ActivateColiders();
                    _expNext = Time.unscaledTime + 2f;
                    break;
                case 2:
                    ProbePlugin.W("[exp] step2 after Coliders active hit=" + HitAreaStates());
                    var pHead = AreaCenter("Head");
                    ProbePlugin.W("[exp] Head center=" + pHead + " -> DelayedSingleClick(Head)");
                    CallDelayedSingleClick("Head", pHead);
                    _expNext = Time.unscaledTime + 4f;
                    break;
                case 3:
                    ProbePlugin.W("[exp] step3 after Head click hit=" + HitAreaStates());
                    var pSkirt = AreaCenter("Skirt");
                    ProbePlugin.W("[exp] Skirt center=" + pSkirt + " -> DelayedSingleClick(Skirt)");
                    CallDelayedSingleClick("Skirt", pSkirt);
                    _expNext = Time.unscaledTime + 4f;
                    break;
                default:
                    ProbePlugin.W("[exp] step" + _expStep + " cursor=" + Input.mousePosition.x + "," + Input.mousePosition.y + " area=" + CurrentHitArea());
                    InvokeTyped(FirstOf("CharacterInteractionHandler"), GType("CharacterInteractionHandler"), "HandleMouseDown");
                    InvokeTyped(FirstOf("CharacterInteractionHandler"), GType("CharacterInteractionHandler"), "HandleMouseUp");
                    _expNext = Time.unscaledTime + 6f;
                    break;
            }
            _expStep++;
        }
        private static bool _modCallsDone;
        private static bool _navToggled;
        private static float _nextMenuPoll;
        private static bool _activated;
        private static bool _labelsDumped;
        private static void ModCalls()
        {
            if (_modCallsDone) return;
            _modCallsDone = true;
            CallMod("LilithMenu", "OnHotkey");
        }

        private static void CallMod(string typeName, string method, params object[] args)
        {
            try
            {
                Type t = null;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (!a.GetName().Name.StartsWith("JustLilithA11y")) continue;
                    try { t = a.GetType("JustLilithA11y." + typeName, false); } catch { }
                    if (t != null) break;
                }
                if (t == null) { ProbePlugin.W("[mod] 找不到类型 " + typeName); return; }
                var flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                MethodInfo m = null;
                if (args == null || args.Length == 0) m = t.GetMethod(method, flags);
                else m = t.GetMethods(flags).FirstOrDefault(x => x.Name == method && x.GetParameters().Length == args.Length);
                if (m == null) { ProbePlugin.W("[mod] 找不到方法 " + typeName + "." + method); return; }
                var r = m.Invoke(null, args);
                ProbePlugin.W("[mod] 已调用 " + typeName + "." + method + " -> " + (r == null ? "void" : r.ToString()));
            }
            catch (Exception ex) { ProbePlugin.W("[mod] " + typeName + "." + method + " 异常 " + ex.GetType().Name + ": " + ex.Message); }
        }

        private static string PathOf(GameObject go)
        {
            try
            {
                var sb = new StringBuilder();
                var t = go.transform;
                int guard = 0;
                while (t != null && guard++ < 12)
                {
                    if (sb.Length > 0) sb.Insert(0, "/");
                    sb.Insert(0, t.name + (t.gameObject.activeSelf ? "" : "(off)"));
                    t = t.parent;
                }
                return sb.ToString();
            }
            catch { return "?"; }
        }

        private static string HitAreaStates()
        {
            try
            {
                var th = GType("CharacterHitArea");
                if (th == null) return "no-type";
                var arr = Resources.FindObjectsOfTypeAll(Il2CppType.From(th));
                var parts = new List<string>();
                for (int i = 0; i < arr.Length && i < 10; i++)
                {
                    var c = arr[i].TryCast<Component>();
                    if (c == null) continue;
                    var col = c.gameObject.GetComponent<Collider2D>();
                    string sz = col == null ? "n" : (col.enabled ? "E" : "d") + (col.bounds.size.x.ToString("F2") + "x" + col.bounds.size.y.ToString("F2"));
                    parts.Add(c.gameObject.name + ":" + (c.gameObject.activeInHierarchy ? 1 : 0) + ":" + sz + ":p=" + PathOf(c.gameObject));
                }
                return string.Join(",", parts.ToArray());
            }
            catch (Exception ex) { return "ex:" + ex.GetType().Name; }
        }

        private static string MenuState()
        {
            try
            {
                var t = GType("SettingView");
                if (t == null) return "no-type";
                var arr = Resources.FindObjectsOfTypeAll(Il2CppType.From(t));
                var parts = new List<string>();
                for (int i = 0; i < arr.Length && i < 6; i++)
                {
                    var c = arr[i].TryCast<Component>();
                    if (c == null) continue;
                    string alpha = "?";
                    var cg = c.GetComponent<CanvasGroup>();
                    if (cg != null) alpha = cg.alpha.ToString("F2");
                    parts.Add(c.gameObject.name + "=" + c.gameObject.activeInHierarchy + "/a" + alpha);
                }
                return "count=" + arr.Length + " " + string.Join(",", parts.ToArray());
            }
            catch (Exception ex) { return "ex:" + ex.GetType().Name + ":" + ex.Message; }
        }

        // M：清点面板/菜单类与自动关闭相关字段
        private static void DumpMenus()
        {
            try
            {
                var tb = GType("ViewPanelBase");
                ProbePlugin.W("[menu] ViewPanelBase type=" + (tb == null ? "null" : tb.FullName));
                if (tb != null)
                {
                    var arr = Resources.FindObjectsOfTypeAll(Il2CppType.From(tb));
                    ProbePlugin.W("[menu] panel instances=" + arr.Length);
                    for (int i = 0; i < arr.Length && i < 30; i++)
                    {
                        var c = arr[i].TryCast<Component>();
                        if (c == null) continue;
                        var go = c.gameObject;
                        string vis = "?";
                        var pv = tb.GetProperty("IsVisible", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (pv != null && pv.CanRead) { try { vis = pv.GetValue(c)?.ToString(); } catch { } }
                        ProbePlugin.W("[menu] #" + i + " " + c.GetType().Name + " name=" + go.name + " active=" + go.activeInHierarchy + " IsVisible=" + vis + " path=" + PathOf(go.transform));
                    }
                }
                var tuk = GType("ModuleUIManagKit");
                ProbePlugin.W("[menu] ModuleUIManagKit type=" + (tuk == null ? "null" : tuk.FullName));
                if (tuk != null)
                {
                    foreach (var m in tuk.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (m.Name.StartsWith("get_") || m.Name.StartsWith("set_")) continue;
                        if (!m.IsStatic && !m.IsPublic && !m.IsPrivate) { }
                        ProbePlugin.W("[menu] K." + m.Name + "(" + string.Join(",", Array.ConvertAll(m.GetParameters(), q => q.ParameterType.Name)) + ") static=" + m.IsStatic);
                    }
                }
                // 自动关闭相关字段：只打名字里带关键词的
                string[] kw = { "auto", "hide", "dismiss", "timer", "duration", "delay", "valid", "life" };
                foreach (var tn in new[] { "DialogueManager", "PlayerLineController", "DialogueBubbleUI" })
                {
                    var t = GType(tn);
                    if (t == null) { ProbePlugin.W("[menu] " + tn + " type=null"); continue; }
                    var fs = new List<string>();
                    foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        var ln = f.Name.ToLowerInvariant();
                        if (!Array.Exists(kw, k => ln.Contains(k))) continue;
                        fs.Add(f.Name + ":" + f.FieldType.Name);
                    }
                    ProbePlugin.W("[menu] " + tn + " 相关字段: " + string.Join(",", fs.ToArray()));
                }
            }
            catch (Exception ex) { ProbePlugin.W("[menu] 异常 " + ex.GetType().Name + ": " + ex.Message); }
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












