using System.Text;
using UnityEngine;

namespace RenaultRacer.Core
{
    /// <summary>
    /// WHAT PRODUCTION REQUIRES, in one list, checked at runtime and again at build time.
    ///
    /// Every art atlas in this project is loaded through Resources and every loader is
    /// written to DEGRADE: no atlas, no sprites, and the view draws its plain generated
    /// shape instead. That is the right behaviour for a half-built project and it is a
    /// trap for a finished one, because the fallback is silent and it is plausible. The
    /// Editor drew a flat red circle where the brake button should be for an entire
    /// session while the web build of the same commit drew the real one - the atlas simply
    /// had not been imported into that Editor, and nothing anywhere said so.
    ///
    /// So: the fallbacks stay, because a missing atlas should not be a crash, and this
    /// says out loud when one is being used. In the Editor and in a development build it
    /// is a Debug.LogError the moment the game starts. In a production build it is a
    /// BUILD FAILURE - ProductionBuild refuses to produce a player at all rather than ship
    /// one that quietly draws placeholder art.
    ///
    /// Adding an atlas means adding a line here. That is the point: the list is the
    /// contract, and there is one of it.
    /// </summary>
    public static class ArtIntegrity
    {
        /// <summary>One required Resources asset: where it lives and what it is for.</summary>
        public struct Required
        {
            public string path;      // Resources path, no extension
            public string what;      // human name, for the message
            public string tableKey;  // the json key its loader reads, or null for a non-atlas

            public Required(string path, string what, string tableKey)
            {
                this.path = path; this.what = what; this.tableKey = tableKey;
            }
        }

        /// <summary>
        /// THE LIST. Everything a production build must be able to load and parse.
        ///
        /// The paths are taken from the loaders themselves rather than retyped, so a path
        /// that moves cannot leave this list pointing at where it used to be.
        /// </summary>
        /// The third column is the json key that atlas's OWN loader reads, so the check is
        /// the one the game actually depends on rather than a guess that they all look
        /// alike. They do not: the car atlas is a table of "frames", everything else is a
        /// table of "sprites", and a validator that assumed otherwise reported perfectly
        /// good car art as broken the first time it was run.
        public static readonly Required[] Manifest =
        {
            new Required(RenaultRacer.UI.UiArt.AtlasPath,                 "the UI atlas",             "sprites"),
            new Required(RenaultRacer.UI.MinimapArt.AtlasPath,            "the minimap atlas",        "sprites"),
            new Required(RenaultRacer.Vehicle.CarArt.AtlasPath,           "the car atlas",            "frames"),
            new Required(RenaultRacer.Effects.FxArt.AtlasPath,            "the effects atlas",        "sprites"),
            new Required(RenaultRacer.Track.RoadsideSprites.AtlasPath,    "the roadside props atlas", "sprites"),
            new Required(RenaultRacer.Track.RoadsideSprites.MaterialPath, "the prop sprite material", null),
            new Required(RenaultRacer.Effects.PickupArt.SpritePath,       "the lightning pickup",     null),
            new Required(RenaultRacer.Effects.PickupArt.ArcsPath,         "the pickup arc burst",     null),
            new Required(RenaultRacer.Audio.AudioDirector.PickupClipPath, "the pickup zap sound",     null),
        };

        /// <summary>
        /// Check every entry. Returns true when production art is complete; on false,
        /// <paramref name="report"/> names each thing that is missing or unreadable.
        ///
        /// It LOADS rather than merely testing for existence, and it parses the json
        /// rather than merely loading it, because those are three different failures and
        /// only the last one is the interesting one: an atlas whose json has been
        /// truncated or regenerated against a different texture loads perfectly and draws
        /// nothing.
        /// </summary>
        public static bool Verify(out string report)
        {
            StringBuilder sb = new StringBuilder(512);
            bool ok = true;

            for (int i = 0; i < Manifest.Length; i++)
            {
                Required r = Manifest[i];

                Object asset = Resources.Load(r.path);
                if (asset == null)
                {
                    sb.AppendLine("  MISSING   " + r.what + "   Resources/" + r.path);
                    ok = false;
                    continue;
                }

                if (string.IsNullOrEmpty(r.tableKey))
                {
                    sb.AppendLine("  ok        " + r.what + "   Resources/" + r.path);
                    continue;
                }

                Texture2D tex = Resources.Load<Texture2D>(r.path);
                TextAsset json = Resources.Load<TextAsset>(r.path);

                if (tex == null)
                {
                    sb.AppendLine("  NO TEXTURE " + r.what + "   Resources/" + r.path);
                    ok = false;
                }
                else if (json == null)
                {
                    sb.AppendLine("  NO JSON   " + r.what + "   Resources/" + r.path
                                  + "   (the texture is there; its metadata is not)");
                    ok = false;
                }
                else if (string.IsNullOrEmpty(json.text)
                      || json.text.IndexOf("\"" + r.tableKey + "\"") < 0)
                {
                    sb.AppendLine("  BAD JSON  " + r.what + "   Resources/" + r.path
                                  + "   (" + json.text.Length + " chars, no \"" + r.tableKey
                                  + "\" table)");
                    ok = false;
                }
                else
                {
                    sb.AppendLine("  ok        " + r.what + "   "
                                  + tex.width + "x" + tex.height + ", "
                                  + json.text.Length + " chars of metadata");
                }
            }

            report = sb.ToString();
            return ok;
        }

        /// <summary>
        /// Called on start-up. In the Editor and in a development build a failure is a
        /// loud error; in a production player it can only be a warning, because by then
        /// the build gate has already had its say and shouting at the user achieves
        /// nothing.
        /// </summary>
        public static void VerifyAtStartup()
        {
            string report;
            if (Verify(out report)) return;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            Debug.LogError("[ArtIntegrity] PRODUCTION ART IS MISSING - what you are looking at is "
                           + "FALLBACK SHAPES, not the game.\n" + report
                           + "\nIn the Editor this is usually a stale import: reimport "
                           + "Assets/Art/Resources, or re-run the Tools/ArtPrep scripts.");
#else
            Debug.LogWarning("[ArtIntegrity] production art incomplete:\n" + report);
#endif
        }
    }
}
