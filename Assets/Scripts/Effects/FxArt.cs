using System;
using UnityEngine;

namespace RenaultRacer.Effects
{
    /// <summary>
    /// The small effects atlas (Tools/ArtPrep/prepare_fx.py): smoke puff, impact burst,
    /// speed streak and electric spark. Loaded once, sprites created once, shared by the
    /// particle systems and the speed lines. Missing art returns nulls and every caller
    /// keeps its previous look.
    /// </summary>
    public static class FxArt
    {
        public const string AtlasPath = "FX/fx_atlas";

        [Serializable] private sealed class SpriteJson { public string name; public float[] rect; public float[] pivot; }
        [Serializable] private sealed class AtlasJson { public SpriteJson[] sprites; }

        private static bool _loaded;
        private static Texture2D _texture;
        public static Sprite Smoke { get; private set; }
        public static Sprite Impact { get; private set; }
        public static Sprite Streak { get; private set; }
        public static Sprite Spark { get; private set; }

        public static Texture2D Texture { get { Load(); return _texture; } }

        public static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            _texture = Resources.Load<Texture2D>(AtlasPath);
            TextAsset json = Resources.Load<TextAsset>(AtlasPath);
            if (_texture == null || json == null) return;
            AtlasJson data = JsonUtility.FromJson<AtlasJson>(json.text);
            if (data == null || data.sprites == null) return;
            for (int i = 0; i < data.sprites.Length; i++)
            {
                SpriteJson s = data.sprites[i];
                if (s.rect == null || s.rect.Length != 4) continue;
                Vector2 pivot = s.pivot != null && s.pivot.Length == 2 ? new Vector2(s.pivot[0], s.pivot[1]) : new Vector2(0.5f, 0.5f);
                Sprite sp = Sprite.Create(_texture, new Rect(s.rect[0], s.rect[1], s.rect[2], s.rect[3]),
                                          pivot, 100f, 0, SpriteMeshType.FullRect);
                sp.name = s.name;
                switch (s.name)
                {
                    case "smoke": Smoke = sp; break;
                    case "impact": Impact = sp; break;
                    case "streak": Streak = sp; break;
                    case "spark": Spark = sp; break;
                }
            }
        }
    }
}
