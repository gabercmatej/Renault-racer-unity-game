using System;
using System.Collections.Generic;
using UnityEngine;

namespace RenaultRacer.UI
{
    /// <summary>
    /// The Phase 10 UI art: one atlas texture plus its json, both produced by
    /// Tools/ArtPrep/prepare_ui.py and loaded from Resources/UI once, on first use.
    ///
    /// Loaded through Resources rather than wired in the scene for the same reason the UI
    /// is built in code (see GameUi): nothing in Main.unity has to change for the art to
    /// arrive, and the atlas is the single source of every sprite rect.
    ///
    /// Sprites are created once with Sprite.Create. Each json entry carries a `scale` -
    /// texels per UI unit at the 1280x720 reference - and the sprite is created with
    /// pixelsPerUnit = scale * 100, which is what makes a 9-slice border draw at its DESIGN
    /// thickness under a CanvasScaler whose referencePixelsPerUnit is 100.
    ///
    /// Everything degrades: if the atlas is missing, <see cref="Get"/> returns null and the
    /// views fall back to their plain generated shapes rather than throwing.
    /// </summary>
    public static class UiArt
    {
        public const string AtlasPath = "UI/ui_atlas";

        [Serializable]
        private sealed class Entry
        {
            public string name;
            public float[] rect;     // x, y, w, h - texels, bottom-left origin
            public float[] pivot;    // normalised
            public float[] border;   // left, bottom, right, top - texels
            public float scale;      // texels per UI unit
            public float[] size;     // design size, UI units
        }

        [Serializable]
        private sealed class Countdown
        {
            public float[] lampCentres;   // x0, y0, x1, y1, x2, y2 normalised in the housing
            public float lampDiameter;    // lamp sprite width / housing width
        }

        [Serializable]
        private sealed class AtlasFile
        {
            public int width;
            public int height;
            public Entry[] sprites;
            public Countdown countdown;
        }

        private static bool _attempted;
        private static readonly Dictionary<string, Sprite> Sprites = new Dictionary<string, Sprite>();
        private static readonly Dictionary<string, Vector2> Sizes = new Dictionary<string, Vector2>();
        private static Vector2[] _lampCentres;
        private static float _lampDiameter;

        /// <summary>True once the atlas has been found and parsed.</summary>
        public static bool Available
        {
            get { EnsureLoaded(); return Sprites.Count > 0; }
        }

        /// <summary>The sprite called <paramref name="name"/>, or null when there is no art.</summary>
        public static Sprite Get(string name)
        {
            EnsureLoaded();
            Sprite s;
            return Sprites.TryGetValue(name, out s) ? s : null;
        }

        /// <summary>Design size in UI units, or <paramref name="fallback"/> without art.</summary>
        public static Vector2 Size(string name, Vector2 fallback)
        {
            EnsureLoaded();
            Vector2 v;
            return Sizes.TryGetValue(name, out v) ? v : fallback;
        }

        /// <summary>
        /// Centre of countdown lamp <paramref name="index"/> in the housing, normalised
        /// with a bottom-left origin - use it directly as an anchor.
        /// </summary>
        public static Vector2 LampCentre(int index)
        {
            EnsureLoaded();
            if (_lampCentres == null || index < 0 || index >= _lampCentres.Length)
                return new Vector2(0.2f + 0.3f * index, 0.5f);
            return _lampCentres[index];
        }

        /// <summary>Lamp sprite width as a fraction of the housing width.</summary>
        public static float LampDiameter
        {
            get { EnsureLoaded(); return _lampDiameter > 0f ? _lampDiameter : 0.23f; }
        }

        private static void EnsureLoaded()
        {
            if (_attempted) return;
            _attempted = true;

            Texture2D tex = Resources.Load<Texture2D>(AtlasPath);
            TextAsset json = Resources.Load<TextAsset>(AtlasPath);
            if (tex == null || json == null)
            {
                Debug.LogWarning("[UiArt] Resources/" + AtlasPath + " not found - using plain UI shapes.");
                return;
            }

            AtlasFile file;
            try
            {
                file = JsonUtility.FromJson<AtlasFile>(json.text);
            }
            catch (Exception e)
            {
                Debug.LogError("[UiArt] could not parse " + AtlasPath + ".json: " + e.Message);
                return;
            }
            if (file == null || file.sprites == null) return;

            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;

            for (int i = 0; i < file.sprites.Length; i++)
            {
                Entry e = file.sprites[i];
                if (e == null || string.IsNullOrEmpty(e.name) || e.rect == null || e.rect.Length < 4)
                    continue;

                float scale = e.scale > 0f ? e.scale : 1f;
                Vector2 pivot = (e.pivot != null && e.pivot.Length >= 2)
                    ? new Vector2(e.pivot[0], e.pivot[1]) : new Vector2(0.5f, 0.5f);
                Vector4 border = (e.border != null && e.border.Length >= 4)
                    ? new Vector4(e.border[0], e.border[1], e.border[2], e.border[3]) : Vector4.zero;

                Sprite s = Sprite.Create(tex, new Rect(e.rect[0], e.rect[1], e.rect[2], e.rect[3]),
                                         pivot, 100f * scale, 0, SpriteMeshType.FullRect, border);
                s.name = e.name;
                Sprites[e.name] = s;

                Vector2 size = (e.size != null && e.size.Length >= 2)
                    ? new Vector2(e.size[0], e.size[1])
                    : new Vector2(e.rect[2] / scale, e.rect[3] / scale);
                Sizes[e.name] = size;
            }

            if (file.countdown != null && file.countdown.lampCentres != null
                && file.countdown.lampCentres.Length >= 6)
            {
                _lampCentres = new Vector2[3];
                for (int i = 0; i < 3; i++)
                    _lampCentres[i] = new Vector2(file.countdown.lampCentres[2 * i],
                                                  file.countdown.lampCentres[2 * i + 1]);
                _lampDiameter = file.countdown.lampDiameter;
            }
        }
    }
}
