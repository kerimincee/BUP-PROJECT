using UnityEngine;
using System.Collections;

public class DisableTracking : MonoBehaviour
{
    public Camera cam;
    private Vector3 startPos;

    public bool disablePosition = false;
    public bool disableRotation = false;

    void Start()
    {
        cam = GetComponentInChildren<Camera>();
        startPos = transform.localPosition;
    }

    void Update()
    {
        if (cam != null)
        {
            if (disablePosition)
            {
                transform.localPosition = startPos - cam.transform.localPosition;
            }
            if (disableRotation)
            {
                transform.localRotation = Quaternion.Inverse(cam.transform.localRotation);
            }
        }
    }
}