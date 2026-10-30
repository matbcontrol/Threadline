using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

// Builds the test scene and captures frames without the editor window:
// Unity.exe -batchmode -projectPath <project> -executeMethod ThreadlineSetup.BatchAll
public static class ThreadlineSetup
{
    const string Root = "Assets/Threadline";
    const string ScenePath = Root + "/Threadline.unity";
    // Plaster cast scans from SMK, public domain (open.smk.dk). Converted from STL to OBJ, Z up.
    // Rotation is picked so the figure faces the camera when the turntable is at zero.
    // scale: how many times the figure exceeds the common height; lift: where the figure's bottom sits, as a fraction of that height.
    // This way a bust with a chest gets its head into frame while the pedestal drops out below.
    struct Figure
    {
        public string name, file;
        public Vector3 euler;
        public float scale, lift;
        public Figure(string name, string file, Vector3 euler, float scale = 1f, float lift = 0f)
        {
            this.name = name; this.file = file; this.euler = euler; this.scale = scale; this.lift = lift;
        }
    }
    static readonly Figure[] Figures =
    {
        new Figure("David", "David.obj", new Vector3(-90f, 125f, 0f)),                    // KAS2232
        new Figure("Homer", "Homer.obj", new Vector3(-90f, 180f, 0f), 1.7f, -0.8f),       // KAS210
        new Figure("Alexander", "Alexander.obj", new Vector3(-90f, 180f, 0f)),            // KAS282
        new Figure("Horse", "Horse.obj", new Vector3(-90f, 270f, 0f), 0.85f, -0.22f),     // KAS2120, in profile
    };
    const float ModelImportScale = 0.01f;
    // Bust on a pedestal to the right of the board: the wide Game view shows both the statue and its thread portrait
    public static readonly Vector3 SubjectPosition = new Vector3(0.95f, -0.45f, -0.15f);
    // All figures are scaled to one height, so one subject camera framing fits them all
    const float FigureHeight = 0.5f;
    // Framing: share of the figure height inside the circle and upward shift of the center (as a fraction of height)
    const float FrameHeight = 0.85f;
    const float FrameUp = 0.05f;
    static string CaptureDir => Path.GetFullPath(Path.Combine(Application.dataPath, "../Captures"));

    public static void BatchAll()
    {
        try
        {
            BuildScene();
            Capture();
            EditorApplication.Exit(0);
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            EditorApplication.Exit(1);
        }
    }

    [MenuItem("Threadline/Build Scene")]
    public static void BuildScene()
    {
        int subjectLayer = EnsureLayer("Subject");
        SetMsaa(4);
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        var plaster = MakeLit("Plaster", new Color(0.92f, 0.91f, 0.88f), 0f, 0.15f);
        var board = MakeLit("Board", new Color(0.9f, 0.87f, 0.8f), 0f, 0.12f);
        board.SetTexture("_BaseMap", MakePaperTexture());
        var brass = MakeLit("Brass", new Color(0.66f, 0.5f, 0.26f), 0.7f, 0.45f);
        var pedestalMat = MakeLit("Pedestal", new Color(0.13f, 0.125f, 0.12f), 0f, 0.1f);
        var turntableMat = MakeLit("Turntable", new Color(0.03f, 0.03f, 0.03f), 0.2f, 0.45f);

        // Hard side key and a rim light from behind, ambient almost off:
        // the threads need deep shadow on one half of the face
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.09f, 0.09f, 0.095f);
        RenderSettings.reflectionIntensity = 0.1f;
        // Portrait lighting on the bust: key from the front side, half the face in shadow; the key never burns the face to pure white.
        // Bust lights and board light are split by layer so the portrait does not depend on how the board is lit
        var key = MakeLight("Key Light", new Vector3(15f, 70f, 0f), 1.6f);
        var rim = MakeLight("Rim Light", new Vector3(10f, -120f, 0f), 0.6f);
        key.cullingMask = rim.cullingMask = 1 << subjectLayer;
        // Board light from the upper left; the thread shadows in the shader are offset to match it
        var boardLight = MakeLight("Board Light", new Vector3(25f, 25f, 0f), 1.05f);
        boardLight.cullingMask = ~(1 << subjectLayer);
        boardLight.shadows = LightShadows.None;

        // The bust stands on a turntable to the right of the board and a little lower: the viewer sees both the statue and its thread portrait
        var subject = new GameObject("Subject");
        subject.transform.position = SubjectPosition;
        // The figure switcher lives next to StringArt so both show up in one inspector during recording
        // Switcher goes first: in recordings the figure slider should sit at the top of the inspector
        var saGo = new GameObject("StringArt");
        var switcher = saGo.AddComponent<FigureSwitcher>();
        var sa = saGo.AddComponent<StringArt>();
        var placed = new System.Collections.Generic.List<GameObject>();
        foreach (var fig in Figures)
        {
            string path = $"{Root}/Models/{fig.file}";
            ConfigureModelImport(path);
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (!model) { Debug.LogWarning("[Threadline] missing model " + path); continue; }
            var holder = new GameObject(fig.name);
            holder.transform.SetParent(subject.transform, false);
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(model);
            inst.transform.SetParent(holder.transform, false);
            inst.transform.localEulerAngles = fig.euler;
            // One height for all; the scan origin is in a corner, so the figure center is placed over the pivot
            var b = RendererBounds(inst);
            float k = FigureHeight * fig.scale / b.size.y;
            inst.transform.localScale *= k;
            b = RendererBounds(inst);
            var sp = subject.transform.position;
            inst.transform.position -= new Vector3(b.center.x - sp.x, b.min.y - sp.y - fig.lift * FigureHeight, b.center.z - sp.z);
            placed.Add(holder);
        }
        if (placed.Count == 0)
        {
            var holder = new GameObject("Placeholder");
            holder.transform.SetParent(subject.transform, false);
            BuildPlaceholderBust(holder.transform);
            placed.Add(holder);
        }
        switcher.figures = placed.ToArray();
        switcher.figure = 0;
        switcher.turnTarget = subject.transform;
        switcher.Apply();

        foreach (var r in subject.GetComponentsInChildren<Renderer>(true))
        {
            var mats = new Material[r.sharedMaterials.Length];
            for (int i = 0; i < mats.Length; i++) mats[i] = plaster;
            r.sharedMaterials = mats;
        }
        SetLayer(subject, subjectLayer);
        subject.AddComponent<Spin>().degreesPerSecond = Vector3.zero;

        // The turntable spins with the bust, but the subject camera does not see it (default layer)
        var table = Primitive(PrimitiveType.Cylinder, "Turntable", subject.transform, turntableMat);
        table.transform.localPosition = new Vector3(0f, -0.012f, 0f);
        table.transform.localScale = new Vector3(0.36f, 0.012f, 0.36f);
        var pedestal = Primitive(PrimitiveType.Cube, "Pedestal", null, pedestalMat);
        pedestal.transform.position = SubjectPosition + new Vector3(0f, -0.024f - 0.75f, 0f);
        pedestal.transform.localScale = new Vector3(0.42f, 1.5f, 0.42f);

        var bounds = new Bounds(subject.transform.position + Vector3.up * FigureHeight * 0.5f, new Vector3(FigureHeight, FigureHeight, FigureHeight));
        var subjectCam = new GameObject("SubjectCam").AddComponent<Camera>();
        subjectCam.clearFlags = CameraClearFlags.SolidColor;
        // White background maps to zero darkness, so no threads are spent outside the figure
        subjectCam.backgroundColor = Color.white;
        subjectCam.cullingMask = 1 << subjectLayer;
        subjectCam.fieldOfView = 25f;
        subjectCam.nearClipPlane = 0.01f;
        subjectCam.allowMSAA = false;
        subjectCam.allowHDR = false;
        subjectCam.depth = -10f;
        subjectCam.GetUniversalAdditionalCameraData().renderPostProcessing = false;
        // Tight framing on the head: facial features must be larger than the nail spacing
        Vector3 focus = bounds.center + Vector3.up * bounds.size.y * FrameUp;
        float height = bounds.size.y * FrameHeight;
        float dist = height * 0.5f / Mathf.Tan(subjectCam.fieldOfView * 0.5f * Mathf.Deg2Rad);
        subjectCam.transform.position = focus + Vector3.back * dist;
        subjectCam.transform.LookAt(focus);

        var mainCam = new GameObject("Main Camera").AddComponent<Camera>();
        mainCam.tag = "MainCamera";
        mainCam.transform.position = new Vector3(0.3f, 0f, -2.35f);
        mainCam.transform.LookAt(new Vector3(0.3f, 0f, 0f));
        mainCam.fieldOfView = 33f;
        mainCam.nearClipPlane = 0.01f;
        mainCam.clearFlags = CameraClearFlags.SolidColor;
        mainCam.backgroundColor = new Color(0.035f, 0.035f, 0.04f);
        mainCam.GetUniversalAdditionalCameraData().renderPostProcessing = true;

        var post = new GameObject("Post").AddComponent<Volume>();
        post.isGlobal = true;
        post.sharedProfile = MakePostProfile();
        // Editor recordings should be sharp across the whole frame; depth of field is enabled only by the film render
        if (post.sharedProfile.TryGet(out DepthOfField dofOff)) dofOff.active = false;

        sa.subjectCamera = subjectCam;
        sa.boardMaterial = board;
        sa.nailMaterial = brass;
        mainCam.gameObject.AddComponent<AudioListener>();
        AssignAudio(saGo.AddComponent<ThreadAudio>());

        EditorSceneManager.SaveScene(scene, ScenePath);
        Debug.Log($"[Threadline] scene saved, figures: {string.Join(", ", placed.ConvertAll(p => p.name))}");
    }

    // Thread sound for the scene that is already open, without rebuilding it
    [MenuItem("Threadline/Setup Audio")]
    public static void SetupAudio()
    {
        var sa = Object.FindFirstObjectByType<StringArt>();
        if (!sa) { Debug.LogWarning("[Threadline] no StringArt in the scene"); return; }
        var ta = sa.GetComponent<ThreadAudio>();
        if (!ta) ta = sa.gameObject.AddComponent<ThreadAudio>();
        AssignAudio(ta);
        var cam = Camera.main;
        if (cam && !cam.GetComponent<AudioListener>()) cam.gameObject.AddComponent<AudioListener>();
        EditorSceneManager.MarkSceneDirty(sa.gameObject.scene);
        if (!Application.isPlaying) EditorSceneManager.SaveScene(sa.gameObject.scene);
    }

    // Clips come from the Audio folder by name prefix: pull, nail, release, rustle
    static void AssignAudio(ThreadAudio ta)
    {
        var clips = new System.Collections.Generic.List<AudioClip>();
        foreach (var guid in AssetDatabase.FindAssets("t:AudioClip", new[] { $"{Root}/Audio" }))
            clips.Add(AssetDatabase.LoadAssetAtPath<AudioClip>(AssetDatabase.GUIDToAssetPath(guid)));
        ta.pullClips = clips.FindAll(c => c.name.StartsWith("pull")).ToArray();
        ta.nailClips = clips.FindAll(c => c.name.StartsWith("nail")).ToArray();
        ta.releaseClips = clips.FindAll(c => c.name.StartsWith("release")).ToArray();
        ta.rustleLoop = clips.Find(c => c.name.StartsWith("rustle"));
        EditorUtility.SetDirty(ta);
        Debug.Log($"[Threadline] audio: {ta.pullClips.Length} pull, {ta.nailClips.Length} nail, {ta.releaseClips.Length} release, rustle {(ta.rustleLoop ? ta.rustleLoop.name : "none")}");
    }

    public static void BatchTurntable()
    {
        try
        {
            BuildScene();
            CaptureTurntable();
            EditorApplication.Exit(0);
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            EditorApplication.Exit(1);
        }
    }

    // Finished thread portrait of each figure: figure_N.png
    public static void CaptureFigures()
    {
        Directory.CreateDirectory(CaptureDir);
        if (SceneManager.GetActiveScene().path != ScenePath) EditorSceneManager.OpenScene(ScenePath);
        var sa = Object.FindFirstObjectByType<StringArt>();
        var switcher = sa.GetComponent<FigureSwitcher>();
        var mainCam = Camera.main;
        sa.Setup();
        var frame = new RenderTexture(1080, 1080, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
        for (int f = 0; f < switcher.figures.Length; f++)
        {
            switcher.figure = f;
            switcher.Apply();
            for (int k = 0; k < 120; k++)
            {
                RenderCamera(sa.subjectCamera, sa.sceneRT);
                sa.Tick(1f / 30f);
            }
            RenderCamera(mainCam, frame);
            SaveColor(frame, Path.Combine(CaptureDir, $"figure_{f}.png"));
            SaveColor(sa.sceneRT, Path.Combine(CaptureDir, $"figure_{f}_model.png"));
        }
        switcher.figure = 0;
        switcher.Apply();
        frame.Release();
        sa.Teardown();
        Debug.Log("[Threadline] figures captured");
    }

    // Sheet for tuning rotation: each figure from four sides (turntable at 0, 90, 180, 270 degrees)
    public static void CaptureTurntable()
    {
        Directory.CreateDirectory(CaptureDir);
        var switcher = Object.FindFirstObjectByType<FigureSwitcher>();
        var subject = GameObject.Find("Subject").transform;
        var cam = GameObject.Find("SubjectCam").GetComponent<Camera>();
        const int cell = 192;
        int rows = switcher.figures.Length;
        var sheet = new Texture2D(cell * 4, cell * rows, TextureFormat.RGBA32, false);
        var rt = new RenderTexture(cell, cell, 24, RenderTextureFormat.ARGB32);
        var tex = new Texture2D(cell, cell, TextureFormat.RGBA32, false);
        for (int f = 0; f < rows; f++)
        {
            switcher.figure = f;
            switcher.Apply();
            for (int a = 0; a < 4; a++)
            {
                subject.rotation = Quaternion.Euler(0f, a * 90f, 0f);
                RenderCamera(cam, rt);
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, cell, cell), 0, 0);
                tex.Apply();
                RenderTexture.active = prev;
                sheet.SetPixels(a * cell, (rows - 1 - f) * cell, cell, cell, tex.GetPixels());
            }
        }
        subject.rotation = Quaternion.identity;
        switcher.figure = 0;
        switcher.Apply();
        File.WriteAllBytes(Path.Combine(CaptureDir, "turntable.png"), sheet.EncodeToPNG());
        rt.Release();
        Debug.Log("[Threadline] turntable saved");
    }

    [MenuItem("Threadline/Capture Frames")]
    public static void Capture()
    {
        Directory.CreateDirectory(CaptureDir);
        if (SceneManager.GetActiveScene().path != ScenePath) EditorSceneManager.OpenScene(ScenePath);
        var sa = Object.FindFirstObjectByType<StringArt>();
        var subject = GameObject.Find("Subject");
        var mainCam = Camera.main;
        sa.Setup();

        var frame = new RenderTexture(1080, 1080, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
        float[] angles = { 0f, 35f, 75f };
        for (int f = 0; f < angles.Length; f++)
        {
            subject.transform.rotation = Quaternion.Euler(0f, angles[f], 0f);
            // The search settles within about a hundred Tick calls, so 120 is enough
            for (int k = 0; k < 120; k++)
            {
                RenderCamera(sa.subjectCamera, sa.sceneRT);
                sa.Tick(1f / 30f);
            }
            RenderCamera(mainCam, frame);
            SaveColor(frame, Path.Combine(CaptureDir, $"frame_{f}.png"));
            SaveColor(sa.sceneRT, Path.Combine(CaptureDir, $"scene_{f}.png"));
            if (f == 0)
            {
                SaveColor(sa.sceneRT, Path.Combine(CaptureDir, "scene_rt.png"));
                SaveSingle(sa.targetRT, Path.Combine(CaptureDir, "target_rt.png"), 1f);
                SaveSingle(sa.coverageRT, Path.Combine(CaptureDir, "coverage_rt.png"), 1f);
                CpuCheck(sa);
            }
            var cnt = AsyncGPUReadback.Request(sa.stateRT);
            cnt.WaitForCompletion();
            int threads = 0;
            foreach (float v in cnt.GetData<float>()) if (v > 0.5f) threads++;
            Debug.Log($"[Threadline] frame {f}: angle {angles[f]}, threads {threads}");
        }

        frame.Release();
        sa.Teardown();
        Debug.Log("[Threadline] capture done: " + CaptureDir);
        CaptureSequence();
    }

    // A 10-second take: empty board, threads wind on and the portrait emerges, then the model
    // turns and the threads re-tension; the camera slowly orbits the board the whole time.
    // JPG frames go to SequenceDir; encode them into a clip outside Unity (ffmpeg works).
    public static string SequenceDir => Path.Combine(CaptureDir, "Sequence");

    public static void CaptureSequence()
    {
        if (Directory.Exists(SequenceDir)) Directory.Delete(SequenceDir, true);
        Directory.CreateDirectory(SequenceDir);
        if (SceneManager.GetActiveScene().path != ScenePath) EditorSceneManager.OpenScene(ScenePath);
        var sa = Object.FindFirstObjectByType<StringArt>();
        var subject = GameObject.Find("Subject");
        var mainCam = Camera.main;
        sa.Setup();

        const int fps = 30, total = 10 * fps, turnStart = 4 * fps;
        var frame = new RenderTexture(1080, 1080, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
        var tmp = new RenderTexture(1080, 1080, 0, RenderTextureFormat.ARGB32);
        var tex = new Texture2D(1080, 1080, TextureFormat.RGB24, false);
        for (int f = 0; f < total; f++)
        {
            float t = f / (float)(total - 1);
            float turn = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((f - turnStart) / (float)(total - turnStart)));
            subject.transform.rotation = Quaternion.Euler(0f, Mathf.Lerp(0f, 60f, turn), 0f);

            float yaw = Mathf.Lerp(-14f, 14f, t) * Mathf.Deg2Rad;
            float dist = Mathf.Lerp(1.8f, 1.7f, t);
            mainCam.transform.position = new Vector3(Mathf.Sin(yaw) * dist, Mathf.Lerp(0.08f, -0.05f, t), -Mathf.Cos(yaw) * dist);
            mainCam.transform.LookAt(Vector3.zero);

            // For the first four seconds the portrait emerges: single threads at first, full strength by the end
            // Only the most valuable threads (curls, shadows) come in first, midtones later
            float build = Mathf.Clamp01(f / (float)turnStart);
            sa.addProbability = Mathf.Lerp(0.02f, 0.08f, build);
            sa.addThresholdBoost = 0.45f * (1f - build) * (1f - build);

            RenderCamera(sa.subjectCamera, sa.sceneRT);
            sa.Tick(1f / fps);
            RenderCamera(mainCam, frame);

            Graphics.Blit(frame, tmp);
            var prev = RenderTexture.active;
            RenderTexture.active = tmp;
            tex.ReadPixels(new Rect(0, 0, 1080, 1080), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            File.WriteAllBytes(Path.Combine(SequenceDir, $"f{f:D4}.jpg"), tex.EncodeToJPG(95));
        }
        Object.DestroyImmediate(tex);
        tmp.Release();
        frame.Release();
        sa.Teardown();
        Debug.Log($"[Threadline] sequence done: {total} frames in {SequenceDir}");
    }

    // Check that skips GPU thread rendering: read back the target and chord state, draw the active chords on the CPU
    // with the same nail geometry as the scoring pass. If the head shows up here but not in the frame,
    // the bug is in thread rendering.
    static void CpuCheck(StringArt sa)
    {
        var tReq = AsyncGPUReadback.Request(sa.targetRT);
        tReq.WaitForCompletion();
        var target = tReq.GetData<float>().ToArray();
        SaveArray(target, sa.targetSize, sa.targetSize, Path.Combine(CaptureDir, "dbg_target_readback.png"), 1f);

        var sReq = AsyncGPUReadback.Request(sa.stateRT);
        sReq.WaitForCompletion();
        var state = sReq.GetData<float>().ToArray();
        int n = sa.nails;

        var on = new System.Collections.Generic.List<int>();
        for (int i = 0; i < state.Length; i++) if (state[i] > 0.5f) on.Add(i);
        Debug.Log($"[Threadline] cpu: chords on {on.Count}");

        const int S = 512;
        var img = new float[S * S];
        for (int i = 0; i < img.Length; i++) img[i] = 1f;
        foreach (int id in on)
        {
            int a = id % n, b = id / n;
            Vector2 pa = NailUV(a, n) * (S - 1), pb = NailUV(b, n) * (S - 1);
            int steps = Mathf.CeilToInt(Vector2.Distance(pa, pb));
            for (int s = 0; s <= steps; s++)
            {
                var p = Vector2.Lerp(pa, pb, s / (float)steps);
                int x = Mathf.Clamp(Mathf.RoundToInt(p.x), 0, S - 1), y = Mathf.Clamp(Mathf.RoundToInt(p.y), 0, S - 1);
                img[y * S + x] *= 0.75f;
            }
        }
        SaveArray(img, S, S, Path.Combine(CaptureDir, "dbg_cpu_chords.png"), 1f);
    }

    // Reference: classic greedy search with one continuous thread from nail to nail on the same target.
    // Too slow for every frame, but shows what quality is reachable with this many nails.
    static void CpuGreedy(float[] target, int n, int minGap, int lines, float alpha, string path)
    {
        const int R = 256;
        var residual = (float[])target.Clone();
        var cache = new System.Collections.Generic.Dictionary<int, int[]>();
        int[] Pixels(int a, int b)
        {
            int key = Mathf.Min(a, b) * n + Mathf.Max(a, b);
            if (cache.TryGetValue(key, out var px)) return px;
            Vector2 pa = NailUV(a, n) * (R - 1), pb = NailUV(b, n) * (R - 1);
            int steps = Mathf.CeilToInt(Vector2.Distance(pa, pb));
            var list = new System.Collections.Generic.List<int>(steps + 1);
            int last = -1;
            for (int s = 0; s <= steps; s++)
            {
                var p = Vector2.Lerp(pa, pb, s / (float)steps);
                int id = Mathf.Clamp(Mathf.RoundToInt(p.y), 0, R - 1) * R + Mathf.Clamp(Mathf.RoundToInt(p.x), 0, R - 1);
                if (id != last) list.Add(id);
                last = id;
            }
            px = list.ToArray();
            cache[key] = px;
            return px;
        }

        var sequence = new System.Collections.Generic.List<int> { 0 };
        int cur = 0, prev = -1;
        for (int it = 0; it < lines; it++)
        {
            float best = -1f;
            int bestB = -1;
            for (int b = 0; b < n; b++)
            {
                int gap = Mathf.Abs(b - cur);
                gap = Mathf.Min(gap, n - gap);
                if (gap < minGap || b == prev) continue;
                var px = Pixels(cur, b);
                // Same as StringArtGenerator and similar tools: mean darkness of the residual, residual never drops below zero,
                // exactly as many threads as requested
                float sum = 0f;
                foreach (int id in px) sum += residual[id];
                float score = sum / px.Length;
                if (score > best) { best = score; bestB = b; }
            }
            if (bestB < 0) break;
            foreach (int id in Pixels(cur, bestB)) residual[id] = Mathf.Max(0f, residual[id] - alpha);
            prev = cur;
            cur = bestB;
            sequence.Add(cur);
        }

        const int S = 1024;
        var img = new float[S * S];
        for (int i = 0; i < img.Length; i++) img[i] = 1f;
        for (int k = 1; k < sequence.Count; k++)
        {
            Vector2 pa = NailUV(sequence[k - 1], n) * (S - 1), pb = NailUV(sequence[k], n) * (S - 1);
            int steps = Mathf.CeilToInt(Vector2.Distance(pa, pb));
            for (int s = 0; s <= steps; s++)
            {
                var p = Vector2.Lerp(pa, pb, s / (float)steps);
                int x = Mathf.Clamp(Mathf.RoundToInt(p.x), 0, S - 1), y = Mathf.Clamp(Mathf.RoundToInt(p.y), 0, S - 1);
                img[y * S + x] *= 0.85f;
            }
        }
        SaveArray(img, S, S, path, 1f);
        Debug.Log($"[Threadline] cpu greedy: {sequence.Count - 1} lines");
    }

    static Vector2 NailUV(int k, int n)
    {
        float t = k * Mathf.PI * 2f / n;
        return new Vector2(0.5f + 0.5f * Mathf.Cos(t), 0.5f + 0.5f * Mathf.Sin(t));
    }

    // Row 0 of the array is written as the bottom row of the image
    static void SaveArray(float[] data, int w, int h, string path, float scale)
    {
        var px = new Color[w * h];
        for (int i = 0; i < px.Length; i++)
        {
            float v = Mathf.Clamp01(data[i] * scale);
            px[i] = new Color(v, v, v, 1f);
        }
        var png = new Texture2D(w, h, TextureFormat.RGBA32, false);
        png.SetPixels(px);
        File.WriteAllBytes(path, png.EncodeToPNG());
        Object.DestroyImmediate(png);
    }

    static void RenderCamera(Camera cam, RenderTexture rt)
    {
        var req = new RenderPipeline.StandardRequest { destination = rt };
        if (RenderPipeline.SupportsRenderRequest(cam, req))
        {
            RenderPipeline.SubmitRenderRequest(cam, req);
            return;
        }
        var prev = cam.targetTexture;
        cam.targetTexture = rt;
        cam.Render();
        cam.targetTexture = prev;
    }

    static void SaveColor(RenderTexture rt, string path)
    {
        var tmp = RenderTexture.GetTemporary(rt.width, rt.height, 0, RenderTextureFormat.ARGB32);
        Graphics.Blit(rt, tmp);
        var prev = RenderTexture.active;
        RenderTexture.active = tmp;
        var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
        tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        tex.Apply();
        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(tmp);
        File.WriteAllBytes(path, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);
    }

    static void SaveSingle(RenderTexture rt, string path, float scale)
    {
        var req = AsyncGPUReadback.Request(rt);
        req.WaitForCompletion();
        SaveArray(req.GetData<float>().ToArray(), rt.width, rt.height, path, scale);
    }

    static void ConfigureModelImport(string path)
    {
        if (!(AssetImporter.GetAtPath(path) is ModelImporter mi)) return;
        bool changed = false;
        if (!Mathf.Approximately(mi.globalScale, ModelImportScale)) { mi.globalScale = ModelImportScale; changed = true; }
        if (mi.indexFormat != ModelImporterIndexFormat.UInt32) { mi.indexFormat = ModelImporterIndexFormat.UInt32; changed = true; }
        if (mi.importNormals != ModelImporterNormals.Calculate) { mi.importNormals = ModelImporterNormals.Calculate; mi.normalSmoothingAngle = 60f; changed = true; }
        if (mi.materialImportMode != ModelImporterMaterialImportMode.None) { mi.materialImportMode = ModelImporterMaterialImportMode.None; changed = true; }
        if (changed) mi.SaveAndReimport();
    }

    static void BuildPlaceholderBust(Transform parent)
    {
        void Part(PrimitiveType type, string name, Vector3 pos, Vector3 scale, Vector3 euler)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = scale;
            go.transform.localEulerAngles = euler;
        }
        Part(PrimitiveType.Sphere, "Head", new Vector3(0f, 0.46f, 0f), new Vector3(0.20f, 0.26f, 0.23f), Vector3.zero);
        Part(PrimitiveType.Cube, "Nose", new Vector3(0f, 0.45f, -0.115f), new Vector3(0.035f, 0.07f, 0.05f), new Vector3(-12f, 0f, 45f));
        Part(PrimitiveType.Sphere, "BrowL", new Vector3(-0.045f, 0.50f, -0.09f), new Vector3(0.06f, 0.03f, 0.05f), Vector3.zero);
        Part(PrimitiveType.Sphere, "BrowR", new Vector3(0.045f, 0.50f, -0.09f), new Vector3(0.06f, 0.03f, 0.05f), Vector3.zero);
        Part(PrimitiveType.Cylinder, "Neck", new Vector3(0f, 0.30f, 0.01f), new Vector3(0.10f, 0.08f, 0.10f), Vector3.zero);
        Part(PrimitiveType.Capsule, "Shoulders", new Vector3(0f, 0.17f, 0.02f), new Vector3(0.18f, 0.25f, 0.16f), new Vector3(0f, 0f, 90f));
    }

    static Bounds RendererBounds(GameObject root)
    {
        var rs = root.GetComponentsInChildren<Renderer>();
        var b = rs[0].bounds;
        foreach (var r in rs) b.Encapsulate(r.bounds);
        return b;
    }

    static void SetLayer(GameObject go, int layer)
    {
        foreach (var t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
    }

    static Material MakeLit(string name, Color color, float metallic, float smoothness)
    {
        string path = $"{Root}/Materials/{name}.mat";
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        var lit = Shader.Find("Universal Render Pipeline/Lit");
        if (!mat)
        {
            mat = new Material(lit);
            AssetDatabase.CreateAsset(mat, path);
        }
        mat.shader = lit;
        mat.SetColor("_BaseColor", color);
        mat.SetFloat("_Metallic", metallic);
        mat.SetFloat("_Smoothness", smoothness);
        EditorUtility.SetDirty(mat);
        return mat;
    }

    static GameObject Primitive(PrimitiveType type, string name, Transform parent, Material mat)
    {
        var go = GameObject.CreatePrimitive(type);
        go.name = name;
        Object.DestroyImmediate(go.GetComponent<Collider>());
        if (parent) go.transform.SetParent(parent, false);
        go.GetComponent<Renderer>().sharedMaterial = mat;
        return go;
    }

    // Cream board with barely visible fibers: without them it looks like plastic
    static Texture2D MakePaperTexture()
    {
        string path = $"{Root}/Textures/BoardPaper.png";
        var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        if (existing) return existing;
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        const int S = 1024;
        var tex = new Texture2D(S, S, TextureFormat.RGB24, false);
        var px = new Color[S * S];
        for (int y = 0; y < S; y++)
        for (int x = 0; x < S; x++)
        {
            float u = x / (float)S, v = y / (float)S;
            float n = 0f, amp = 0.5f, f = 6f;
            for (int o = 0; o < 5; o++)
            {
                n += (Mathf.PerlinNoise(u * f + o * 13.1f, v * f + o * 7.7f) - 0.5f) * amp;
                f *= 2.1f; amp *= 0.5f;
            }
            // Long fibers along x
            float fiber = (Mathf.PerlinNoise(u * 3f, v * 260f) - 0.5f) * 0.6f;
            float k = 1f + n * 0.06f + fiber * 0.025f;
            px[y * S + x] = new Color(k, k * 0.995f, k * 0.985f);
        }
        tex.SetPixels(px);
        File.WriteAllBytes(path, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);
        AssetDatabase.ImportAsset(path);
        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }

    // Depth of field (the film render drives its values), neutral tonemapping, light vignette and grain against banding
    static VolumeProfile MakePostProfile()
    {
        string path = $"{Root}/Settings/FilmPost.asset";
        var old = AssetDatabase.LoadAssetAtPath<VolumeProfile>(path);
        if (old) return old;
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        var profile = ScriptableObject.CreateInstance<VolumeProfile>();
        AssetDatabase.CreateAsset(profile, path);

        var dof = profile.Add<DepthOfField>(true);
        dof.mode.Override(DepthOfFieldMode.Bokeh);
        dof.focusDistance.Override(4f);
        dof.focalLength.Override(35f);
        dof.aperture.Override(8f);
        var tone = profile.Add<Tonemapping>(true);
        tone.mode.Override(TonemappingMode.Neutral);
        var vig = profile.Add<Vignette>(true);
        vig.intensity.Override(0.18f);
        vig.smoothness.Override(0.5f);
        var grain = profile.Add<FilmGrain>(true);
        grain.type.Override(FilmGrainLookup.Thin1);
        grain.intensity.Override(0.12f);
        foreach (var c in profile.components) AssetDatabase.AddObjectToAsset(c, profile);
        EditorUtility.SetDirty(profile);
        AssetDatabase.SaveAssets();
        return profile;
    }

    static Material MakeUnlit(string name, Color color)
    {
        string path = $"{Root}/Materials/{name}.mat";
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        var shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (!mat)
        {
            mat = new Material(shader);
            AssetDatabase.CreateAsset(mat, path);
        }
        mat.shader = shader;
        mat.SetColor("_BaseColor", color);
        EditorUtility.SetDirty(mat);
        return mat;
    }

    static Light MakeLight(string name, Vector3 euler, float intensity)
    {
        var light = new GameObject(name).AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = intensity;
        light.shadows = LightShadows.Soft;
        light.transform.eulerAngles = euler;
        return light;
    }

    static void SetMsaa(int samples)
    {
        for (int i = 0; i < QualitySettings.count; i++)
            if (QualitySettings.GetRenderPipelineAssetAt(i) is UniversalRenderPipelineAsset q)
            {
                q.msaaSampleCount = samples;
                EditorUtility.SetDirty(q);
            }
        if (GraphicsSettings.defaultRenderPipeline is UniversalRenderPipelineAsset d)
        {
            d.msaaSampleCount = samples;
            EditorUtility.SetDirty(d);
        }
        AssetDatabase.SaveAssets();
    }

    static int EnsureLayer(string name)
    {
        int existing = LayerMask.NameToLayer(name);
        if (existing >= 0) return existing;
        var tagManager = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
        var layers = tagManager.FindProperty("layers");
        for (int i = 8; i < layers.arraySize; i++)
        {
            var sp = layers.GetArrayElementAtIndex(i);
            if (!string.IsNullOrEmpty(sp.stringValue)) continue;
            sp.stringValue = name;
            tagManager.ApplyModifiedProperties();
            return i;
        }
        throw new Exception("No free layer for " + name);
    }
}
