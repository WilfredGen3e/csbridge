using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using ColossalFramework;
using ColossalFramework.UI;
using UnityEngine;

namespace CSBridge
{
    /// <summary>Phase 6: camera control and screenshots, so an agent can look at what it builds.</summary>
    public static class CameraEndpoints
    {
        private static int shotCounter;

        public static void Register(HttpServer server)
        {
            server.Get("/camera", GetCamera);
            server.Post("/camera", SetCamera);
            server.Get("/screenshot", Screenshot);
            server.Get("/view", GetView);
            server.Post("/view", SetView);
            server.Post("/time", SetTime);
        }

        // Camera code touches Unity objects: run on the main thread.
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

        private static CameraController Cam()
        {
            CameraController c = ToolsModifierControl.cameraController;
            if (c == null) throw new ApiException(503, "Camera is not available (no city loaded?)");
            return c;
        }

        private static string CameraJson(CameraController c)
        {
            return new JsonWriter().Obj()
                .Prop("x", Math.Round(c.m_currentPosition.x, 1)).Prop("y", Math.Round(c.m_currentPosition.y, 1)).Prop("z", Math.Round(c.m_currentPosition.z, 1))
                .Prop("size", Math.Round(c.m_currentSize, 0))
                .Prop("heading", Math.Round(c.m_currentAngle.x, 1)).Prop("pitch", Math.Round(c.m_currentAngle.y, 1))
                .EndObj().ToString();
        }

        // GET /camera -> focus point (x,y,z), size (zoom distance in metres, smaller = closer), heading and pitch in degrees
        private static HttpResponse GetCamera(HttpRequest req)
        {
            return HttpResponse.Json(200, OnMainThread<string>(delegate () { return CameraJson(Cam()); }, 5000));
        }

        // POST /camera {"x":100,"z":50,"size":400,"heading":0,"pitch":45,"instant":true}
        //   size 40-3000 (view distance), pitch 0-90 (90 = straight down, like a map), heading 0-360. All optional except that x and z go together.
        private static HttpResponse SetCamera(HttpRequest req)
        {
            Dictionary<string, string> body = JsonReader.ParseFlat(req.Body);
            bool hasPos = body.ContainsKey("x") || body.ContainsKey("z");
            if (hasPos && !(body.ContainsKey("x") && body.ContainsKey("z"))) throw new ApiException(400, "Give both x and z");
            float x = hasPos ? BuildEndpoints.GetFloat(body, "x") : 0f, z = hasPos ? BuildEndpoints.GetFloat(body, "z") : 0f;
            if (hasPos) BuildEndpoints.CheckBounds(x, z);
            bool hasSize = body.ContainsKey("size"), hasHeading = body.ContainsKey("heading"), hasPitch = body.ContainsKey("pitch");
            float size = hasSize ? BuildEndpoints.GetFloat(body, "size") : 0f;
            float heading = hasHeading ? BuildEndpoints.GetFloat(body, "heading") : 0f;
            float pitch = hasPitch ? BuildEndpoints.GetFloat(body, "pitch") : 0f;
            if (hasSize && (size < 40f || size > 3000f)) throw new ApiException(400, "size must be 40-3000");
            if (hasPitch && (pitch < 0f || pitch > 90f)) throw new ApiException(400, "pitch must be 0-90");
            bool instant = !body.ContainsKey("instant") || JsonReader.GetBool(body, "instant");

            string json = OnMainThread<string>(delegate ()
            {
                CameraController c = Cam();
                if (hasPos)
                {
                    c.ClearTarget();   // stop following a vehicle/citizen
                    float y = Singleton<TerrainManager>.instance.SampleRawHeightSmoothWithWater(new Vector3(x, 0f, z), false, 0f);
                    c.m_targetPosition = new Vector3(x, y, z);
                }
                if (hasSize) c.m_targetSize = size;
                if (hasHeading || hasPitch)
                    c.m_targetAngle = new Vector2(hasHeading ? heading : c.m_targetAngle.x, hasPitch ? pitch : c.m_targetAngle.y);
                if (instant)
                {
                    c.m_currentPosition = c.m_targetPosition;
                    c.m_currentSize = c.m_targetSize;
                    c.m_currentAngle = c.m_targetAngle;
                }
                return CameraJson(c);
            }, 5000);
            return HttpResponse.Json(200, json);
        }

        // GET /screenshot?hide_ui=1  -> {"path": "/.../shot_1.png"}; the file is a PNG of the game view.
        private static HttpResponse Screenshot(HttpRequest req)
        {
            Dictionary<string, string> q = BuildEndpoints.ParseQuery(req.Query);
            bool hideUi = !q.ContainsKey("hide_ui") || (q["hide_ui"] != "0" && q["hide_ui"] != "false");
            string dir = Path.Combine(Path.GetTempPath(), "csbridge-screenshots");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "shot_" + (++shotCounter) + "_" + DateTime.Now.ToString("HHmmss") + ".png");
            if (File.Exists(path)) File.Delete(path);

            SavedFloat tilt = null;
            float tiltBefore = 0f;
            SavedInt dof = null;
            int dofBefore = 0;
            bool freeBefore = false;
            try
            {
                OnMainThread<bool>(delegate ()
                {
                    CameraController c = Cam();
                    // The camera re-shows the UI every frame unless it is in free-camera mode, so use that while capturing.
                    if (hideUi) { freeBefore = c.m_freeCamera; c.m_freeCamera = true; UIView.Show(false); }
                    // Switch off the game's depth-of-field / tilt-shift blur so the whole picture is sharp.
                    try
                    {
                        System.Reflection.BindingFlags nf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                        System.Reflection.FieldInfo f1 = typeof(CameraController).GetField("m_TiltShiftAmount", nf);
                        System.Reflection.FieldInfo f2 = typeof(CameraController).GetField("m_DOFMode", nf);
                        if (f1 != null) { tilt = (SavedFloat)f1.GetValue(c); tiltBefore = tilt.value; tilt.value = 0f; }
                        if (f2 != null) { dof = (SavedInt)f2.GetValue(c); dofBefore = dof.value; dof.value = 0; }
                    }
                    catch (Exception) { }
                    return true;
                }, 5000);
                Thread.Sleep(500);   // let a few frames render without UI and blur
                OnMainThread<bool>(delegate () { Application.CaptureScreenshot(path, 1); return true; }, 5000);

                // The file is written at the end of the frame; wait until it exists and stops growing.
                long last = -1;
                for (int i = 0; i < 80; i++)
                {
                    Thread.Sleep(100);
                    if (!File.Exists(path)) continue;
                    long len = new FileInfo(path).Length;
                    if (len > 0 && len == last) break;
                    last = len;
                }
            }
            finally
            {
                OnMainThread<bool>(delegate ()
                {
                    if (hideUi) { Cam().m_freeCamera = freeBefore; UIView.Show(true); }
                    if (tilt != null) tilt.value = tiltBefore;
                    if (dof != null) dof.value = dofBefore;
                    return true;
                }, 5000);
            }
            if (!File.Exists(path)) throw new ApiException(500, "The game did not write the screenshot");
            return HttpResponse.Json(200, new JsonWriter().Obj().Prop("ok", true).Prop("path", path)
                .Prop("bytes", new FileInfo(path).Length).EndObj().ToString());
        }

        // GET /view -> current info view and all available modes
        private static HttpResponse GetView(HttpRequest req)
        {
            string json = OnMainThread<string>(delegate ()
            {
                JsonWriter w = new JsonWriter().Obj().Prop("current", Singleton<InfoManager>.instance.CurrentMode.ToString()).ArrIn("modes");
                foreach (InfoManager.InfoMode m in Enum.GetValues(typeof(InfoManager.InfoMode))) w.Raw("\"" + m + "\"");
                return w.EndArr().EndObj().ToString();
            }, 5000);
            return HttpResponse.Json(200, json);
        }

        // POST /view {"mode":"Traffic"}  (None = normal view). Colours the map like the game's info panels do.
        private static HttpResponse SetView(HttpRequest req)
        {
            Dictionary<string, string> body = JsonReader.ParseFlat(req.Body);
            string name;
            if (!body.TryGetValue("mode", out name)) throw new ApiException(400, "Missing field: mode (see GET /view)");
            InfoManager.InfoMode mode = InfoManager.InfoMode.None;
            bool found = false;
            foreach (InfoManager.InfoMode m in Enum.GetValues(typeof(InfoManager.InfoMode)))
                if (string.Equals(m.ToString(), name, StringComparison.OrdinalIgnoreCase)) { mode = m; found = true; }
            if (!found) throw new ApiException(404, "Unknown view '" + name + "' (see GET /view)");
            string json = OnMainThread<string>(delegate ()
            {
                Singleton<InfoManager>.instance.SetCurrentMode(mode, InfoManager.SubInfoMode.Default);
                return new JsonWriter().Obj().Prop("ok", true).Prop("current", mode.ToString()).EndObj().ToString();
            }, 5000);
            return HttpResponse.Json(200, json);
        }

        // POST /time {"hour": 12.5}  -> sets the time of day (0-24) without changing the date; useful for screenshots
        private static HttpResponse SetTime(HttpRequest req)
        {
            float hour = BuildEndpoints.GetFloat(JsonReader.ParseFlat(req.Body), "hour");
            if (hour < 0f || hour >= 24f) throw new ApiException(400, "hour must be 0 <= hour < 24");
            string json = GameThread.Run<string>(delegate ()
            {
                SimulationManager sim = Singleton<SimulationManager>.instance;
                uint target = (uint)(hour * SimulationManager.DAYTIME_HOUR_TO_FRAME) & (SimulationManager.DAYTIME_FRAMES - 1);
                sim.m_dayTimeOffsetFrames = (target - sim.m_referenceFrameIndex) & (SimulationManager.DAYTIME_FRAMES - 1);
                sim.m_dayTimeFrame = (sim.m_currentFrameIndex + sim.m_dayTimeOffsetFrames) & (SimulationManager.DAYTIME_FRAMES - 1);
                sim.m_currentDayTimeHour = hour;
                return new JsonWriter().Obj().Prop("ok", true).Prop("hour", hour).EndObj().ToString();
            }, 3000);
            return HttpResponse.Json(200, json);
        }
    }
}
