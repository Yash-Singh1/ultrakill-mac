using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ULTRAKILL.MacPort
{
    public sealed class FpsDisplay : MonoBehaviour
    {
        static FpsDisplay instance;
        TextMeshProUGUI label;
        Canvas canvas;
        double sampleStart;
        int frames;

        public static void Install()
        {
            if (instance) return;
            var root = new GameObject("Mac FPS counter", typeof(RectTransform));
            DontDestroyOnLoad(root);
            instance = root.AddComponent<FpsDisplay>();
        }

        void Start()
        {
            canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32767;
            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;
            var textObject = new GameObject("FPS", typeof(RectTransform));
            textObject.transform.SetParent(transform, false);
            label = textObject.AddComponent<TextMeshProUGUI>();
            label.font = TMP_Settings.defaultFontAsset;
            label.fontSize = 22;
            label.color = Color.white;
            label.alignment = TextAlignmentOptions.TopLeft;
            label.raycastTarget = false;
            label.enableWordWrapping = false;
            label.outlineWidth = 0.2f;
            label.outlineColor = Color.black;
            var rect = label.rectTransform;
            rect.anchorMin = rect.anchorMax = new Vector2(0, 1);
            rect.pivot = new Vector2(0, 1);
            rect.anchoredPosition = new Vector2(12, -10);
            rect.sizeDelta = new Vector2(300, 36);
            label.text = "FPS ...";
            sampleStart = Time.realtimeSinceStartupAsDouble;
        }

        void Update()
        {
            if (!label) return;
            if (Input.GetKeyDown(KeyCode.F7)) canvas.enabled = !canvas.enabled;
            frames++;
            double elapsed = Time.realtimeSinceStartupAsDouble - sampleStart;
            if (elapsed < 0.25) return;
            float fps = (float)(frames / elapsed);
            label.SetText("{0:0} FPS  {1:1} ms", fps, 1000f / fps);
            sampleStart = Time.realtimeSinceStartupAsDouble;
            frames = 0;
        }
    }
}
