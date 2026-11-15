using System;
using System.Collections.Generic;
using System.Threading;
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
                JsonWriter w = new JsonWriter().Obj().ArrIn("panels");
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
