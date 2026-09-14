using UnityEngine;

namespace FluidStabilizer.Sim
{
    /// <summary>
    /// Tiny shared IMGUI helpers so on-screen text stays readable over any
    /// background (sky, road, water) — a dark panel behind every text block, plus
    /// a text style with a drop shadow.
    /// </summary>
    public static class SimGui
    {
        private static Texture2D _px;

        public static Texture2D Pixel
        {
            get
            {
                if (_px == null)
                {
                    _px = new Texture2D(1, 1);
                    _px.SetPixel(0, 0, Color.white);
                    _px.Apply();
                    _px.hideFlags = HideFlags.HideAndDontSave;
                }
                return _px;
            }
        }

        public static void Box(Rect r, Color c)
        {
            var prev = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, Pixel);
            GUI.color = prev;
        }

        /// <summary>A dark, slightly translucent panel with a 1px lighter border. Pad your content by ~10px.</summary>
        public static void Panel(Rect r, float alpha = 0.78f)
        {
            Box(r, new Color(0.05f, 0.06f, 0.08f, alpha));
            var edge = new Color(1f, 1f, 1f, 0.12f);
            Box(new Rect(r.x, r.y, r.width, 1), edge);
            Box(new Rect(r.x, r.yMax - 1, r.width, 1), edge);
            Box(new Rect(r.x, r.y, 1, r.height), edge);
            Box(new Rect(r.xMax - 1, r.y, 1, r.height), edge);
        }

        public static void Border(Rect r, Color c, float t = 2f)
        {
            Box(new Rect(r.x, r.y, r.width, t), c);
            Box(new Rect(r.x, r.yMax - t, r.width, t), c);
            Box(new Rect(r.x, r.y, t, r.height), c);
            Box(new Rect(r.xMax - t, r.y, t, r.height), c);
        }

        /// <summary>A label style with a soft drop shadow — legible even without a panel behind it.
        /// Word-wraps by default so Korean text never runs past its box edge.</summary>
        public static GUIStyle Shadowed(int fontSize, Color color, FontStyle style = FontStyle.Normal)
        {
            var s = new GUIStyle(GUI.skin.label)
            {
                fontSize = fontSize,
                fontStyle = style,
                normal = { textColor = color },
                richText = false,
                wordWrap = true,
            };
            return s;
        }

        /// <summary>Draw text with a 1px shadow at <paramref name="r"/> (use inside/outside a panel).</summary>
        public static void ShadowLabel(Rect r, string text, GUIStyle style)
        {
            var prev = style.normal.textColor;
            var shadowRect = new Rect(r.x + 1.5f, r.y + 1.5f, r.width, r.height);
            style.normal.textColor = new Color(0f, 0f, 0f, 0.85f);
            GUI.Label(shadowRect, text, style);
            style.normal.textColor = prev;
            GUI.Label(r, text, style);
        }
    }
}
