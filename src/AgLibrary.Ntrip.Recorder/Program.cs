using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace AgLibrary.Ntrip.Recorder
{
    /// <summary>
    /// Records what an NTRIP caster answers, so the answer can be turned into a synthetic
    /// fixture for the fake caster tests, and tests several casters from a small local web page.
    /// Credentials are removed from recordings. Recordings stay local; do not commit them.
    ///
    /// Command line: Recorder --host H [--port 2101] [--mount M] [--user U] [--pass P]
    ///   [--version 1|2] [--gga lat,lon[,alt]] [--seconds 20] [--out file.rec.txt]
    ///   Without --mount the sourcetable is requested.
    /// Web page:     Recorder --ui [--port 8765]
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            var opt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--")) return Usage();
                string key = args[i].Substring(2);
                if (key == "ui" || key == "no-browser") { opt[key] = ""; continue; }
                if (i + 1 >= args.Length) return Usage();
                opt[key] = args[++i];
            }

            if (opt.ContainsKey("ui"))
            {
                int uiPort = opt.ContainsKey("port") ? int.Parse(opt["port"], CultureInfo.InvariantCulture) : 8765;
                return WebUi.Run(uiPort, !opt.ContainsKey("no-browser"));
            }
            if (!opt.ContainsKey("host")) return Usage();

            var o = new ProbeOptions
            {
                Host = opt["host"],
                Port = opt.ContainsKey("port") ? int.Parse(opt["port"], CultureInfo.InvariantCulture) : 2101,
                Mount = opt.ContainsKey("mount") ? opt["mount"] : "",
                User = opt.ContainsKey("user") ? opt["user"] : "",
                Pass = opt.ContainsKey("pass") ? opt["pass"] : "",
                Version = opt.ContainsKey("version") ? int.Parse(opt["version"], CultureInfo.InvariantCulture) : 2,
                Seconds = opt.ContainsKey("seconds") ? double.Parse(opt["seconds"], CultureInfo.InvariantCulture) : 20,
            };
            if (opt.ContainsKey("gga"))
            {
                string[] p = opt["gga"].Split(',');
                o.Gga = CasterProbe.BuildGga(
                    double.Parse(p[0], CultureInfo.InvariantCulture),
                    double.Parse(p[1], CultureInfo.InvariantCulture),
                    p.Length > 2 ? double.Parse(p[2], CultureInfo.InvariantCulture) : 0);
            }

            ProbeResult r = CasterProbe.Run(o);
            string outPath = opt.ContainsKey("out") ? opt["out"]
                : o.Host + "_" + (o.Mount.Length > 0 ? o.Mount : "sourcetable") + "_v" + o.Version + ".rec.txt";
            File.WriteAllText(outPath, r.Log, new UTF8Encoding(false));
            Console.WriteLine(r.Category + (r.Message.Length > 0 ? ": " + r.Message : ""));
            Console.WriteLine("Saved " + Path.GetFullPath(outPath));
            Console.WriteLine("Check the file for anything personal before sharing it. Do not commit it.");
            return 0;
        }

        private static int Usage()
        {
            Console.WriteLine("Usage: Recorder --host H [--port 2101] [--mount M] [--user U] [--pass P]");
            Console.WriteLine("                [--version 1|2] [--gga lat,lon[,alt]] [--seconds 20] [--out file]");
            Console.WriteLine("       Recorder --ui [--port 8765] [--no-browser]  (local web page for testing several casters)");
            Console.WriteLine("Without --mount the sourcetable is requested.");
            return 1;
        }
    }
}
