using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// Real-time string art portrait. A camera renders the model, and a set of tensioned chords lives on the GPU:
// each step computes the residual (target minus thread coverage) along every nail pair, adds threads that help
// and removes extra ones (local least-squares search). The state is not reset between
// frames, so the image refines over time and re-tensions smoothly when the model moves.
// Visible threads wind on and off with an animation.
public class StringArt : MonoBehaviour
{
    // Tooltips are kept short and plain: the inspector appears on camera in the demo video
    [Header("Strings")]
    [Tooltip("How many threads are tensioned right now; updated twice a second")]
    public int visibleThreads;
    [Tooltip("Number of nails around the circle; changing it rebuilds everything")]
    [Range(64, 512)] public int nails = 320;
    [Tooltip("How much darkness one thread removes from the residual. Lower means more threads")]
    [Range(0.01f, 0.12f)] public float lineDarkness = 0.025f;
    [Tooltip("Thread density multiplier")]
    [Range(0f, 3f)] public float threadOpacity = 1f;
    [Tooltip("Physical thread width, meters")]
    [Range(0.0001f, 0.003f)] public float threadWidth = 0.00035f;
    [Tooltip("Threads never get thinner than this many screen pixels: thinner causes stair-stepping and moire")]
    [Range(0.5f, 4f)] public float threadMinPixels = 1.5f;
    [Tooltip("Thread \"ink\" per unit length: sets the portrait tone from a distance")]
    [Range(0.00002f, 0.0004f)] public float threadInk = 0.00016f;
    [Range(0f, 1f)] public float threadSheen = 0.3f;
    public Color threadColor = new Color(0.04f, 0.04f, 0.05f);
    [Tooltip("Seconds to wind one thread")]
    [Range(0.05f, 2f)] public float windTime = 0.35f;
    public bool threadShadows = true;

    [Header("Image")]
    [Tooltip("Darkness = (1 - luminance - blackPoint) * contrast. Too much contrast turns the model into a solid silhouette")]
    [Range(0.5f, 2.5f)] public float contrast = 1.15f;
    [Tooltip("Unsharp mask strength (local contrast)")]
    [Range(0f, 4f)] public float sharpen = 1.5f;
    [Range(0f, 2f)] public float edgeBoost = 0.6f;
    [Range(0f, 0.5f)] public float blackPoint = 0.05f;
    [Range(0.5f, 2f)] public float gamma = 1f;
    [Tooltip("Blur radius for the unsharp mask, in target texels")]
    [Range(1f, 8f)] public float sharpenRadius = 4f;
    public bool flipY;

    [Header("Solver")]
    [Tooltip("Search steps per frame")]
    [Range(1, 8)] public int iterationsPerFrame = 4;
    [Tooltip("Chords between nails closer than this are skipped; changing it rebuilds everything")]
    [Range(4, 40)] public int minGap = 16;
    [Tooltip("Margin that keeps threads from flickering at the break-even point")]
    [Range(0f, 0.05f)] public float hysteresis = 0.006f;
    [Tooltip("Chance that a useful chord turns on in one step: thins out bundles of nearly identical chords")]
    [Range(0.001f, 1f)] public float addProbability = 0.08f;
    [Tooltip("Extra threshold for new threads only. A high value at the start makes the portrait emerge from the darkest areas first")]
    [Range(0f, 0.6f)] public float addThresholdBoost = 0f;
    [Range(0.01f, 1f)] public float removeProbability = 0.12f;
    [Tooltip("Thread width in target texels when computing coverage")]
    public float coverageWidth = 1f;

    [Header("Scene")]
    public Camera subjectCamera;
    [Tooltip("Resolution of the target and coverage textures")]
    public int targetSize = 512;
    public float radius = 0.5f;
    public Material boardMaterial;
    public Material nailMaterial;

    // Textures are public for debugging and capture, not needed in the inspector
    [HideInInspector] public RenderTexture sceneRT;
    [HideInInspector] public RenderTexture targetRT;
    [HideInInspector] public RenderTexture coverageRT;
    [HideInInspector] public RenderTexture scoreRT;
    [HideInInspector] public RenderTexture stateRT;
    [HideInInspector] public RenderTexture visRT;

    RenderTexture stateBack, visBack;
    Material targetMat, scoreMat, selectMat, coverageMat, visMat, chordMat, shadowMat;
    Mesh chordMesh;
    CommandBuffer cmd;
    GameObject built;
    int step;
    float nextCount;
    int builtNails, builtMinGap;

    void OnEnable() => Setup();
    void OnDisable() => Teardown();

    void LateUpdate()
    {
#if ENABLE_INPUT_SYSTEM
        // R in the Game view: clear the board and let the portrait build up again
        var kb = UnityEngine.InputSystem.Keyboard.current;
        if (kb != null && kb.rKey.wasPressedThisFrame) RebuildSlowly();
#endif
        Tick(Time.deltaTime);
        // Thread counter is for the inspector only, every half second
        if (Time.unscaledTime >= nextCount)
        {
            nextCount = Time.unscaledTime + 0.5f;
            AsyncGPUReadback.Request(stateRT, 0, req =>
            {
                if (this == null || req.hasError) return;
                int n = 0;
                foreach (float v in req.GetData<float>()) if (v > 0.5f) n++;
                visibleThreads = n;
            });
        }
    }

    public void Setup()
    {
        targetMat = new Material(Shader.Find("Threadline/Target"));
        scoreMat = new Material(Shader.Find("Threadline/Score"));
        selectMat = new Material(Shader.Find("Threadline/Select"));
        coverageMat = new Material(Shader.Find("Threadline/Coverage"));
        visMat = new Material(Shader.Find("Threadline/Vis"));
        chordMat = new Material(Shader.Find("Threadline/Chords"));
        shadowMat = new Material(chordMat);
        shadowMat.SetFloat("_ShadowMode", 1f);
        shadowMat.renderQueue = 3000;
        chordMat.renderQueue = 3001;
        cmd = new CommandBuffer { name = "Threadline" };

        sceneRT = new RenderTexture(targetSize, targetSize, 24, RenderTextureFormat.ARGB32) { name = "ThreadScene" };
        targetRT = NewFloatRT("ThreadTarget", targetSize, FilterMode.Bilinear);
        coverageRT = NewFloatRT("ThreadCoverage", targetSize, FilterMode.Bilinear);
        scoreRT = NewFloatRT("ThreadScore", nails, FilterMode.Point);
        stateRT = NewFloatRT("ThreadState", nails, FilterMode.Point);
        stateBack = NewFloatRT("ThreadStateBack", nails, FilterMode.Point);
        visRT = NewFloatRT("ThreadVis", nails, FilterMode.Point);
        visBack = NewFloatRT("ThreadVisBack", nails, FilterMode.Point);
        subjectCamera.targetTexture = sceneRT;

        built = new GameObject("Threadline (runtime)") { hideFlags = HideFlags.DontSave };
        built.transform.SetParent(transform, false);
        BuildBoard();
        BuildChords();
        ResetThreads();
        builtNails = nails;
        builtMinGap = minGap;
    }

    // Remove all threads: the portrait starts tensioning from an empty board
    [ContextMenu("Reset Threads")]
    public void ResetThreads()
    {
        var c = new CommandBuffer();
        foreach (var rt in new[] { stateRT, stateBack, visRT, visBack })
        {
            c.SetRenderTarget(rt);
            c.ClearRenderTarget(false, true, Color.clear);
        }
        Graphics.ExecuteCommandBuffer(c);
        c.Release();
    }

    float rebuildStart = -1f;
    const float RebuildSeconds = 3.2f;

    // Remove all threads and bring them back slowly: the most useful chords first (shadows, curls), then
    // the half tones. Inspector values stay as they are, the ramp is applied on top of them
    [ContextMenu("Rebuild Slowly")]
    public void RebuildSlowly()
    {
        ResetThreads();
        rebuildStart = Time.unscaledTime;
    }

    static RenderTexture NewFloatRT(string name, int size, FilterMode filter)
    {
        var rt = new RenderTexture(size, size, 0, RenderTextureFormat.RFloat)
        {
            name = name, filterMode = filter, wrapMode = TextureWrapMode.Clamp
        };
        rt.Create();
        return rt;
    }

    public void Teardown()
    {
        if (subjectCamera) subjectCamera.targetTexture = null;
        cmd?.Release();
        cmd = null;
        DestroyAny(built);
        DestroyAny(chordMesh);
        foreach (var rt in new[] { sceneRT, targetRT, coverageRT, scoreRT, stateRT, stateBack, visRT, visBack })
            if (rt) rt.Release();
        foreach (var m in new[] { targetMat, scoreMat, selectMat, coverageMat, visMat, chordMat, shadowMat }) DestroyAny(m);
    }

    static void DestroyAny(Object o)
    {
        if (!o) return;
        if (Application.isPlaying) Destroy(o);
        else DestroyImmediate(o);
    }

    void BuildBoard()
    {
        // Smooth disc: the stock cylinder has only 20 sides, so the board edge would look faceted
        var board = new GameObject("Board");
        board.transform.SetParent(built.transform, false);
        board.transform.localPosition = new Vector3(0f, 0f, 0.001f);
        board.AddComponent<MeshFilter>().sharedMesh = BuildDisc(radius * 1.07f, 0.01f, 128);
        board.AddComponent<MeshRenderer>().sharedMaterial = boardMaterial;

        // Nail: a thin shank plus a head, otherwise it looks like a plastic peg in macro shots
        for (int k = 0; k < nails; k++)
        {
            float t = k * Mathf.PI * 2f / nails;
            var at = new Vector3(Mathf.Cos(t) * radius, Mathf.Sin(t) * radius, 0f);
            AddNailPart("Nail", at + new Vector3(0f, 0f, -0.007f), new Vector3(0.0028f, 0.0075f, 0.0028f));
            AddNailPart("Nail Head", at + new Vector3(0f, 0f, -0.0145f), new Vector3(0.0058f, 0.0006f, 0.0058f));
        }
    }

    void AddNailPart(string name, Vector3 pos, Vector3 scale)
    {
        var part = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        part.name = name;
        DestroyAny(part.GetComponent<Collider>());
        part.transform.SetParent(built.transform, false);
        part.transform.localPosition = pos;
        part.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
        part.transform.localScale = scale;
        if (nailMaterial) part.GetComponent<Renderer>().sharedMaterial = nailMaterial;
    }

    // Disc in the XY plane: the front faces -Z, thickness goes into +Z
    static Mesh BuildDisc(float r, float thickness, int segments)
    {
        var verts = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var tris = new List<int>();
        verts.Add(Vector3.zero);
        normals.Add(Vector3.back);
        uvs.Add(new Vector2(0.5f, 0.5f));
        for (int i = 0; i < segments; i++)
        {
            float t = i * Mathf.PI * 2f / segments;
            verts.Add(new Vector3(Mathf.Cos(t) * r, Mathf.Sin(t) * r, 0f));
            normals.Add(Vector3.back);
            uvs.Add(new Vector2(0.5f + 0.5f * Mathf.Cos(t), 0.5f + 0.5f * Mathf.Sin(t)));
        }
        for (int i = 0; i < segments; i++)
        {
            tris.Add(0); tris.Add(1 + (i + 1) % segments); tris.Add(1 + i);
        }
        int rim = verts.Count;
        for (int i = 0; i < segments; i++)
        {
            float t = i * Mathf.PI * 2f / segments;
            var n = new Vector3(Mathf.Cos(t), Mathf.Sin(t), 0f);
            verts.Add(n * r); normals.Add(n); uvs.Add(new Vector2(i / (float)segments, 0f));
            verts.Add(n * r + Vector3.forward * thickness); normals.Add(n); uvs.Add(new Vector2(i / (float)segments, 0.02f));
        }
        for (int i = 0; i < segments; i++)
        {
            int a = rim + i * 2, b = rim + ((i + 1) % segments) * 2;
            tris.Add(a); tris.Add(b); tris.Add(a + 1);
            tris.Add(b); tris.Add(b + 1); tris.Add(a + 1);
        }
        var mesh = new Mesh { name = "Board" };
        mesh.SetVertices(verts);
        mesh.SetNormals(normals);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateTangents();
        return mesh;
    }

    // One quad per nail pair. Positions are computed in the vertex shader, UV0 holds the nail indices,
    // UV1 holds the chord end (0 or 1) and the thread side (-1 or 1).
    void BuildChords()
    {
        var verts = new List<Vector3>();
        var ab = new List<Vector2>();
        var ends = new List<Vector2>();
        var tris = new List<int>();
        for (int a = 0; a < nails; a++)
        for (int b = a + 1; b < nails; b++)
        {
            int gap = Mathf.Min(b - a, nails - (b - a));
            if (gap < minGap) continue;
            int v = verts.Count;
            for (int end = 0; end < 2; end++)
            for (int side = -1; side <= 1; side += 2)
            {
                verts.Add(Vector3.zero);
                ab.Add(new Vector2(a, b));
                ends.Add(new Vector2(end, side));
            }
            tris.Add(v); tris.Add(v + 1); tris.Add(v + 2);
            tris.Add(v + 1); tris.Add(v + 3); tris.Add(v + 2);
        }

        chordMesh = new Mesh { name = "Chords", indexFormat = IndexFormat.UInt32 };
        chordMesh.SetVertices(verts);
        chordMesh.SetUVs(0, ab);
        chordMesh.SetUVs(1, ends);
        chordMesh.SetTriangles(tris, 0);
        chordMesh.bounds = new Bounds(Vector3.zero, new Vector3(radius * 2.5f, radius * 2.5f, 0.1f));

        AddChordRenderer("Chords", chordMat);
        AddChordRenderer("Chord Shadows", shadowMat);
    }

    void AddChordRenderer(string name, Material mat)
    {
        var go = new GameObject(name);
        go.transform.SetParent(built.transform, false);
        go.AddComponent<MeshFilter>().sharedMesh = chordMesh;
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = mat;
        mr.shadowCastingMode = ShadowCastingMode.Off;
    }

    // Target from the model snapshot, then a few search steps, then one winding animation step.
    // The snapshot comes from sceneRT (the subject camera renders into it on its own). Anything that changes between
    // draws inside the buffer is set globally through the buffer itself, otherwise every draw sees the last value.
    public void Tick(float dt)
    {
        // Nail count and min gap define the chord grid: when either changes, rebuild everything
        if (nails != builtNails || minGap != builtMinGap)
        {
            Teardown();
            Setup();
        }

        targetMat.SetTexture("_SceneTex", sceneRT);
        targetMat.SetVector("_Texel", new Vector4(1f / targetSize, 1f / targetSize, targetSize, targetSize));
        targetMat.SetFloat("_Contrast", contrast);
        targetMat.SetFloat("_Black", blackPoint);
        targetMat.SetFloat("_Gamma", gamma);
        targetMat.SetFloat("_EdgeBoost", edgeBoost);
        targetMat.SetFloat("_FlipY", flipY ? 1f : 0f);
        targetMat.SetFloat("_Sharpen", sharpen);
        targetMat.SetFloat("_SharpenRadius", sharpenRadius);

        cmd.Clear();
        cmd.SetGlobalTexture("_TargetTex", targetRT);
        cmd.SetGlobalTexture("_CoverageTex", coverageRT);
        cmd.SetGlobalTexture("_ScoreTex", scoreRT);
        cmd.SetGlobalFloat("_Nails", nails);
        cmd.SetGlobalFloat("_MinGap", minGap);
        cmd.SetGlobalFloat("_LineDarkness", lineDarkness);
        cmd.SetGlobalFloat("_Hysteresis", hysteresis);
        float addProb = addProbability, addBoost = addThresholdBoost;
        if (rebuildStart >= 0f)
        {
            float s = Mathf.Clamp01((Time.unscaledTime - rebuildStart) / RebuildSeconds);
            addProb = Mathf.Lerp(0.004f, addProbability, s * s);
            addBoost += 0.45f * (1f - s) * (1f - s);
            if (s >= 1f) rebuildStart = -1f;
        }
        cmd.SetGlobalFloat("_AddProb", addProb);
        cmd.SetGlobalFloat("_AddBoost", addBoost);
        cmd.SetGlobalFloat("_RemoveProb", removeProbability);
        cmd.SetGlobalFloat("_CoverageWidth", coverageWidth / targetSize);
        cmd.SetGlobalFloat("_UVFlip", SystemInfo.graphicsUVStartsAtTop ? -1f : 1f);

        cmd.SetRenderTarget(targetRT);
        cmd.DrawProcedural(Matrix4x4.identity, targetMat, 0, MeshTopology.Triangles, 3);

        for (int i = 0; i < iterationsPerFrame; i++)
        {
            cmd.SetGlobalTexture("_StateTex", stateRT);
            cmd.SetRenderTarget(coverageRT);
            cmd.ClearRenderTarget(false, true, Color.clear);
            cmd.DrawMesh(chordMesh, Matrix4x4.identity, coverageMat, 0, 0);

            cmd.SetRenderTarget(scoreRT);
            cmd.DrawProcedural(Matrix4x4.identity, scoreMat, 0, MeshTopology.Triangles, 3);

            cmd.SetGlobalFloat("_Seed", (step++ % 100000) + 0.5f);
            cmd.SetRenderTarget(stateBack);
            cmd.DrawProcedural(Matrix4x4.identity, selectMat, 0, MeshTopology.Triangles, 3);
            (stateRT, stateBack) = (stateBack, stateRT);
        }

        cmd.SetGlobalTexture("_StateTex", stateRT);
        cmd.SetGlobalTexture("_VisTex", visRT);
        cmd.SetGlobalFloat("_VisStep", windTime > 0f ? dt / windTime : 1f);
        cmd.SetRenderTarget(visBack);
        cmd.DrawProcedural(Matrix4x4.identity, visMat, 0, MeshTopology.Triangles, 3);
        (visRT, visBack) = (visBack, visRT);

        Graphics.ExecuteCommandBuffer(cmd);

        foreach (var m in new[] { chordMat, shadowMat })
        {
            m.SetTexture("_VisTex", visRT);
            m.SetFloat("_Nails", nails);
            m.SetFloat("_Radius", radius);
            m.SetFloat("_Width", threadWidth);
            m.SetFloat("_MinPixels", threadMinPixels);
            m.SetFloat("_Ink", threadInk);
            m.SetFloat("_Sheen", threadSheen);
            m.SetFloat("_Opacity", threadOpacity);
            m.SetColor("_ThreadColor", threadColor);
        }
        shadowMat.SetFloat("_Opacity", threadShadows ? threadOpacity : 0f);
    }
}
