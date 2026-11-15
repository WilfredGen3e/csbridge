using System;
using System.Collections.Generic;
using System.Threading;
using ColossalFramework;
using ColossalFramework.Threading;
using ColossalFramework.UI;
using UnityEngine;

namespace CSBridge
{
    /// <summary>
    /// Game UI helpers: list visible popup panels and close them (e.g. the milestone/unlock windows that freeze the game).
    /// UI code must run on Unity's main thread, so this goes through ThreadHelper.dispatcher, not the simulation thread.
    /// </summary>
    public static class UiEndpoints
    {
        public static void Register(HttpServer server)
        {
            server.Get("/ui", ListPanels);
            server.Post("/ui/close", Close);
            server.Post("/ui/autodismiss", SetAutoDismiss);
        }

        // ---- automatic dismissal of popups/notifications ("new zone available", milestones, tutorials) ----
        // Every 1.5 s the mod hides any top-level panel that was not visible when the city finished loading, plus a few
        // known popups. Switch off with POST /ui/autodismiss {"enabled":false} if you want to play by hand.
        private static Timer watcher;
        private static bool autoEnabled = true;
        private static HashSet<string> baseline;
        private static readonly Dictionary<string, int> dismissed = new Dictionary<string, int>();
        private static readonly object watchLock = new object();
        private static readonly string[] AlwaysDismiss = new string[] { "GenericTutorialHelper", "(Library) TutorialPanel", "UnlockingPanel" };

        public static void StartWatcher()
        {
            StopWatcher();
            lock (watchLock) { baseline = null; }
            watcher = new Timer(delegate (object o)
            {
                if (!autoEnabled) return;
                try { ThreadHelper.dispatcher.Dispatch(DismissNow); } catch (Exception) { }
            }, null, 4000, 1500);
        }

        public static void StopWatcher()
        {
            if (watcher != null) { watcher.Dispose(); watcher = null; }
        }

        private static void DismissNow()
        {
            try
            {
                UIView view = UIView.GetAView();
                if (view == null) return;
                List<UIPanel> visible = new List<UIPanel>();
                foreach (UIPanel p in view.GetComponentsInChildren<UIPanel>())
                {
                    if (p == null || !p.isVisible || p.name == null) continue;
                    if (Depth(p) > 3 || p.width < 100f || p.height < 40f) continue;
                    visible.Add(p);
                }
                int closed = 0;
                foreach (UIPanel p in visible)
                {
                    // Only known popups: never panels the player opens (pause menu, options, save/load, info panels).
                    if (Array.IndexOf(AlwaysDismiss, p.name) < 0) continue;
                    if (UIView.HasModalInput() && UIView.GetModalComponent() == p) UIView.PopModal();
                    p.Hide();
                    closed++;
                    lock (watchLock)
                    {
                        int c; dismissed.TryGetValue(p.name, out c); dismissed[p.name] = c + 1;
                    }
                    BridgeLog.Info("Auto-dismissed panel '" + p.name + "' (" + (int)p.width + "x" + (int)p.height + ")");
                }
                if (closed > 0)
                {
                    SimulationManager sim = Singleton<SimulationManager>.instance;
                    // Freezes caused by those popups are released from the simulation thread.
                    sim.AddAction(delegate () { if (sim.ForcedSimulationPaused) sim.ForcedSimulationPaused = false; });
                }
            }
            catch (Exception ex) { BridgeLog.Error("auto-dismiss", ex); }
        }

        // POST /ui/autodismiss {"enabled": true|false}
        private static HttpResponse SetAutoDismiss(HttpRequest req)
        {
            autoEnabled = JsonReader.GetBool(JsonReader.ParseFlat(req.Body), "enabled");
            return HttpResponse.Json(200, new JsonWriter().Obj().Prop("ok", true).Prop("auto_dismiss", autoEnabled).EndObj().ToString());
        }

        private static T OnMainThread<T>(Func<T> f, int timeoutMs)
        {
            T result = default(T);
            Exception error = null;
            ManualResetEvent done = new ManualResetEvent(false);
            ThreadHelper.dispatcher.Dispatch(delegate ()
            {
                try { result = f(); }
                catch (Exception ex) { error = ex; }
                finally { done.Set(); }
            });
            if (!done.WaitOne(timeoutMs)) throw new GameThread.TimeoutException("Game UI did not respond within " + timeoutMs + " ms");
            if (error != null) throw error;
            return result;
        }

        private static int Depth(UIComponent c)
        {
            int d = 0;
            while (c != null && c.parent != null) { d++; c = c.parent; }
            return d;
        }

        private static List<UIPanel> VisibleTopPanels()
        {
            List<UIPanel> list = new List<UIPanel>();
            UIView view = UIView.GetAView();
            if (view == null) return list;
            foreach (UIPanel p in view.GetComponentsInChildren<UIPanel>())
            {
                if (p == null || !p.isVisible || p.name == null) continue;
                // Popups are direct children of the view's root; deeper panels are parts of those.
                if (Depth(p) > 3) continue;
                if (p.width < 200f || p.height < 100f) continue;
                if (p.name == "FullScreenContainer" || p.name == "TSBar" || p.name == "InfoPanel") continue;
                list.Add(p);
            }
            return list;
        }

        // GET /ui -> visible top-level panels (name, size); useful to find a window that blocks the game
        private static HttpResponse ListPanels(HttpRequest req)
        {
            string json = OnMainThread<string>(delegate ()
            {
                JsonWriter w = new JsonWriter().Obj().Prop("auto_dismiss", autoEnabled);
                w.ObjIn("auto_dismissed");
                lock (watchLock) { foreach (KeyValuePair<string, int> kv in dismissed) w.Prop(kv.Key, kv.Value); }
                w.EndObj();
                w.ArrIn("panels");
                foreach (UIPanel p in VisibleTopPanels())
                    w.Obj().Prop("name", p.name).Prop("depth", Depth(p)).Prop("width", p.width).Prop("height", p.height)
                        .Prop("modal", UIView.HasModalInput() && UIView.GetModalComponent() == p).EndObj();
                return w.EndArr().EndObj().ToString();
            }, 5000);
            return HttpResponse.Json(200, json);
        }

        /// <summary>Hide a visible popup by name (main thread); returns false when it is not shown.</summary>
        public static bool CloseByName(string name)
        {
            return OnMainThread<bool>(delegate ()
            {
                foreach (UIPanel p in VisibleTopPanels())
                {
                    if (!string.Equals(p.name, name, StringComparison.OrdinalIgnoreCase)) continue;
                    if (UIView.HasModalInput() && UIView.GetModalComponent() == p) UIView.PopModal();
                    p.Hide();
                    return true;
                }
                return false;
            }, 5000);
        }

        // POST /ui/close {"name":"UnlockingPanel"}  (hides the panel; "all_modal":true closes the current modal one)
        private static HttpResponse Close(HttpRequest req)
        {
            System.Collections.Generic.Dictionary<string, string> body = JsonReader.ParseFlat(req.Body);
            string name;
            if (!body.TryGetValue("name", out name) || name.Length == 0) throw new ApiException(400, "Missing field: name (see GET /ui)");
            string json = OnMainThread<string>(delegate ()
            {
                foreach (UIPanel p in VisibleTopPanels())
                {
                    if (!string.Equals(p.name, name, StringComparison.OrdinalIgnoreCase)) continue;
                    if (UIView.HasModalInput() && UIView.GetModalComponent() == p) UIView.PopModal();
                    p.Hide();
                    return new JsonWriter().Obj().Prop("ok", true).Prop("closed", p.name).EndObj().ToString();
                }
                throw new ApiException(404, "No visible panel named '" + name + "' (see GET /ui)");
            }, 5000);
            return HttpResponse.Json(200, json);
        }
    }
}
