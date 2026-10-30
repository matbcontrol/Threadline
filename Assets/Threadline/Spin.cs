using UnityEngine;

public class Spin : MonoBehaviour
{
    public Vector3 degreesPerSecond = new Vector3(0f, 20f, 0f);

    void Update()
    {
        transform.Rotate(degreesPerSecond * Time.deltaTime, Space.World);
    }
}
