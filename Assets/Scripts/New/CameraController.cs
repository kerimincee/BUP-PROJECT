using UnityEngine;
using UnityEngine.XR;
using System.Collections;

// Script obtained from MVN to reset the camera of the Oculus onto the head of the MVN Avatar.
// Used in combination with AnchorController.cs

public class CameraController : MonoBehaviour
{
    Transform
        childCamera;

    [Tooltip("If enabled, inverts position. False by default to prevent VR lockup.")]
    public bool lockPosition = false;
    [Tooltip("If enabled, inverts rotation. False by default to prevent VR head turning lockup.")]
    public bool lockRotation = false;
    [Tooltip("Key used to recenter the VR headset. Kept separate from R, which selects reverse gear.")]
    public KeyCode recenterKey = KeyCode.C;

    // Use this for initialization
    void Start()
    {
        childCamera = transform.parent;
    }

    private void Update()
    {
        if (Input.GetKeyDown(recenterKey))
        {
            CameraCounter.RecenterXR();
        }
    }
    // Update is called once per frame
    void LateUpdate()
    {
        if (childCamera != null)
        {
            if (lockPosition)
            {
                Vector3 invertedPosition = -childCamera.localPosition;
                transform.localPosition = invertedPosition;
            }
            if (lockRotation)
            {
                Quaternion invertedRotation = Quaternion.Inverse(childCamera.localRotation);
                transform.localRotation = invertedRotation;
            }
        }
    }
}
