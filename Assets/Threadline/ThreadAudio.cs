using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

// Sound of the threads themselves. The chord state is read back from the GPU every frame and compared
// with the previous frame: a thread that went taut plays a pull and, once it has wound across, a tick on
// the far nail; a thread taken off plays a soft release. Long chords sound lower, pan follows where the
// chord lies on the board. When hundreds of threads change at once a rustle loop swells underneath.
[RequireComponent(typeof(StringArt))]
public class ThreadAudio : MonoBehaviour
{
    [Header("Clips")]
    public AudioClip[] pullClips;
    public AudioClip[] nailClips;
    public AudioClip[] releaseClips;
    [Tooltip("Loop under mass changes; its volume follows how many threads change per second")]
    public AudioClip rustleLoop;

    [Header("Mix")]
    [Range(0f, 1f)] public float volume = 1f;
    [Range(0f, 1f)] public float pullVolume = 0.7f;
    [Range(0f, 1f)] public float nailVolume = 0.55f;
    [Range(0f, 1f)] public float releaseVolume = 0.3f;
    [Range(0f, 1f)] public float rustleVolume = 0.2f;
    [Tooltip("Threads per second at which the rustle reaches about two thirds of its volume")]
    [Range(50f, 5000f)] public float rustleRate = 800f;
    [Tooltip("At most this many one-shots start per second; the rest only feed the rustle")]
    [Range(4, 120)] public int maxPerSecond = 36;
    [Range(4, 64)] public int voices = 24;
    [Tooltip("More changes than this in one frame count as a burst: ticks instead of long pulls")]
    [Range(1, 200)] public int burstThreshold = 24;
    [Tooltip("Pitch of the longest and of the shortest chord")]
    public Vector2 pitchRange = new Vector2(0.85f, 1.25f);
    [Range(0f, 0.1f)] public float pitchJitter = 0.04f;
    [Range(0f, 1f)] public float stereoWidth = 0.6f;

    [Header("Live")]
    [Tooltip("Threads pulled on during the last second")]
    public int addedPerSecond;
    [Tooltip("Threads taken off during the last second")]
    public int removedPerSecond;

    StringArt art;
    AudioSource[] pool;
    AudioSource rustle;
    int nextVoice;
    float[] prev;
    int prevNails;
    int pending;
    float tokens;
    float rustleLevel;
    float rateAdded, rateRemoved;

    // Reservoir samples of this readback's events, so a frame with thousands of changes stays cheap
    const int Reservoir = 12;
    readonly Vector2Int[] addedPick = new Vector2Int[Reservoir];
    readonly Vector2Int[] removedPick = new Vector2Int[Reservoir];

    void OnEnable()
    {
        art = GetComponent<StringArt>();
        if (!FindAnyObjectByType<AudioListener>() && Camera.main) Camera.main.gameObject.AddComponent<AudioListener>();

        pool = new AudioSource[voices];
        for (int i = 0; i < voices; i++) pool[i] = NewSource("Thread Voice " + i);
        rustle = NewSource("Thread Rustle");
        rustle.clip = rustleLoop;
        rustle.loop = true;
        rustle.volume = 0f;
        if (rustleLoop) rustle.Play();
        prev = null;
        pending = 0;
    }

    void OnDisable()
    {
        if (pool != null) foreach (var s in pool) if (s) Destroy(s.gameObject);
        if (rustle) Destroy(rustle.gameObject);
        pool = null;
    }

    AudioSource NewSource(string name)
    {
        var go = new GameObject(name) { hideFlags = HideFlags.DontSave | HideFlags.HideInHierarchy };
        go.transform.SetParent(transform, false);
        var s = go.AddComponent<AudioSource>();
        s.playOnAwake = false;
        s.spatialBlend = 0f;
        return s;
    }

    void LateUpdate()
    {
        float dt = Time.unscaledDeltaTime;
        tokens = Mathf.Min(tokens + maxPerSecond * dt, maxPerSecond * 0.25f);

        // StringArt has already run this frame's solver steps; ask for the newest state
        if (art.stateRT && pending < 3)
        {
            pending++;
            int n = art.nails;
            AsyncGPUReadback.Request(art.stateRT, 0, req => OnState(req, n));
        }

        // Rates for the inspector and the rustle, smoothed over about a second
        float k = 1f - Mathf.Exp(-dt);
        rateAdded = Mathf.Max(0f, rateAdded * (1f - k));
        rateRemoved = Mathf.Max(0f, rateRemoved * (1f - k));
        addedPerSecond = Mathf.RoundToInt(rateAdded);
        removedPerSecond = Mathf.RoundToInt(rateRemoved);

        if (rustle)
        {
            float target = 1f - Mathf.Exp(-(rateAdded + rateRemoved) / rustleRate);
            float speed = target > rustleLevel ? 12f : 2.5f;
            rustleLevel = Mathf.Lerp(rustleLevel, target, 1f - Mathf.Exp(-speed * dt));
            rustle.volume = rustleLevel * rustleVolume * volume;
        }
    }

    void OnState(AsyncGPUReadbackRequest req, int n)
    {
        pending = Mathf.Max(0, pending - 1);
        if (this == null || !isActiveAndEnabled || req.hasError) return;
        var data = req.GetData<float>();
        if (data.Length != n * n) return;

        // New board (first frame or a different nail count): remember it, no sound for the rebuild itself
        if (prev == null || prevNails != n)
        {
            prev = new float[n * n];
            data.CopyTo(prev);
            prevNails = n;
            return;
        }

        int added = 0, removed = 0;
        for (int b = 1; b < n; b++)
        {
            int row = b * n;
            for (int a = 0; a < b; a++)
            {
                int i = row + a;
                bool on = data[i] > 0.5f, was = prev[i] > 0.5f;
                if (on == was) continue;
                // State texture is written as (x = a, y = b) with a < b
                if (on) Pick(addedPick, ref added, a, b);
                else Pick(removedPick, ref removed, a, b);
            }
        }
        data.CopyTo(prev);

        rateAdded += added;
        rateRemoved += removed;
        if (added + removed == 0) return;

        // Spend the one-shot budget on pulls first, releases get what is left
        int pulls = Mathf.Min(Mathf.Min(added, Reservoir), Mathf.FloorToInt(tokens));
        tokens -= pulls;
        int releases = Mathf.Min(Mathf.Min(removed, Reservoir), Mathf.FloorToInt(tokens));
        tokens -= releases;
        if (pulls + releases == 0) return;

        // Many shots in one frame share the loudness so a mass change does not clip
        float share = 1f / Mathf.Sqrt(pulls + releases);
        double now = AudioSettings.dspTime;
        // In a burst long pulls would smear into hiss: mostly short nail ticks over the rustle then,
        // full pulls only for the occasional single thread
        bool burst = added + removed > burstThreshold;
        for (int i = 0; i < pulls; i++)
        {
            var c = addedPick[i];
            if (!burst || Random.value < 0.2f)
                Shot(RandomClip(pullClips), c, n, pullVolume * share, now + Random.value * 0.03);
            // The thread winds from nail a to nail b over windTime, then hooks the far nail
            if (burst || Random.value < 0.6f)
                Shot(RandomClip(nailClips), c, n, nailVolume * share, now + art.windTime * Random.Range(0.85f, 1f));
        }
        for (int i = 0; i < releases; i++)
            Shot(RandomClip(releaseClips), removedPick[i], n, releaseVolume * share, now + Random.value * 0.03);
    }

    static void Pick(Vector2Int[] pick, ref int count, int a, int b)
    {
        // Reservoir sampling: every event has the same chance to end up in the sample
        if (count < pick.Length) pick[count] = new Vector2Int(a, b);
        else
        {
            int j = Random.Range(0, count + 1);
            if (j < pick.Length) pick[j] = new Vector2Int(a, b);
        }
        count++;
    }

    static AudioClip RandomClip(AudioClip[] clips) =>
        clips == null || clips.Length == 0 ? null : clips[Random.Range(0, clips.Length)];

    void Shot(AudioClip clip, Vector2Int chord, int n, float gain, double at)
    {
        if (!clip || pool == null) return;
        float ta = chord.x * Mathf.PI * 2f / n, tb = chord.y * Mathf.PI * 2f / n;
        // Chord length relative to the diameter, 0..1
        float len = Mathf.Abs(Mathf.Sin((tb - ta) * 0.5f));
        float pan = (Mathf.Cos(ta) + Mathf.Cos(tb)) * 0.5f * stereoWidth;

        var s = pool[nextVoice];
        nextVoice = (nextVoice + 1) % pool.Length;
        s.Stop();
        s.clip = clip;
        s.volume = gain * volume * Random.Range(0.75f, 1f);
        s.pitch = Mathf.Lerp(pitchRange.y, pitchRange.x, len) * (1f + Random.Range(-pitchJitter, pitchJitter));
        s.panStereo = Mathf.Clamp(pan, -1f, 1f);
        s.PlayScheduled(at);
    }
}
