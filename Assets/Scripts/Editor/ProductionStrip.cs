using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;
using RenaultRacer.UI;
using RenaultRacer.Vehicle;

namespace RenaultRacer.EditorTools
{
    /// <summary>
    /// Plan item 9.6b: the debug profile switcher and the dev handling profiles must not
    /// ship. Only `Production.asset` goes in the build.
    ///
    /// WHY THIS IS A BUILD CALLBACK AND NOT AN #if
    ///
    /// `#if !DEVELOPMENT_BUILD` around the code that USES an asset does not remove the
    /// asset. Unity's build works from the serialized reference graph: if the scene's
    /// CarController holds four HandlingProfiles, all four are pulled into the data
    /// file whether or not a single line of code can still reach them. The reference is
    /// what has to go, not the code.
    ///
    /// <see cref="IProcessSceneWithReport"/> runs against the COPY of the scene that is
    /// being built, never the saved asset, so this cannot corrupt the project even if it
    /// throws - and it means the dev switcher stays fully usable in the Editor and in a
    /// development build, which is the point of having it.
    ///
    /// The four dev profiles are ~1 KB each, so this is not a size optimisation. It is
    /// about what SHIPS: a production build must not contain a key combination that
    /// silently swaps the car's handling out from under a player mid-race.
    /// </summary>
    public sealed class ProductionStrip : IProcessSceneWithReport
    {
        /// <summary>The one profile that ships. Everything else is a dev reference.</summary>
        public const string ShippingProfile = "Production";

        /// <summary>
        /// Set for the duration of a build that deliberately keeps the debug HUD and the
        /// dev profiles in a NON-development build - i.e. the LAN device test, whose
        /// whole job is to be production-representative while still being readable on a
        /// phone. Nothing else may set it, and LanTestBuild clears it in a finally block
        /// so a thrown build cannot leave the flag standing.
        /// </summary>
        public static bool KeepDevToolsForThisBuild;

        public int callbackOrder { get { return 0; } }

        public void OnProcessScene(Scene scene, BuildReport report)
        {
            // report is null when a scene is processed for Play Mode, which is not a
            // build at all and must be left completely alone.
            if (report == null) return;

            bool development = (report.summary.options & BuildOptions.Development) != 0;
            if (development || KeepDevToolsForThisBuild)
            {
                Debug.Log("[ProductionStrip] "
                          + (development ? "development build" : "device-test build")
                          + " - dev profiles and the debug HUD are kept.");
                return;
            }

            int profilesRemoved = 0, hudsRemoved = 0;
            List<GameObject> roots = new List<GameObject>(scene.GetRootGameObjects());
            for (int r = 0; r < roots.Count; r++)
            {
                CarController[] cars = roots[r].GetComponentsInChildren<CarController>(true);
                for (int i = 0; i < cars.Length; i++)
                    profilesRemoved += StripProfiles(cars[i]);

                DebugHud[] huds = roots[r].GetComponentsInChildren<DebugHud>(true);
                for (int i = 0; i < huds.Length; i++)
                {
                    Object.DestroyImmediate(huds[i]);
                    hudsRemoved++;
                }
            }

            Debug.Log("[ProductionStrip] production build: dropped " + profilesRemoved
                      + " dev handling profile reference(s) and " + hudsRemoved
                      + " debug HUD(s). Only " + ShippingProfile + " ships.");
        }

        /// <summary>
        /// Trim CarController.profiles down to the shipping profile alone, through
        /// SerializedObject because the field is private - which it should stay.
        /// </summary>
        private static int StripProfiles(CarController car)
        {
            var so = new SerializedObject(car);
            SerializedProperty list = so.FindProperty("profiles");
            if (list == null || !list.isArray) return 0;

            int keepIndex = -1;
            for (int i = 0; i < list.arraySize; i++)
            {
                Object p = list.GetArrayElementAtIndex(i).objectReferenceValue;
                if (p != null && p.name == ShippingProfile) { keepIndex = i; break; }
            }

            if (keepIndex < 0)
            {
                // Better a loud build than a quiet one that ships the wrong car.
                throw new BuildFailedException(
                    "[ProductionStrip] CarController has no '" + ShippingProfile
                    + "' handling profile assigned. A production build must ship it.");
            }

            int removed = list.arraySize - 1;
            Object keep = list.GetArrayElementAtIndex(keepIndex).objectReferenceValue;
            list.arraySize = 1;
            list.GetArrayElementAtIndex(0).objectReferenceValue = keep;
            so.ApplyModifiedPropertiesWithoutUndo();
            return removed;
        }
    }
}
