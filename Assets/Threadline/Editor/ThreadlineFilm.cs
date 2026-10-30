using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

// A 12-second camera move in one take, rendered frame by frame: thread macro, pull back to the whole board and the bust on its turntable,
// the bust turns and the threads re-tension live, then push back in to the macro. The last frame matches the first.
// JPG frames and a log of thread events (for syncing sound) are written to OutDir; encode them into a clip outside Unity.
public static class ThreadlineFilm
{
    public static string OutDir => Path.GetFullPath(Path.Combine(Application.dataPath, "../Captures/Film"));
    const string ScenePath = "Assets/Threadline/Threadline.unity";
    const float Duration = 12f;

    // Camera key: where we look, where from (offset from the look target), aperture
    struct Key
    {
        public float t;
        public Vector3 target, offset;
        public float aperture;
        public Key(float t, Vector3 target, Vector3 offset, float aperture)
        {
            this.t = t; this.target = target; this.offset = offset; this.aperture = aperture;
        }
    }

    // Macro at the board edge: the brass nails and the fan of threads read as string art from the first frame.
    // The wide shot (board with the bust below) depends on the frame format: wideY is where we look, wideDist is from how far
    static Key[] Keys;

    static Key[] MakeKeys(float wideY, float wideDist)
    {
        return new[]
        {
            new Key(0.0f, new Vector3(-0.33f, 0.33f, 0f), new Vector3(0.11f, -0.07f, -0.20f), 5.6f),
            new Key(2.0f, new Vector3(-0.25f, 0.38f, 0f), new Vector3(0.09f, -0.09f, -0.23f), 5.6f),
            new Key(4.6f, new Vector3(0f, wideY, 0f), new Vector3(0f, 0.10f, -wideDist), 11f),
            new Key(9.4f, new Vector3(0f, wideY, 0f), new Vector3(0.06f, 0.08f, -wideDist * 0.937f), 11f),
            new Key(12.0f, new Vector3(-0.33f, 0.33f, 0f), new Vector3(0.11f, -0.07f, -0.20f), 5.6f),
        };
    }

    // 9:16 vertical for TikTok and Shorts, 4:5 for feed posts
    public static void Render916() => Render(2160, 3840, 60, -0.43f, 3.95f, OutDir);
    public static void Render45() => Render(1728, 2160, 60, -0.40f, 3.62f, OutDir + "_45");

    // Portrait build-up over the first 4.4 s: only the most valuable threads (shadows, curls) at first, then midtones.
    // From 10.2 s the threads unwind (each thread becomes "darker" for the solver, so extras get removed); by the loop point the board is almost empty
    static void Solver(StringArt sa, float t, float baseDarkness)
    {
        float build = Mathf.Clamp01(t / 4.4f);
        sa.addProbability = Mathf.Lerp(0.02f, 0.08f, build);
        sa.addThresholdBoost = 0.45f * (1f - build) * (1f - build);
        float unwind = Mathf.Clamp01((t - 10.2f) / 1.6f);
        sa.lineDarkness = Mathf.Lerp(baseDarkness, 0.9f, unwind * unwind);
    }

    static float Ease(float s) => s * s * s * (s * (s * 6f - 15f) + 10f);

    // The hold segment (second to third key) runs linearly, the others ease in and out
    static void CameraAt(float t, out Vector3 pos, out Vector3 target, out float aperture)
    {
        int i = 0;
        while (i < Keys.Length - 2 && t > Keys[i + 1].t) i++;
        var a = Keys[i];
        var b = Keys[i + 1];
        float s = Mathf.Clamp01((t - a.t) / (b.t - a.t));
        if (i != 2) s = Ease(s);
        target = Vector3.Lerp(a.target, b.target, s);
        // Rotate the direction, interpolate distance logarithmically: the pull-back feels uniform
        var dir = Vector3.Slerp(a.offset.normalized, b.offset.normalized, s);
        float dist = Mathf.Exp(Mathf.Lerp(Mathf.Log(a.offset.magnitude), Mathf.Log(b.offset.magnitude), s));
        pos = target + dir * dist;
        aperture = Mathf.Exp(Mathf.Lerp(Mathf.Log(a.aperture), Mathf.Log(b.aperture), s));
    }

    static float TurnAngle(float t)
    {
        if (t < 5.2f) return 0f;
        if (t < 7.2f) return 65f * Ease((t - 5.2f) / 2f);
        if (t < 7.6f) return 65f;
        if (t < 9.4f) return 65f * (1f - Ease((t - 7.6f) / 1.8f));
        return 0f;
    }

    public static void Render(int width, int height, int fps, float wideY = -0.43f, float wideDist = 3.95f, string outDir = null)
    {
        outDir ??= OutDir;
        Keys = MakeKeys(wideY, wideDist);
        if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
        Directory.CreateDirectory(outDir);
        if (SceneManager.GetActiveScene().path != ScenePath) EditorSceneManager.OpenScene(ScenePath);

        var sa = Object.FindFirstObjectByType<StringArt>();
        var switcher = sa.GetComponent<FigureSwitcher>();
        switcher.figure = 0;
        switcher.Apply();
        var subject = GameObject.Find("Subject").transform;
        var cam = UnityEngine.Camera.main;
        var volume = Object.FindFirstObjectByType<Volume>();
        volume.sharedProfile.TryGet(out DepthOfField dof);
        if (dof) dof.active = true;

        sa.Setup();
        float dt = 1f / fps;
        float baseDarkness = sa.lineDarkness;
        float baseAdd = sa.addProbability;
        // The board starts empty: the first frame is just nails, threads start tensioning on camera
        subject.rotation = Quaternion.identity;
        sa.ResetThreads();
        RenderCam(sa.subjectCamera, sa.sceneRT);

        var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
        var flat = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32);
        var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
        int n = sa.nails;
        float[] prev = ReadState(sa);
        var log = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;
        int frames = Mathf.RoundToInt(Duration * fps);

        for (int f = 0; f < frames; f++)
        {
            float t = f * dt;
            EditorUtility.DisplayProgressBar("Threadline film", $"frame {f + 1}/{frames}", f / (float)frames);

            subject.rotation = Quaternion.Euler(0f, TurnAngle(t), 0f);
            Solver(sa, t, baseDarkness);
            RenderCam(sa.subjectCamera, sa.sceneRT);
            sa.Tick(dt);

            CameraAt(t, out var pos, out var target, out float aperture);
            cam.transform.position = pos;
            cam.transform.rotation = Quaternion.LookRotation(target - pos, Vector3.up);
            var fwd = cam.transform.forward;
            float focus = fwd.z > 1e-4f ? -pos.z / fwd.z : 4f;
            if (dof)
            {
                dof.focusDistance.value = focus;
                dof.aperture.value = aperture;
            }
            RenderCam(cam, rt);

            Graphics.Blit(rt, flat);
            var active = RenderTexture.active;
            RenderTexture.active = flat;
            tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            tex.Apply();
            RenderTexture.active = active;
            File.WriteAllBytes(Path.Combine(outDir, $"f{f:D4}.jpg"), tex.EncodeToJPG(94));

            // Events for sound: which threads were tensioned and which were removed this frame
            float[] cur = ReadState(sa);
            int added = 0, removed = 0;
            var items = new List<string>();
            for (int i = 0; i < cur.Length; i++)
            {
                bool on = cur[i] > 0.5f, was = prev[i] > 0.5f;
                if (on == was) continue;
                if (on) added++; else removed++;
                if (!on || items.Count >= 40) continue;
                int a = i % n, b = i / n;
                float ta = a * Mathf.PI * 2f / n, tb = b * Mathf.PI * 2f / n;
                var pa = new Vector2(Mathf.Cos(ta), Mathf.Sin(ta));
                var pb = new Vector2(Mathf.Cos(tb), Mathf.Sin(tb));
                items.Add(string.Format(inv, "{0:F3}:{1:F3}", Vector2.Distance(pa, pb) * 0.5f, (pa.x + pb.x) * 0.5f));
            }
            prev = cur;
            log.Append(f).Append(';').Append(added).Append(';').Append(removed).Append(';').Append(string.Join(",", items)).Append('\n');
        }

        File.WriteAllText(Path.Combine(outDir, "events.txt"), log.ToString());
        EditorUtility.ClearProgressBar();
        Object.DestroyImmediate(tex);
        rt.Release();
        flat.Release();
        subject.rotation = Quaternion.identity;
        sa.lineDarkness = baseDarkness;
        sa.addProbability = baseAdd;
        sa.addThresholdBoost = 0f;
        sa.Teardown();
        Debug.Log($"[Threadline] film done: {frames} frames {width}x{height} in {outDir}");
    }

    // One main camera frame after the portrait has built up: checks composition without a screen recording
    public static void RenderStill(int width, int height, string path)
    {
        if (SceneManager.GetActiveScene().path != ScenePath) EditorSceneManager.OpenScene(ScenePath);
        var sa = Object.FindFirstObjectByType<StringArt>();
        var cam = UnityEngine.Camera.main;
        cam.aspect = width / (float)height;
        sa.Setup();
        for (int k = 0; k < 200; k++)
        {
            RenderCam(sa.subjectCamera, sa.sceneRT);
            sa.Tick(1f / 60f);
        }
        var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
        var flat = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32);
        RenderCam(cam, rt);
        Graphics.Blit(rt, flat);
        var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
        var active = RenderTexture.active;
        RenderTexture.active = flat;
        tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
        tex.Apply();
        RenderTexture.active = active;
        File.WriteAllBytes(path, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);
        rt.Release();
        flat.Release();
        cam.ResetAspect();
        sa.Teardown();
    }

    static float[] ReadState(StringArt sa)
    {
        var req = AsyncGPUReadback.Request(sa.stateRT);
        req.WaitForCompletion();
        return req.GetData<float>().ToArray();
    }

    static void RenderCam(UnityEngine.Camera cam, RenderTexture rt)
    {
        var req = new RenderPipeline.StandardRequest { destination = rt };
        if (RenderPipeline.SupportsRenderRequest(cam, req))
        {
            RenderPipeline.SubmitRenderRequest(cam, req);
            return;
        }
        var prevTarget = cam.targetTexture;
        cam.targetTexture = rt;
        cam.Render();
        cam.targetTexture = prevTarget;
    }
}
