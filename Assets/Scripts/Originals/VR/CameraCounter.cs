using UnityEngine;
using UnityEngine.XR;
using System.Collections;

public class CameraCounter : MonoBehaviour {

    Transform childCamera;

    [Tooltip("If enabled, inverts the local position of the camera to pin it at origin. Kept false by default to prevent head rotation lockup in 6DoF VR.")]
    public bool lockPosition = false;

    // Use this for initialization
    void Start () {
        if (transform.childCount > 0)
        {
            childCamera = transform.GetChild(0);
        }
    }

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.R))
        {
            RecenterXR();
        }
    }

    public static void RecenterXR()
    {
        // Modern OpenXR / XR Subsystems recenter
        var subsystems = new System.Collections.Generic.List<XRInputSubsystem>();
        SubsystemManager.GetSubsystems(subsystems);
        foreach (var subsystem in subsystems)
        {
            subsystem.TryRecenter();
        }

        // Legacy XR fallback
        UnityEngine.XR.InputTracking.Recenter();
    }

    // Update is called once per frame
    void LateUpdate () {
        if (lockPosition && childCamera != null)
        {
            Vector3 invertedPosition = -childCamera.localPosition;
            transform.localPosition = invertedPosition;
        }
    }
}
