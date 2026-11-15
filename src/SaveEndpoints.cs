using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using ColossalFramework;
using ColossalFramework.IO;
using ColossalFramework.Packaging;
using ColossalFramework.UI;
using ColossalFramework.PlatformServices;

namespace CSBridge
{
    /// <summary>Phase 8: saving. Uses the game's own SavePanel, so the save also becomes the "last save" for --continuelastsave.</summary>
    public static class SaveEndpoints
    {
        public static void Register(HttpServer server)
        {
            server.Get("/saves", Saves);
            server.Post("/save", Save);
            server.Post("/load", Load);
        }

        // POST /load {"name":"My City"}: loads a save the way the game's Quick Load does. The current city is dropped
        // (unsaved progress is lost), the mod restarts with the new city and the API is back after ~30-60 s.
        private static HttpResponse Load(HttpRequest req)
        {
            string name;
            if (!JsonReader.ParseFlat(req.Body).TryGetValue("name", out name) || name.Trim().Length == 0) throw new ApiException(400, "Missing field: name");
            name = name.Trim();
            string json = OnMainThread<string>(delegate ()
            {
                LoadingManager lm = Singleton<LoadingManager>.instance;
                if (lm.m_currentlyLoading) throw new ApiException(409, "The game is loading already");
                Package.Asset found = null;
                List<string> names = new List<string>();
                foreach (Package.Asset a in PackageManager.FilterAssets(UserAssetType.SaveGameMetaData))
                {
                    if (a == null) continue;
                    names.Add(a.name);
                    if (string.Equals(a.name, name, StringComparison.OrdinalIgnoreCase)) found = a;
                }
                if (found == null) throw new ApiException(404, "No save named '" + name + "'. Known: " + string.Join(", ", names.ToArray()));
                SaveGameMetaData meta = found.Instantiate<SaveGameMetaData>();
                SimulationMetaData sim = new SimulationMetaData();
                sim.m_CityName = meta.cityName;
                sim.m_updateMode = SimulationManager.UpdateMode.LoadGame;
                lm.LoadLevel(meta.assetRef, "Game", "InGame", sim);
                return new JsonWriter().Obj().Prop("ok", true).Prop("loading", found.name).Prop("note", "API is unavailable for ~30-60 s while the city loads").EndObj().ToString();
            }, 15000);
            return HttpResponse.Json(200, json);
        }

        private static string PathFor(string name)
        {
            return Path.Combine(DataLocation.saveLocation, PathEscaper.Escape(name) + PackageManager.packageExtension);
        }

        private static T OnMainThread<T>(Func<T> f, int timeoutMs)
        {
            T result = default(T);
            Exception error = null;
            ManualResetEvent done = new ManualResetEvent(false);
            ColossalFramework.Threading.ThreadHelper.dispatcher.Dispatch(delegate ()
            {
                try { result = f(); }
                catch (Exception ex) { error = ex; }
                finally { done.Set(); }
            });
            if (!done.WaitOne(timeoutMs)) throw new GameThread.TimeoutException("Game did not respond within " + timeoutMs + " ms");
            if (error != null) throw error;
            return result;
        }

        // GET /saves -> savegame files (name, modified time, size), newest first
        private static HttpResponse Saves(HttpRequest req)
        {
            List<FileInfo> files = new List<FileInfo>();
            if (Directory.Exists(DataLocation.saveLocation))
                foreach (string f in Directory.GetFiles(DataLocation.saveLocation, "*" + PackageManager.packageExtension))
                    files.Add(new FileInfo(f));
            files.Sort(delegate (FileInfo a, FileInfo b) { return b.LastWriteTime.CompareTo(a.LastWriteTime); });
            JsonWriter w = new JsonWriter().Obj().ArrIn("saves");
            foreach (FileInfo fi in files)
                w.Obj().Prop("name", Path.GetFileNameWithoutExtension(fi.Name))
                    .Prop("modified", fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")).Prop("bytes", fi.Length).EndObj();
            return HttpResponse.Json(200, w.EndArr().EndObj().ToString());
        }

        // POST /save {"name":"My City","overwrite":false}
        // Refuses to replace an existing save unless overwrite=true. The new save becomes the last save,
        // so scripts/restart-game.sh (--continuelastsave) will load it next.
        private static HttpResponse Save(HttpRequest req)
        {
            Dictionary<string, string> body = JsonReader.ParseFlat(req.Body);
            string name;
            if (!body.TryGetValue("name", out name) || name.Trim().Length == 0) throw new ApiException(400, "Missing field: name");
            name = name.Trim();
            if (name.Length > 64) throw new ApiException(400, "name is too long (max 64)");
            bool overwrite = body.ContainsKey("overwrite") && JsonReader.GetBool(body, "overwrite");
            string path = PathFor(name);
            DateTime before = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            if (File.Exists(path) && !overwrite)
                throw new ApiException(409, "A save named '" + name + "' already exists; choose another name or pass overwrite=true");

            bool started = OnMainThread<bool>(delegate ()
            {
                SavePanel panel = UIView.library.Get<SavePanel>("SavePanel");
                if (panel == null) throw new ApiException(503, "Save panel not available (no city loaded?)");
                return panel.SaveGame(name);
            }, 10000);
            if (!started) throw new ApiException(409, "The game cannot save right now (still loading, or another save is in progress)");

            // Saving is asynchronous: wait until the file is (re)written and stops changing.
            long lastLen = -1;
            for (int i = 0; i < 300; i++)
            {
                Thread.Sleep(200);
                if (!File.Exists(path)) continue;
                if (File.GetLastWriteTimeUtc(path) <= before) continue;
                long len = new FileInfo(path).Length;
                if (len > 0 && len == lastLen) break;
                lastLen = len;
            }
            if (!File.Exists(path) || File.GetLastWriteTimeUtc(path) <= before)
                throw new ApiException(500, "The save did not finish in time; check the game");
            return HttpResponse.Json(200, new JsonWriter().Obj().Prop("ok", true).Prop("name", name)
                .Prop("bytes", new FileInfo(path).Length).EndObj().ToString());
        }
    }
}
