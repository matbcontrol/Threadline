using UnityEngine;

// Shows one figure from the list and turns it: the threads re-tension into the new portrait on their own.
public class FigureSwitcher : MonoBehaviour
{
    public GameObject[] figures;
    [Range(0, 3)] public int figure;
    [Tooltip("Object turned by the Model Rotation slider (the turntable with the bust)")]
    public Transform turnTarget;
    [Range(-90f, 90f)] public float modelRotation;

    void Update() => Apply();

    public void Apply()
    {
        if (figures != null)
            for (int i = 0; i < figures.Length; i++)
                if (figures[i] && figures[i].activeSelf != (i == figure)) figures[i].SetActive(i == figure);
        if (turnTarget) turnTarget.localRotation = Quaternion.Euler(0f, modelRotation, 0f);
    }
}
