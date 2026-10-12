using System.Collections.Generic;
using UnityEngine;

namespace DigimonNetplay
{
    /// <summary>The game's own character select art (face icons and full artwork per color), drawn with IMGUI.</summary>
    internal static class Portraits
    {
        private static readonly Dictionary<string, Sprite> Cache = new Dictionary<string, Sprite>();

        public static Sprite Face(string character) => Load("UI/Menu_CharSelectionFaces/" + character);
        public static Sprite Art(string character, int color) => Load("UI/Menu_CharSelectionArtwork/" + character + color);

        private static Sprite Load(string path)
        {
            if (Cache.TryGetValue(path, out var s)) return s;
            try { s = Resources.Load(path, typeof(Sprite)) as Sprite; }
            catch { s = null; }
            Cache[path] = s;
            return s;
        }

        /// <summary>Draws <paramref name="sprite"/> fitted inside <paramref name="area"/>, keeping its shape. False if there is nothing to draw.</summary>
        public static bool Draw(Rect area, Sprite sprite, float alpha = 1f)
        {
            if (sprite == null || sprite.texture == null) return false;
            if (Event.current.type != EventType.Repaint) return true;
            var tex = sprite.texture;
            Rect px;
            try { px = sprite.textureRect; }      // where the sprite sits in its (atlas) texture
            catch { px = sprite.rect; }          // tightly packed atlas: best effort
            if (px.width <= 0 || px.height <= 0) return false;
            var uv = new Rect(px.x / tex.width, px.y / tex.height, px.width / tex.width, px.height / tex.height);
            float scale = Mathf.Min(area.width / px.width, area.height / px.height);
            float w = px.width * scale, h = px.height * scale;
            var r = new Rect(area.x + (area.width - w) / 2, area.y + (area.height - h) / 2, w, h);
            var was = GUI.color;
            GUI.color = new Color(was.r, was.g, was.b, was.a * alpha);
            GUI.DrawTextureWithTexCoords(r, tex, uv, true);
            GUI.color = was;
            return true;
        }
    }
}
