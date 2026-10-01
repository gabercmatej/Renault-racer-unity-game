using System;
using UnityEngine;

namespace RenaultRacer.Vehicle
{
    /// <summary>
    /// The prepared car atlas (Tools/ArtPrep/prepare_car.py): eleven angle views, eleven
    /// brake-light overlays and the shadow, with the metadata that keeps them aligned.
    ///
    /// Loaded once from Resources and turned into Sprites once. Nothing here runs per
    /// frame. The scene does not reference the atlas directly, so regenerating it never
    /// needs the scene to be touched.
    /// </summary>
    public sealed class CarArt
    {
        public const string AtlasPath = "Car/car_atlas";

        [Serializable]
        private sealed class FrameJson
        {
            public string key;
            public float[] body;
            public float[] bodyPivot;
            public float[] brake;
            public float[] brakePivot;
            public float left;
            public float right;
            public float[] tyres;    // rear tyre contact points from the pivot, px, y up: lx, ly, rx, ry
            public float[] ground;   // footprint (lowest quarter of the silhouette) from the pivot, px: min, max
        }

        [Serializable]
        private sealed class AtlasJson
        {
            public int atlasSize;
            public float carHeightPx;
            public float straightWidthPx;
            public FrameJson[] frames;
            public float[] shadow;
        }

        /// <summary>Sprite pixels per world unit. Scale is applied on the transform.</summary>
        public const float PixelsPerUnit = 100f;

        /// <summary>Views indexed by signed frame index + 5: [0]=L5 .. [5]=S .. [10]=R5.</summary>
        public readonly Sprite[] Body = new Sprite[11];
        public readonly Sprite[] Brake = new Sprite[11];
        /// <summary>Body extent left / right of the pivot, in sprite pixels.</summary>
        public readonly float[] Left = new float[11];
        public readonly float[] Right = new float[11];
        /// <summary>Footprint centre and width relative to the pivot, sprite px.</summary>
        public readonly float[] GroundCentre = new float[11];
        public readonly float[] GroundWidth = new float[11];
        /// <summary>Rear tyre contact points relative to the pivot, sprite px (y up).</summary>
        public readonly Vector2[] TyreLeft = new Vector2[11];
        public readonly Vector2[] TyreRight = new Vector2[11];
        public Sprite Shadow;
        public float StraightWidthPx;
        public float CarHeightPx;

        public static int Slot(int signedIndex)
        {
            return Mathf.Clamp(signedIndex, -CarFrameSelector.MaxIndex, CarFrameSelector.MaxIndex)
                   + CarFrameSelector.MaxIndex;
        }

        private static int SlotForKey(string key)
        {
            if (key == "S") return Slot(0);
            int n = key[1] - '0';
            return Slot(key[0] == 'L' ? -n : n);
        }

        /// <summary>Null if the atlas is missing or incomplete - the caller keeps its placeholder.</summary>
        public static CarArt Load()
        {
            Texture2D tex = Resources.Load<Texture2D>(AtlasPath);
            TextAsset json = Resources.Load<TextAsset>(AtlasPath);
            if (tex == null || json == null)
            {
                Debug.LogWarning("[CarArt] " + AtlasPath + " not found - keeping the placeholder car.");
                return null;
            }

            AtlasJson data = JsonUtility.FromJson<AtlasJson>(json.text);
            if (data == null || data.frames == null || data.frames.Length != 11)
            {
                Debug.LogError("[CarArt] car_atlas.json is malformed.");
                return null;
            }

            tex.wrapMode = TextureWrapMode.Clamp;
            var art = new CarArt();
            art.StraightWidthPx = data.straightWidthPx;
            art.CarHeightPx = data.carHeightPx;

            for (int i = 0; i < data.frames.Length; i++)
            {
                FrameJson f = data.frames[i];
                int slot = SlotForKey(f.key);
                art.Body[slot] = Make(tex, f.body, f.bodyPivot, "Car_" + f.key);
                art.Brake[slot] = Make(tex, f.brake, f.brakePivot, "CarBrake_" + f.key);
                art.Left[slot] = f.left;
                art.Right[slot] = f.right;
                if (f.ground != null && f.ground.Length == 2)
                {
                    art.GroundCentre[slot] = (f.ground[0] + f.ground[1]) * 0.5f;
                    art.GroundWidth[slot] = f.ground[1] - f.ground[0];
                }
                else
                {
                    art.GroundCentre[slot] = (f.right - f.left) * 0.5f;
                    art.GroundWidth[slot] = f.left + f.right;
                }
                if (f.tyres != null && f.tyres.Length == 4)
                {
                    art.TyreLeft[slot] = new Vector2(f.tyres[0], f.tyres[1]);
                    art.TyreRight[slot] = new Vector2(f.tyres[2], f.tyres[3]);
                }
                else
                {
                    art.TyreLeft[slot] = new Vector2(-0.4f * data.straightWidthPx, 0f);
                    art.TyreRight[slot] = new Vector2(0.4f * data.straightWidthPx, 0f);
                }
            }
            for (int i = 0; i < 11; i++)
            {
                if (art.Body[i] == null || art.Brake[i] == null)
                {
                    Debug.LogError("[CarArt] frame slot " + i + " missing in car_atlas.json.");
                    return null;
                }
            }
            if (data.shadow != null && data.shadow.Length == 4)
                art.Shadow = Make(tex, data.shadow, new[] { 0.5f, 0.5f }, "CarShadow");
            return art;
        }

        private static Sprite Make(Texture2D tex, float[] r, float[] pivot, string name)
        {
            Sprite s = Sprite.Create(tex, new Rect(r[0], r[1], r[2], r[3]),
                                     new Vector2(pivot[0], pivot[1]), PixelsPerUnit, 0,
                                     SpriteMeshType.FullRect);
            s.name = name;
            return s;
        }
    }
}
