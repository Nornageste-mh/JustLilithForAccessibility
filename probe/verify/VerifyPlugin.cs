using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace LilithVerify
{
    [BepInPlugin("lilith.verify", "Lilith Verify", "0.0.1")]
    public class VerifyPlugin : BasePlugin
    {
        internal static ManualLogSource Log2;
        public override void Load()
        {
            Log2 = Log;
            ClassInjector.RegisterTypeInIl2Cpp<VerifyRunner>();
            var go = new GameObject("LilithVerify");
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<VerifyRunner>();
            Log.LogInfo("[verify] build 0.0.1 loaded");
        }
    }

    public class VerifyRunner : MonoBehaviour
    {
        private bool _toggled, _dumped, _activated, _off;
        private object _headItem;
        private int _frame;

        private void Update()
        {
            try
            {
                _frame++;
                float t = Time.unscaledTime;
                if (!_toggled && t > 30f) { _toggled = true; Toggle(); }
                else if (!_dumped && t > 34f) { _dumped = true; DumpItems(); }
                else if (!_activated && t > 40f) { _activated = true; ActivateHead(); }
                else if (!_off && t > 46f) { _off = true; Toggle(); }
            }
            catch (Exception e) { VerifyPlugin.Log2.LogError("[verify] " + e.GetType().Name + ": " + e.Message); }
        }

        private static Type UiNavType()
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { if (a.GetName().Name == "JustLilithA11y") return a.GetType("JustLilithA11y.UiNav", false); }
                catch { }
            }
            return null;
        }

        private void Toggle()
        {
            try
            {
                var t = UiNavType();
                if (t == null) { VerifyPlugin.Log2.LogWarning("[verify] UiNav 类型未找到"); return; }
                var m = t.GetMethod("Toggle", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (m == null) { VerifyPlugin.Log2.LogWarning("[verify] Toggle 未找到"); return; }
                m.Invoke(null, null);
                VerifyPlugin.Log2.LogInfo("[verify] Toggle 已调用");
            }
            catch (Exception e) { VerifyPlugin.Log2.LogError("[verify] Toggle 失败 " + e.GetType().Name + ": " + e.Message); }
        }

        private static IEnumerable ItemsOf(Type t)
        {
            var p = t.GetProperty("Items", BindingFlags.NonPublic | BindingFlags.Static);
            if (p == null) return null;
            return p.GetValue(null) as IEnumerable;
        }

        private static string NameOf(object item)
        {
            try
            {
                var c = item as UnityEngine.Component;
                if (c != null) return c.gameObject.name;
                var pi = item.GetType().GetProperty("name");
                if (pi != null) return pi.GetValue(item) as string;
            }
            catch { }
            return "?";
        }

        private void DumpItems()
        {
            try
            {
                var t = UiNavType();
                var items = ItemsOf(t);
                if (items == null) { VerifyPlugin.Log2.LogWarning("[verify] Items 为空"); return; }
                int i = 0;
                foreach (var it in items)
                {
                    i++;
                    string nm = NameOf(it);
                    VerifyPlugin.Log2.LogInfo("[verify] item #" + i + " " + nm);
                    if (nm != null && nm.IndexOf("Head", StringComparison.OrdinalIgnoreCase) >= 0 && _headItem == null)
                        _headItem = it;
                }
                VerifyPlugin.Log2.LogInfo("[verify] items=" + i + " head=" + (_headItem != null));
            }
            catch (Exception e) { VerifyPlugin.Log2.LogError("[verify] dump 失败 " + e.GetType().Name + ": " + e.Message); }
        }

        private void ActivateHead()
        {
            try
            {
                if (_headItem == null) { VerifyPlugin.Log2.LogWarning("[verify] 没有 head item"); return; }
                var t = UiNavType();
                var m = t.GetMethod("Activate", BindingFlags.NonPublic | BindingFlags.Static);
                if (m == null) { VerifyPlugin.Log2.LogWarning("[verify] Activate 未找到"); return; }
                VerifyPlugin.Log2.LogInfo("[verify] Activate(" + NameOf(_headItem) + ")");
                m.Invoke(null, new object[] { _headItem });
                VerifyPlugin.Log2.LogInfo("[verify] Activate 返回");
            }
            catch (Exception e) { VerifyPlugin.Log2.LogError("[verify] Activate 失败 " + e.GetType().Name + ": " + e.Message); }
        }
    }
}
