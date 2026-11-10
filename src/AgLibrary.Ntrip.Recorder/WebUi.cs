using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;

namespace AgLibrary.Ntrip.Recorder
{
    /// <summary>
    /// A small local web page to test several casters one after another. It listens on
    /// localhost only. The API needs a custom header, which a web page on another site cannot
    /// send without a CORS preflight that this server never answers, so only this page can use it.
    /// </summary>
    internal static class WebUi
    {
        private const string RecordingsFolder = "recordings";

        public static int Run(int port, bool openBrowser)
        {
            var listener = new HttpListener();
            string prefix = "http://localhost:" + port + "/";
            listener.Prefixes.Add(prefix);
            try { listener.Start(); }
            catch (HttpListenerException ex)
            {
                Console.WriteLine("Cannot listen on " + prefix + ": " + ex.Message);
                return 1;
            }

            Console.WriteLine("NTRIP caster tester: " + prefix);
            Console.WriteLine("Recordings are saved in " + Path.GetFullPath(RecordingsFolder) + " (not for git).");
            Console.WriteLine("Press Ctrl+C to stop.");
            if (openBrowser)
            {
                try { Process.Start(new ProcessStartInfo(prefix) { UseShellExecute = true }); } catch (Exception) { }
            }

            while (true)
            {
                HttpListenerContext ctx = listener.GetContext();
                try { Handle(ctx); }
                catch (Exception ex)
                {
                    Respond(ctx, 500, "text/plain", ex.Message);
                }
            }
        }

        private static void Handle(HttpListenerContext ctx)
        {
            string path = ctx.Request.Url.AbsolutePath;
            if (ctx.Request.HttpMethod == "GET" && (path == "/" || path == "/index.html"))
            {
                Respond(ctx, 200, "text/html; charset=utf-8", LoadPage());
                return;
            }
            if (ctx.Request.HttpMethod == "POST" && path == "/api/run")
            {
                if (ctx.Request.Headers["X-Recorder"] != "1") { Respond(ctx, 403, "text/plain", "forbidden"); return; }
                string body;
                using (var rd = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) body = rd.ReadToEnd();
                Respond(ctx, 200, "application/json", RunRequest(body));
                return;
            }
            Respond(ctx, 404, "text/plain", "not found");
        }

        private static string RunRequest(string json)
        {
            var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var req = js.Deserialize<Dictionary<string, object>>(json);
            var o = new ProbeOptions
            {
                Host = Str(req, "host").Trim(),
                Port = (int)Num(req, "port", 2101),
                Mount = Str(req, "mount").Trim(),
                User = Str(req, "user"),
                Pass = Str(req, "pass"),
                Version = (int)Num(req, "version", 2),
                Seconds = Math.Max(2, Math.Min(120, Num(req, "seconds", 15))),
            };
            if (o.Host.Length == 0) throw new ArgumentException("host is empty");
            string gga = Str(req, "gga").Trim();
            if (gga.Length > 0)
            {
                string[] p = gga.Split(',');
                if (p.Length < 2) throw new ArgumentException("GGA must be lat,lon[,alt]");
                o.Gga = CasterProbe.BuildGga(
                    double.Parse(p[0], CultureInfo.InvariantCulture),
                    double.Parse(p[1], CultureInfo.InvariantCulture),
                    p.Length > 2 ? double.Parse(p[2], CultureInfo.InvariantCulture) : 0);
            }

            ProbeResult r = CasterProbe.Run(o);

            string file = "";
            if (Num(req, "save", 0) > 0)
            {
                Directory.CreateDirectory(RecordingsFolder);
                file = Path.Combine(RecordingsFolder, Safe(o.Host + "_" + (o.Mount.Length > 0 ? o.Mount : "sourcetable")
                    + "_v" + o.Version + "_" + DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture)) + ".rec.txt");
                File.WriteAllText(file, r.Log, new UTF8Encoding(false));
            }

            var types = new List<object>();
            foreach (var kv in r.Types)
                types.Add(new Dictionary<string, object> { { "type", kv.Key }, { "count", kv.Value }, { "what", RtcmMessages.Describe(kv.Key) } });

            var result = new Dictionary<string, object>
            {
                { "category", r.Category }, { "message", r.Message }, { "firstLine", r.FirstLine },
                { "passed", r.Passed }, { "chunked", r.Chunked },
                { "connectMs", r.ConnectMs }, { "headerMs", r.HeaderMs }, { "firstRtcmMs", r.FirstRtcmMs },
                { "bytes", r.Bytes }, { "frames", r.Frames }, { "crcFailures", r.ChecksumFailures },
                { "skipped", r.BytesSkipped }, { "types", types }, { "mountpoints", r.Mountpoints },
                { "log", r.Log }, { "file", file == "" ? "" : Path.GetFullPath(file) },
            };
            return js.Serialize(result);
        }

        private static string Str(Dictionary<string, object> d, string key) =>
            d.ContainsKey(key) && d[key] != null ? Convert.ToString(d[key], CultureInfo.InvariantCulture) : "";

        private static double Num(Dictionary<string, object> d, string key, double fallback)
        {
            if (!d.ContainsKey(key) || d[key] == null) return fallback;
            double v;
            return double.TryParse(Convert.ToString(d[key], CultureInfo.InvariantCulture), NumberStyles.Float,
                CultureInfo.InvariantCulture, out v) ? v : fallback;
        }

        private static string Safe(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }

        private static string LoadPage()
        {
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("ui.html"))
            using (var rd = new StreamReader(s, Encoding.UTF8))
                return rd.ReadToEnd();
        }

        private static void Respond(HttpListenerContext ctx, int status, string type, string text)
        {
            byte[] b = Encoding.UTF8.GetBytes(text);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = type;
            ctx.Response.ContentLength64 = b.Length;
            ctx.Response.OutputStream.Write(b, 0, b.Length);
            ctx.Response.OutputStream.Close();
        }
    }
}
