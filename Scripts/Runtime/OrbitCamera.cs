using UnityEngine;

namespace FireEscape
{
    /// <summary>Independent view navigation; never changes simulation time or position.</summary>
    [RequireComponent(typeof(Camera))]
    public sealed class OrbitCamera : MonoBehaviour
    {
        public Vector3 Target;
        public float Distance = 43f;
        public float Yaw = -27f;
        public float Pitch = 55f;
        public bool TopDown;
        public float MapWidth = 36f;
        public float MapDepth = 26f;
        public float MinHeight, MaxHeight;
        float Extent { get { return Mathf.Max(MapWidth, MapDepth, 1f); } }
        private Camera view;
        private Vector3 previousMouse;
        private bool mouseInitialised;

        private void Awake() { view = GetComponent<Camera>(); }

        public void ResetView(float width, float depth, float minHeight, float maxHeight, bool topDown)
        {
            MapWidth = width; MapDepth = depth; MinHeight = minHeight; MaxHeight = maxHeight;
            Target = new Vector3(width * .5f, (minHeight + maxHeight) * .5f, depth * .5f);
            Distance = Mathf.Max(width, depth) * 1.35f;
            Yaw = -27f; Pitch = 55f; TopDown = topDown;
            ApplyView();
        }

        private void LateUpdate()
        {
            if (view == null) view = GetComponent<Camera>();
            // Keep the simulation framed in the part of the window beside the control panel.
            float uiScale = Mathf.Clamp(Screen.width / 1440f, .68f, 1.25f);
            float left = Mathf.Min(370f * uiScale, Screen.width * .48f);
            view.pixelRect = new Rect(left, 0, Mathf.Max(1, Screen.width - left), Screen.height);
            Vector3 mouse = Input.mousePosition;
            Vector3 mouseDelta = mouseInitialised ? mouse - previousMouse : Vector3.zero;
            previousMouse = mouse; mouseInitialised = true;
            bool inside = mouse.x > left && mouse.x < Screen.width;
            if (inside && !EnvironmentView.CameraInputBlocked)
            {
                float scroll = Input.mouseScrollDelta.y;
                Distance = Mathf.Clamp(Distance * Mathf.Exp(-scroll * .09f), Extent * .02f, Extent * 3f);
                if (Input.GetMouseButton(1))
                {
                    if (TopDown) { TopDown = false; Pitch = 70f; }
                    Yaw += mouseDelta.x * .25f;
                    Pitch = Mathf.Clamp(Pitch - mouseDelta.y * .19f, 22f, 85f);
                }
                if (Input.GetMouseButton(2))
                {
                    Vector3 right = transform.right; right.y = 0; right.Normalize();
                    Vector3 forward = Vector3.Cross(right, Vector3.up).normalized;
                    Target -= right * mouseDelta.x * Distance * .0012f;
                    Target -= forward * mouseDelta.y * Distance * .0012f;
                    Target.x = Mathf.Clamp(Target.x, -.1f * Extent, MapWidth + .1f * Extent);
                    Target.z = Mathf.Clamp(Target.z, -.1f * Extent, MapDepth + .1f * Extent);
                }
            }
            ApplyView();
        }

        private void ApplyView()
        {
            if (view == null) view = GetComponent<Camera>();
            // Clip planes follow the map size; near plane grows with distance to keep depth precision for overlays.
            float relief = Mathf.Max(0, MaxHeight - MinHeight);
            view.nearClipPlane = Mathf.Max(.15f, Distance * .002f);
            view.farClipPlane = Distance + 2f * (Extent + relief);
            view.orthographic = TopDown;
            if (TopDown)
            {
                float aspect = Mathf.Max(.2f, view.aspect);
                // Distance controls zoom in both perspective and orthographic mode.
                view.orthographicSize = Mathf.Max(MapDepth * .55f, MapWidth / aspect * .55f) * Distance / (Mathf.Max(MapWidth, MapDepth) * 1.35f);
                transform.position = new Vector3(Target.x, MaxHeight + Extent, Target.z);
                transform.rotation = Quaternion.Euler(90f, 0, 0);
            }
            else
            {
                Quaternion rotation = Quaternion.Euler(Pitch, Yaw, 0);
                transform.rotation = rotation;
                transform.position = Target - rotation * Vector3.forward * Distance;
                view.fieldOfView = 48f;
            }
        }
    }
}
