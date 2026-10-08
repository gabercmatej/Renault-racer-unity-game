using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace RenaultRacer.EditorTools
{
    /// <summary>
    /// The production Web build, plus the §19 size audit that plan item 9.6 asks for.
    ///
    /// The audit is here rather than in a spreadsheet because the number that matters is
    /// the **network payload** - the compressed bytes a phone on 4G actually pulls down -
    /// and that is not what the Build Report shows you. `BuildSummary.totalSize` counts
    /// uncompressed output, and the Build Report window attributes the .wasm to "code"
    /// without telling you what it compressed to. Measuring the files on disk is the only
    /// honest answer.
    ///
    ///   Unity.exe -batchmode -quit -projectPath . \
    ///             -executeMethod RenaultRacer.EditorTools.ProductionBuild.RunFromCommandLine
    /// </summary>
    public static class ProductionBuild
    {
        /// <summary>
        /// The one and only production output, always built from the current working
        /// tree and always cleaned first. Not named after a phase - see LanTestBuild.
        /// </summary>
        public const string Output = "Builds/WebCurrent";
        private const string Scene = "Assets/Scenes/Main.unity";
        private const string ReportPath = "Logs/build-size-audit.txt";

        // Plan section 19, network payload, Brotli.
        private const float IdealMb = 10f;
        private const float AcceptableMb = 12f;
        private const float InvestigateMb = 15f;

        [MenuItem("Renault Racer/Build Web — PRODUCTION (Brotli) + size audit", false, 22)]
        public static void RunFromMenu()
        {
            string report = Build();
            Debug.Log(report);
        }

        public static void RunFromCommandLine()
        {
            string report;
            try { report = Build(); }
            catch (Exception e)
            {
                Console.WriteLine("[ProductionBuild] threw: " + e);
                EditorApplication.Exit(2);
                return;
            }

            Console.WriteLine(report);
            try
            {
                Directory.CreateDirectory("Logs");
                File.WriteAllText(ReportPath, report);
                Console.WriteLine("[ProductionBuild] audit written to " + ReportPath);
            }
            catch (Exception e) { Console.WriteLine("could not write the audit: " + e.Message); }

            EditorApplication.Exit(report.Contains("BUILD FAILED") ? 1 : 0);
        }

        private static string Build()
        {
            // Identity FIRST, before anything else happens - see BuildStamp.Commit.
            BuildStamp.Begin("PRODUCTION");
            try { return BuildCore(); }
            finally { BuildStamp.End(); }
        }

        private static string BuildCore()
        {
            var sb = new StringBuilder(4096);

            if (!LanTestBuild.IsProduction())
            {
                // The LAN build restores these in a finally block, but if something ever
                // gets past that, shipping Gzip while the server sends `br` is a blank
                // page for every player. Refuse rather than produce a plausible wrong one.
                return "BUILD FAILED: player settings are not production. "
                     + LanTestBuild.DescribeSettings();
            }

            // PRODUCTION ART, BEFORE A PLAYER IS PRODUCED AT ALL.
            //
            // Every art loader in this project degrades to a plain generated shape when
            // its atlas is missing, and that fallback is silent and plausible - which is
            // exactly how a build could ship drawing a flat red circle where the brake
            // button belongs. A development build may look like that; a production one may
            // not, and the only place that can be guaranteed is here, before the bytes
            // exist. Refuse rather than produce a plausible wrong one.
            string artReport;
            if (!RenaultRacer.Core.ArtIntegrity.Verify(out artReport))
                return "BUILD FAILED: production art is incomplete, and a production build "
                     + "may not ship fallback shapes.\n" + artReport;
            sb.AppendLine("production art verified:");
            sb.Append(artReport);
            sb.AppendLine();

            // Nothing from a previous build may survive into this one.
            BuildStamp.CleanOutput(Output);

            var options = new BuildPlayerOptions
            {
                scenes = new[] { Scene },
                locationPathName = Output,
                target = BuildTarget.WebGL,
                targetGroup = BuildTargetGroup.WebGL,
                // Never None: see BuildStamp.Options for what the cache did.
                options = BuildStamp.Options
            };

            BuildReport report = BuildPipeline.BuildPlayer(options);
            BuildSummary summary = report.summary;

            sb.AppendLine("================= PRODUCTION BUILD + SIZE AUDIT =================");
            sb.AppendLine(BuildStamp.LastStamp);
            sb.AppendLine("result " + summary.result
                          + "   " + summary.totalTime.TotalSeconds.ToString("F0") + " s"
                          + "   errors " + summary.totalErrors
                          + "   warnings " + summary.totalWarnings);
            sb.AppendLine(LanTestBuild.DescribeSettings());
            sb.AppendLine();

            if (summary.result != BuildResult.Succeeded)
            {
                sb.AppendLine("BUILD FAILED");
                return sb.ToString();
            }

            BuildStamp.AssertStamped();
            string frameworkError = ServeFrameworkUncompressed(Output, sb);
            if (frameworkError != null)
            {
                sb.AppendLine("BUILD FAILED: " + frameworkError);
                return sb.ToString();
            }
            BuildStamp.WriteSidecar(Output, "PRODUCTION");
            AuditSize(Output, sb);
            return sb.ToString();
        }

        /// <summary>
        /// Ships the framework as a PLAIN .js file and leaves only .wasm/.data pre-compressed.
        ///
        /// Measured on the NEOSERV LiteSpeed host (2026-10-08): it compresses any response
        /// typed as JavaScript on the fly and ignores the no-gzip/no-brotli flags, so
        /// framework.js.br - already Brotli, served with Content-Encoding: br - came back
        /// Brotli- or gzip-compressed a SECOND time on most requests while still announcing a
        /// single "br". A browser decodes once and gets Brotli bytes as script. wasm/data are
        /// not types the host compresses and always arrived byte-exact; loader.js, a plain .js,
        /// was compressed correctly. So the framework goes out the way loader.js does: plain
        /// JavaScript, compressed (once, correctly) by the server.
        ///
        /// The file keeps a content-hash name (MD5 of the plain bytes) so the immutable cache
        /// rule still holds, and index.html is rewritten to point at it. Returns null, or why
        /// the build must not ship.
        /// </summary>
        public static string ServeFrameworkUncompressed(string buildPath, StringBuilder sb)
        {
            string dir = Path.Combine(buildPath, "Build");
            string index = Path.Combine(buildPath, "index.html");
            string[] found = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.framework.js.br") : new string[0];
            if (found.Length != 1) return "expected exactly one *.framework.js.br, found " + found.Length;
            if (!File.Exists(index)) return "index.html missing";

            byte[] plain;
            try
            {
                using (var input = File.OpenRead(found[0]))
                using (var brotli = new System.IO.Compression.BrotliStream(input, System.IO.Compression.CompressionMode.Decompress))
                using (var output = new MemoryStream())
                {
                    brotli.CopyTo(output);
                    plain = output.ToArray();
                }
            }
            catch (Exception e) { return "could not Brotli-decode the framework: " + e.Message; }

            string text = Encoding.UTF8.GetString(plain);
            if (plain.Length < 10000 || text.IndexOf("unityFramework", StringComparison.Ordinal) < 0)
                return "decoded framework does not look like the Unity framework (" + plain.Length + " bytes)";

            string hash;
            using (var md5 = System.Security.Cryptography.MD5.Create())
                hash = BitConverter.ToString(md5.ComputeHash(plain)).Replace("-", "").ToLowerInvariant();
            string oldName = Path.GetFileName(found[0]);
            string newName = hash + ".framework.js";

            string html = File.ReadAllText(index);
            string oldRef = "Build/" + oldName;
            int first = html.IndexOf(oldRef, StringComparison.Ordinal);
            if (first < 0 || html.IndexOf(oldRef, first + 1, StringComparison.Ordinal) >= 0)
                return "index.html must reference " + oldRef + " exactly once";

            File.WriteAllBytes(Path.Combine(dir, newName), plain);
            File.WriteAllText(index, html.Replace(oldRef, "Build/" + newName));
            File.Delete(found[0]);
            sb.AppendLine("framework served plain (host re-compresses JavaScript): " + oldName + " -> "
                          + newName + ", " + plain.Length.ToString("N0") + " bytes on disk (the host compresses it on the wire)");
            sb.AppendLine();
            return null;
        }

        /// <summary>
        /// What a phone downloads, file by file, against the plan 19 budget.
        /// </summary>
        public static void AuditSize(string buildPath, StringBuilder sb)
        {
            string data = Path.Combine(buildPath, "Build");
            if (!Directory.Exists(data))
            {
                sb.AppendLine("no Build/ folder under " + buildPath);
                return;
            }

            var files = new List<FileInfo>(new DirectoryInfo(data).GetFiles());
            files.Sort(delegate (FileInfo a, FileInfo b) { return b.Length.CompareTo(a.Length); });

            long payload = 0;
            sb.AppendLine("NETWORK PAYLOAD  (what the phone actually downloads)");
            sb.AppendLine(string.Format("  {0,-42} {1,12} {2,10}", "file", "bytes", "MB"));
            for (int i = 0; i < files.Count; i++)
            {
                payload += files[i].Length;
                sb.AppendLine(string.Format("  {0,-42} {1,12:N0} {2,10:F3}",
                    files[i].Name, files[i].Length, files[i].Length / 1024f / 1024f));
            }

            long everything = DirectorySize(new DirectoryInfo(buildPath));
            float payloadMb = payload / 1024f / 1024f;

            sb.AppendLine(string.Format("  {0,-42} {1,12:N0} {2,10:F3}", "TOTAL PAYLOAD", payload, payloadMb));
            sb.AppendLine(string.Format("  {0,-42} {1,12:N0} {2,10:F3}", "build folder incl. template",
                everything, everything / 1024f / 1024f));
            sb.AppendLine();

            string verdict = payloadMb <= IdealMb ? "IDEAL (<= 10 MB)"
                           : payloadMb <= AcceptableMb ? "ACCEPTABLE (<= 12 MB)"
                           : payloadMb <= InvestigateMb ? "INVESTIGATE (<= 15 MB)"
                           : "UNACCEPTABLE (> 15 MB)";
            sb.AppendLine("AGAINST PLAN 19: " + payloadMb.ToString("F2") + " MB = " + verdict);
            sb.AppendLine("  headroom to the 10 MB ideal ceiling: "
                          + (IdealMb - payloadMb).ToString("F2") + " MB");
            sb.AppendLine("  (Phase 10 has to fit the final art, audio and fonts inside that.)");
            sb.AppendLine("================================================================");
        }

        private static long DirectorySize(DirectoryInfo d)
        {
            long total = 0;
            FileInfo[] files = d.GetFiles();
            for (int i = 0; i < files.Length; i++) total += files[i].Length;
            DirectoryInfo[] subs = d.GetDirectories();
            for (int i = 0; i < subs.Length; i++) total += DirectorySize(subs[i]);
            return total;
        }
    }
}
