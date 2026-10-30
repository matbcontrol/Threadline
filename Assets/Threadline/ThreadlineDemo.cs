using System;
using System.Collections;
using UnityEngine;

// Optional tour (Auto Play): eases the parameters one after another so you can watch how each slider changes the portrait.
public class ThreadlineDemo : MonoBehaviour
{
    public StringArt art;
    public FigureSwitcher figures;
    public Spin spin;
    public bool autoPlay;
    public int[] figureOrder = { 1, 2 };

    public static event Action Finished;

    IEnumerator Start()
    {
        if (!autoPlay) yield break;

        // Entering Play mode takes a variable amount of time, so the board is cleared after a short pause
        // and David's portrait always builds from an empty board
        yield return Hold(2f);
        art.ResetThreads();
        yield return Hold(6f);

        // Thin threads versus thick: the thread count drops from thousands to hundreds and back
        yield return Tween(v => art.lineDarkness = v, art.lineDarkness, 0.1f, 2.5f);
        yield return Hold(1.5f);
        yield return Tween(v => art.lineDarkness = v, 0.1f, 0.025f, 2.5f);
        yield return Hold(1.5f);

        // Changing the nail count rebuilds the board: the portrait tensions again, coarser or finer
        art.nails = 128;
        yield return Hold(5f);
        art.nails = 320;
        yield return Hold(5f);

        yield return Tween(v => art.contrast = v, art.contrast, 2.2f, 2f);
        yield return Hold(0.8f);
        yield return Tween(v => art.contrast = v, 2.2f, 1.15f, 2f);
        yield return Hold(0.5f);

        // Figure change: threads re-tension into the new portrait. The default order skips Selene's horse, which reads poorly as a portrait
        foreach (int f in figureOrder)
        {
            figures.figure = f;
            yield return Hold(5f);
        }
        figures.figure = 0;
        yield return Hold(4f);

        spin.degreesPerSecond = new Vector3(0f, 25f, 0f);
        yield return Hold(7f);
        spin.degreesPerSecond = Vector3.zero;
        yield return Hold(1.5f);

        Finished?.Invoke();
    }

    static IEnumerator Hold(float seconds)
    {
        yield return new WaitForSeconds(seconds);
    }

    static IEnumerator Tween(Action<float> set, float from, float to, float seconds)
    {
        for (float t = 0f; t < seconds; t += Time.deltaTime)
        {
            set(Mathf.Lerp(from, to, Mathf.SmoothStep(0f, 1f, t / seconds)));
            yield return null;
        }
        set(to);
    }
}
