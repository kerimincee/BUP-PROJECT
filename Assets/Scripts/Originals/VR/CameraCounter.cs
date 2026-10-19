using UnityEngine;
using UnityEngine.XR;
using System.Collections;

public class CameraCounter : MonoBehaviour {

    Transform childCamera;

    [Tooltip("Apply the XR headset rotation to the child camera. Enable this for HMD mode.")]
    public bool trackHeadset = true;

    [Tooltip("If enabled, inverts the local position of the camera to pin it at origin. Kept false by default to prevent head rotation lockup in 6DoF VR.")]
    public bool lockPosition = false;

    [Tooltip("Key used to recenter the VR headset. Kept separate from R, which selects reverse gear.")]
    public KeyCode recenterKey = KeyCode.C;

    // Use this for initialization
    void Start () {
        if (transform.childCount > 0)
        {
            childCamera = transform.GetChild(0);
        }
    }

    private void Update()
    {
        if (Input.GetKeyDown(recenterKey))
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

    // Apply the HMD pose explicitly. The old Unity VR integration updated Camera
    // transforms implicitly, but that is not reliable with the current XR loaders.
    // CameraCounter is enabled only for VR/Suite modes by PlayerAvatar.
    void LateUpdate () {
        if (childCamera == null)
        {
            return;
        }

        if (trackHeadset)
        {
            InputDevice headset = InputDevices.GetDeviceAtXRNode(XRNode.CenterEye);

            if (headset.TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion rotation))
            {
                childCamera.localRotation = rotation;
            }

            // Keep the camera at the configured driver-seat position. Applying the
            // room-scale HMD position here adds the user's standing height to the
            // seat anchor and places the view on top of the vehicle.
            childCamera.localPosition = Vector3.zero;
        }

        if (lockPosition)
        {
            Vector3 invertedPosition = -childCamera.localPosition;
            transform.localPosition = invertedPosition;
        }
    }
}
