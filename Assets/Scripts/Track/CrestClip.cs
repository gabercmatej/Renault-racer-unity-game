using UnityEngine;

namespace RenaultRacer.Track
{
    /// <summary>
    /// Packs a crest line into a PropSprite vertex colour. r stays fog and a stays
    /// opacity; g and b carry the screen row below which the sprite is hidden, as a
    /// 16-bit fraction of the screen height, INVERTED - so plain white (g = b = 1), which
    /// is what any sprite on this material gets by default, means "nothing is clipped".
    /// Vertex colours still batch, so the clip costs no draw calls.
    /// </summary>
    public static class CrestClip
    {
        /// <param name="clipWorldY">World y of the crest line; -infinity for no clip.</param>
        public static Color Color(float fog, float alpha, float clipWorldY)
        {
            float frac = 0f;
            Camera cam = Camera.main;
            if (cam != null && !float.IsNegativeInfinity(clipWorldY))
            {
                float ortho = cam.orthographicSize;
                frac = Mathf.Clamp01((clipWorldY - (cam.transform.position.y - ortho)) / (2f * ortho));
            }
            int c16 = Mathf.Clamp(Mathf.RoundToInt((1f - frac) * 65535f), 0, 65535);
            return new Color(fog, ((c16 >> 8) + 0.25f) / 255f, ((c16 & 255) + 0.25f) / 255f, alpha);
        }
    }
}
