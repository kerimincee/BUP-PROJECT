using UnityEngine;

/// <summary>
/// Başlangıç ekranı ve tüm senaryo panelleri için ortak IMGUI tasarım sistemi.
/// Yeni senaryolar aynı görsel dili bu sınıftan kullanabilir.
/// </summary>
public static class ProfessionalGUI
{
    public static GUIStyle Panel { get; private set; }
    public static GUIStyle Header { get; private set; }
    public static GUIStyle SubHeader { get; private set; }
    public static GUIStyle Section { get; private set; }
    public static GUIStyle Label { get; private set; }
    public static GUIStyle Muted { get; private set; }
    public static GUIStyle Button { get; private set; }
    public static GUIStyle PrimaryButton { get; private set; }
    public static GUIStyle SelectedButton { get; private set; }
    public static GUIStyle DangerButton { get; private set; }
    public static GUIStyle Speed { get; private set; }
    public static GUIStyle Badge { get; private set; }

    static GUISkin cachedSkin;
    static Texture2D panelTexture;
    static Texture2D buttonTexture;
    static Texture2D buttonHoverTexture;
    static Texture2D primaryTexture;
    static Texture2D selectedTexture;
    static Texture2D dangerTexture;

    public static void Ensure()
    {
        if (cachedSkin == GUI.skin && Panel != null)
            return;

        cachedSkin = GUI.skin;
        panelTexture = Solid(new Color(0.035f, 0.055f, 0.075f, 0.96f));
        buttonTexture = Solid(new Color(0.10f, 0.14f, 0.18f, 1f));
        buttonHoverTexture = Solid(new Color(0.15f, 0.22f, 0.28f, 1f));
        primaryTexture = Solid(new Color(0.02f, 0.48f, 0.72f, 1f));
        selectedTexture = Solid(new Color(0.05f, 0.62f, 0.47f, 1f));
        dangerTexture = Solid(new Color(0.78f, 0.18f, 0.12f, 1f));

        Panel = new GUIStyle(GUI.skin.box)
        {
            padding = new RectOffset(24, 24, 20, 22),
            normal = { background = panelTexture },
            border = new RectOffset(1, 1, 1, 1)
        };
        Header = new GUIStyle(GUI.skin.label)
        {
            fontSize = 26,
            fontStyle = FontStyle.Bold,
            wordWrap = true,
            normal = { textColor = Color.white },
            margin = new RectOffset(0, 0, 0, 2)
        };
        SubHeader = new GUIStyle(GUI.skin.label)
        {
            fontSize = 15,
            wordWrap = true,
            normal = { textColor = new Color(0.62f, 0.72f, 0.80f) },
            margin = new RectOffset(0, 0, 0, 12)
        };
        Section = new GUIStyle(GUI.skin.label)
        {
            fontSize = 14,
            fontStyle = FontStyle.Bold,
            normal = { textColor = new Color(0.20f, 0.78f, 1f) },
            margin = new RectOffset(0, 0, 12, 6)
        };
        Label = new GUIStyle(GUI.skin.label)
        {
            fontSize = 16,
            wordWrap = true,
            normal = { textColor = new Color(0.92f, 0.95f, 0.97f) }
        };
        Muted = new GUIStyle(Label)
        {
            fontSize = 13,
            normal = { textColor = new Color(0.58f, 0.66f, 0.72f) }
        };
        Button = MakeButton(buttonTexture, buttonHoverTexture);
        PrimaryButton = MakeButton(primaryTexture, buttonHoverTexture);
        SelectedButton = MakeButton(selectedTexture, selectedTexture);
        DangerButton = MakeButton(dangerTexture, dangerTexture);
        Speed = new GUIStyle(Header)
        {
            fontSize = 44,
            alignment = TextAnchor.MiddleCenter
        };
        Badge = new GUIStyle(Label)
        {
            fontSize = 15,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
            padding = new RectOffset(10, 10, 7, 7),
            normal = { background = buttonTexture, textColor = Color.white }
        };
    }

    static GUIStyle MakeButton(Texture2D normal, Texture2D hover)
    {
        return new GUIStyle(GUI.skin.button)
        {
            fontSize = 16,
            fontStyle = FontStyle.Bold,
            fixedHeight = 42f,
            alignment = TextAnchor.MiddleCenter,
            padding = new RectOffset(14, 14, 8, 8),
            margin = new RectOffset(0, 0, 4, 4),
            normal = { background = normal, textColor = Color.white },
            hover = { background = hover, textColor = Color.white },
            active = { background = hover, textColor = Color.white },
            focused = { background = normal, textColor = Color.white }
        };
    }

    public static void DrawProgress(float value, Color color, float height = 8f)
    {
        Rect rect = GUILayoutUtility.GetRect(1f, height, GUILayout.ExpandWidth(true));
        GUI.DrawTexture(rect, buttonTexture);
        rect.width *= Mathf.Clamp01(value);
        Color old = GUI.color;
        GUI.color = color;
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = old;
    }

    public static Texture2D Solid(Color color)
    {
        var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false)
        {
            hideFlags = HideFlags.HideAndDontSave
        };
        texture.SetPixel(0, 0, color);
        texture.Apply();
        return texture;
    }
}
