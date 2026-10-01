using System;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using RenaultRacer.Core;
using RenaultRacer.Effects;
using RenaultRacer.Track;
using RenaultRacer.UI;
using RenaultRacer.Vehicle;

namespace RenaultRacer.EditorTools
{
    /// <summary>
    /// Idempotent scene setup for the components that are added in code rather than by
    /// hand. Main.unity is hand-wired, which is fine for a scene this small, but a
    /// component with seven serialized references is a lot of clicking to get wrong
    /// silently - and a missing reference in QualityDirector would show up as "the
    /// weak-tier settings do nothing", which is close to invisible.
    ///
    /// Run it from the menu, or from a command line:
    ///
    ///   Unity.exe -batchmode -quit -projectPath . \
    ///             -executeMethod RenaultRacer.EditorTools.SceneWiring.RunFromCommandLine
    ///
    /// Safe to run repeatedly: it only ever fills in what is missing.
    /// </summary>
    public static class SceneWiring
    {
        private const string ScenePath = "Assets/Scenes/Main.unity";

        [MenuItem("Renault Racer/Wire up the scene (idempotent)", false, 60)]
        public static void RunFromMenu()
        {
            Scene scene = EditorSceneManager.GetActiveScene();
            if (scene.path != ScenePath)
                scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            string report = Wire(scene);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log(report);
        }

        public static void RunFromCommandLine()
        {
            try
            {
                Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
                string report = Wire(scene);
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
                Console.WriteLine(report);
                EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Console.WriteLine("[SceneWiring] threw: " + e);
                EditorApplication.Exit(2);
            }
        }

        private static string Wire(Scene scene)
        {
            var sb = new System.Text.StringBuilder(1024);
            sb.AppendLine("[SceneWiring] " + scene.path);

            QualityDirector quality = Find<QualityDirector>();
            if (quality == null)
            {
                // Onto [Systems], next to Bootstrap and GameDirector, because that is
                // where the things that govern the whole session already live.
                Bootstrap boot = Find<Bootstrap>();
                GameObject host = boot != null ? boot.gameObject : new GameObject("[Systems]");
                quality = host.AddComponent<QualityDirector>();
                sb.AppendLine("  added QualityDirector to " + host.name);
            }

            PerfProbe perf = Find<PerfProbe>();
            if (perf == null)
            {
                Bootstrap boot2 = Find<Bootstrap>();
                GameObject host2 = boot2 != null ? boot2.gameObject : quality.gameObject;
                perf = host2.AddComponent<PerfProbe>();
                sb.AppendLine("  added PerfProbe to " + host2.name);
            }

            var so = new SerializedObject(quality);
            int filled = 0;
            filled += Assign(so, "director", Find<GameDirector>(), sb);
            filled += Assign(so, "roadRenderer", Find<RoadRenderer>(), sb);
            filled += Assign(so, "feedbackFX", Find<FeedbackFX>(), sb);
            filled += Assign(so, "skidMarks", Find<SkidMarks>(), sb);
            filled += Assign(so, "roadsideSprites", Find<RoadsideSprites>(), sb);
            filled += Assign(so, "car", Find<CarController>(), sb);
            // speedLines is deliberately left alone: HudView builds it at runtime, so
            // there is nothing in the scene to point at. QualityDirector resolves it.
            so.ApplyModifiedPropertiesWithoutUndo();

            // The debug HUD is where these two are actually read, on a phone, in a
            // build. Wiring them is the whole point of measuring them.
            DebugHud hud = Find<DebugHud>();
            if (hud != null)
            {
                var hudSo = new SerializedObject(hud);
                filled += Assign(hudSo, "perf", perf, sb);
                filled += Assign(hudSo, "quality", quality, sb);
                filled += Assign(hudSo, "roadRenderer", Find<RoadRenderer>(), sb);
                hudSo.ApplyModifiedPropertiesWithoutUndo();
            }
            else sb.AppendLine("  no DebugHud in the scene - nothing will display the budgets");

            sb.AppendLine("  " + filled + " reference(s) filled in; "
                          + "speedLines is resolved at runtime by design");
            return sb.ToString();
        }

        private static int Assign(SerializedObject so, string field, UnityEngine.Object value,
                                  System.Text.StringBuilder sb)
        {
            SerializedProperty p = so.FindProperty(field);
            if (p == null) { sb.AppendLine("  !! no field " + field); return 0; }
            if (p.objectReferenceValue != null) return 0;
            if (value == null) { sb.AppendLine("  !! nothing in the scene for " + field); return 0; }
            p.objectReferenceValue = value;
            sb.AppendLine("  " + field + " -> " + value.name);
            return 1;
        }

        private static T Find<T>() where T : UnityEngine.Object
        {
            return UnityEngine.Object.FindAnyObjectByType<T>(FindObjectsInactive.Include);
        }
    }
}
