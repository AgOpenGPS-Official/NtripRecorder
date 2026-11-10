using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace AgLibrary.Ntrip.Recorder
{
    internal sealed class ProbeOptions
    {
        public string Host = "";
        public int Port = 2101;
        /// <summary>Empty requests the sourcetable ("GET /").</summary>
        public string Mount = "";
        public string User = "";
        public string Pass = "";
        public int Version = 2;
        /// <summary>A complete GGA sentence, or null.</summary>
        public string Gga;
        public double Seconds = 15;
    }

    internal sealed class ProbeResult
    {
        /// <summary>Accepted, a sourcetable, or one of the NtripCategory names.</summary>
        public string Category = "";
        public string Message = "";
        public string FirstLine = "";
        public bool Chunked;
        public bool Passed;
        public double ConnectMs = -1, HeaderMs = -1, FirstRtcmMs = -1;
        public long Bytes, Frames, ChecksumFailures, BytesSkipped;
        public SortedDictionary<int, long> Types = new SortedDictionary<int, long>();
        public List<string> Mountpoints = new List<string>();
        /// <summary>The recording text, credentials removed.</summary>
        public string Log = "";
    }

    /// <summary>One attempt against one caster, run synchronously. Used by the CLI and the web UI.</summary>
    internal static class CasterProbe
    {
        private const int BodyHexLimit = 4096;
        private const int HeaderLimit = 8192;
        private const int MinSecretLength = 4;

        public static string BuildGga(double lat, double lon, double alt) =>
            GgaBuilder.FromManual(DateTime.UtcNow, lat, lon, alt);

        public static ProbeResult Run(ProbeOptions o)
        {
            var r = new ProbeResult();
            var log = new StringBuilder();
            bool sourcetable = o.Mount.Length == 0;
            string request = BuildRequest(o);

            log.AppendLine("# NTRIP recording (credentials removed)");
            log.AppendLine("recorded: " + DateTime.UtcNow.ToString("u", CultureInfo.InvariantCulture));
            log.AppendLine("caster: " + o.Host + ":" + o.Port + "  mount: " + (sourcetable ? "(sourcetable)" : o.Mount)
                + "  version: " + o.Version + "  gga: " + (o.Gga != null ? "yes" : "no"));

            log.AppendLine("login: " + DescribeSecret("user", o.User) + "; " + DescribeSecret("password", o.Pass));

            var sw = Stopwatch.StartNew();
            var raw = new List<byte>();
            var header = new List<byte>();
            var framer = new Rtcm3Framer();
            ChunkedDecoder chunks = null;
            var table = new StringBuilder();
            NtripOutcome outcome = null;
            bool headerDone = false;

            try
            {
                using (var client = new TcpClient(AddressFamily.InterNetwork))
                {
                    var connect = client.ConnectAsync(o.Host, o.Port);
                    if (!connect.Wait(10000))
                    {
                        Event(log, sw, "connect timeout (10 s)");
                        r.Category = NtripCategory.CasterUnreachable.ToString();
                        r.Message = "Connect timed out after 10 s";
                        return Finish(r, log, raw, o);
                    }
                    r.ConnectMs = sw.Elapsed.TotalMilliseconds;
                    Event(log, sw, "connected");

                    NetworkStream stream = client.GetStream();
                    byte[] req = Encoding.ASCII.GetBytes(request);
                    stream.Write(req, 0, req.Length);
                    Event(log, sw, "request sent");
                    log.AppendLine("--- request ---");
                    log.Append(Redact(request, o));
                    log.AppendLine("--- events ---");

                    var buf = new byte[8192];
                    stream.ReadTimeout = 500;
                    while (sw.Elapsed.TotalSeconds < o.Seconds)
                    {
                        int n;
                        try { n = stream.Read(buf, 0, buf.Length); }
                        catch (IOException) { n = -1; }
                        if (n == 0) { Event(log, sw, "caster closed the connection"); break; }

                        int headerLength = -1, bodyOffset = -1;
                        if (n < 0)
                        {
                            // Read timeout. RTKLIB sends "ICY 200 OK" alone and then waits for the next epoch.
                            if (headerDone || !NtripHeaderScanner.IsIcyStatusLineOnly(header.ToArray())) continue;
                            headerLength = bodyOffset = header.Count;
                            Event(log, sw, "ICY status line without a blank line, taken as the complete header");
                        }
                        else
                        {
                            r.Bytes += n;
                            Event(log, sw, "received " + n + " bytes");
                            for (int i = 0; i < n && raw.Count < BodyHexLimit; i++) raw.Add(buf[i]);

                            if (!headerDone)
                            {
                                for (int i = 0; i < n; i++) header.Add(buf[i]);
                                if (!NtripHeaderScanner.TryFindEnd(header.ToArray(), out headerLength, out bodyOffset))
                                {
                                    if (header.Count > HeaderLimit)
                                    {
                                        r.Category = NtripCategory.NotUnderstood.ToString();
                                        r.Message = "No end of header after " + HeaderLimit + " bytes";
                                        break;
                                    }
                                    continue;
                                }
                            }
                        }

                        if (!headerDone)
                        {
                            headerDone = true;
                            r.HeaderMs = sw.Elapsed.TotalMilliseconds;
                            byte[] all = header.ToArray();
                            string headerText = Encoding.ASCII.GetString(all, 0, headerLength);
                            int rest = all.Length - bodyOffset;
                            byte[] firstBody = new byte[rest];
                            Array.Copy(all, bodyOffset, firstBody, 0, rest);
                            outcome = ResponseClassifier.Classify(headerText, firstBody);
                            r.FirstLine = outcome.FirstLine;
                            r.Chunked = outcome.Chunked;
                            Event(log, sw, "header complete: " + outcome.FirstLine);

                            if (sourcetable && outcome.Category != NtripCategory.NotUnderstood
                                && outcome.Category != NtripCategory.LoginRejected)
                            {
                                // A sourcetable request is answered with SOURCETABLE 200 OK (or 200 + gnss/sourcetable).
                                r.Category = "Sourcetable";
                            }
                            else if (!outcome.IsAccepted)
                            {
                                r.Category = outcome.Category.ToString();
                                r.Message = outcome.Message;
                                if (rest == 0) break;
                            }
                            else
                            {
                                r.Category = "Accepted";
                                if (outcome.Chunked) chunks = new ChunkedDecoder();
                                if (o.Gga != null && o.Version == 1)
                                {
                                    byte[] g = Encoding.ASCII.GetBytes(o.Gga);
                                    stream.Write(g, 0, g.Length);
                                    Event(log, sw, "GGA sent");
                                }
                            }
                            // Fall through with the bytes after the header.
                            var body = new byte[rest];
                            Array.Copy(firstBody, body, rest);
                            Consume(body, body.Length, sw, r, framer, chunks, table, sourcetable, log);
                            if (r.Category != "Accepted" && r.Category != "Sourcetable") break;
                            continue;
                        }

                        Consume(buf, n, sw, r, framer, chunks, table, sourcetable, log);
                        if (sourcetable && table.ToString().Contains("ENDSOURCETABLE")) break;
                    }
                    Event(log, sw, "done, " + r.Bytes + " bytes in total");
                }
            }
            catch (AggregateException ex)
            {
                Fail(r, log, sw, ex.GetBaseException(), o);
            }
            catch (Exception ex)
            {
                Fail(r, log, sw, ex, o);
            }

            if (r.Category.Length == 0)
            {
                r.Category = headerDone ? "Accepted" : NtripCategory.CasterUnreachable.ToString();
                if (!headerDone) r.Message = "No answer from the caster";
            }
            if (r.Category == "Accepted" && r.Frames == 0)
            {
                r.Category = NtripCategory.NoCorrections.ToString();
                r.Message = "Accepted, but no RTCM frame arrived (VRS/NEAR mountpoints need a position)";
            }
            if (sourcetable && r.Category == "Sourcetable")
            {
                foreach (string line in table.ToString().Replace("\r", "").Split('\n'))
                {
                    if (!line.StartsWith("STR;")) continue;
                    string[] f = line.Split(';');
                    if (f.Length > 1) r.Mountpoints.Add(f[1] + (f.Length > 11 && f[11] == "1" ? " (needs GGA)" : ""));
                }
                r.Message = r.Mountpoints.Count + " mountpoints";
            }
            r.Passed = (r.Category == "Accepted" && r.Frames > 0) || (sourcetable && r.Category == "Sourcetable");
            r.ChecksumFailures = framer.ChecksumFailures;
            r.BytesSkipped = framer.BytesSkipped;
            return Finish(r, log, raw, o);
        }

        private static void Consume(byte[] data, int n, Stopwatch sw, ProbeResult r, Rtcm3Framer framer,
            ChunkedDecoder chunks, StringBuilder table, bool sourcetable, StringBuilder log)
        {
            if (n == 0) return;
            if (sourcetable)
            {
                table.Append(Encoding.ASCII.GetString(data, 0, n));
                return;
            }
            if (r.Category != "Accepted") return;
            Rtcm3Framer.MessageHandler onMessage = (type, message) =>
            {
                if (r.Frames == 0)
                {
                    r.FirstRtcmMs = sw.Elapsed.TotalMilliseconds;
                    Event(log, sw, "first RTCM frame (type " + type + ")");
                }
                r.Frames++;
                long c;
                r.Types.TryGetValue(type, out c);
                r.Types[type] = c + 1;
            };
            if (chunks != null && !chunks.IsBroken)
                chunks.Feed(new ReadOnlySpan<byte>(data, 0, n), d => framer.Feed(d, onMessage));
            else
                framer.Feed(new ReadOnlySpan<byte>(data, 0, n), onMessage);
        }

        private static void Fail(ProbeResult r, StringBuilder log, Stopwatch sw, Exception ex, ProbeOptions o)
        {
            Event(log, sw, "error: " + ex.GetType().Name + ": " + ex.Message);
            if (r.Category.Length == 0)
            {
                // The exception text is localised; the error code is not.
                var se = ex as SocketException;
                if (se != null)
                {
                    NtripOutcome failure = ConnectionErrors.Classify(se.SocketErrorCode, o.Host, o.Port);
                    r.Category = failure.Category.ToString();
                    r.Message = failure.Message;
                }
                else
                {
                    r.Category = NtripCategory.CasterUnreachable.ToString();
                    r.Message = ex.Message;
                }
            }
        }

        private static ProbeResult Finish(ProbeResult r, StringBuilder log, List<byte> raw, ProbeOptions o)
        {
            byte[] bytes = raw.ToArray();
            BlankOut(bytes, o.User);
            BlankOut(bytes, o.Pass);
            BlankOut(bytes, Convert.ToBase64String(Encoding.UTF8.GetBytes(o.User + ":" + o.Pass)));
            log.AppendLine("--- result ---");
            log.AppendLine(r.Category + (r.Message.Length > 0 ? ": " + r.Message : "") + "  frames=" + r.Frames
                + "  checksum failures=" + r.ChecksumFailures + "  skipped=" + r.BytesSkipped);
            log.AppendLine("--- first " + bytes.Length + " bytes (hex) ---");
            for (int i = 0; i < bytes.Length; i += 32)
            {
                int n = Math.Min(32, bytes.Length - i);
                for (int j = 0; j < n; j++) log.Append(bytes[i + j].ToString("x2", CultureInfo.InvariantCulture));
                log.AppendLine();
            }
            log.AppendLine("--- first bytes as text ---");
            int t = Math.Min(bytes.Length, 1024);
            var c = new char[t];
            for (int i = 0; i < t; i++)
                c[i] = bytes[i] == '\n' || bytes[i] == '\r' || (bytes[i] >= 32 && bytes[i] < 127) ? (char)bytes[i] : '.';
            log.AppendLine(new string(c));
            r.Log = Redact(log.ToString(), o);
            return r;
        }

        /// <summary>The shape of a login part without its value: the usual reasons for a login that
        /// "is right" but is refused are a space or a line break pasted along, or non-ASCII characters.</summary>
        private static string DescribeSecret(string name, string value)
        {
            if (value.Length == 0) return name + " empty";
            var flags = new List<string>();
            if (value != value.Trim()) flags.Add("LEADING/TRAILING WHITESPACE");
            if (value.IndexOfAny(new[] { '\r', '\n', '\t' }) >= 0) flags.Add("CONTROL CHARACTER");
            if (value.IndexOf(' ') >= 0) flags.Add("contains space");
            if (value.IndexOf(':') >= 0)
                flags.Add(name == "user" ? "contains ':' (not allowed in a user name)" : "contains ':' (casters that split on every ':' refuse it)");
            foreach (char c in value)
                if (c > 126) { flags.Add("non-ASCII (sent as UTF-8)"); break; }
            return name + " " + value.Length + " chars" + (flags.Count > 0 ? " [" + string.Join(", ", flags) + "]" : "");
        }

        public static string BuildRequest(ProbeOptions o)
        {
            var sb = new StringBuilder();
            string path = "/" + Uri.EscapeDataString(o.Mount);
            sb.Append("GET ").Append(path).Append(o.Version == 2 ? " HTTP/1.1\r\n" : " HTTP/1.0\r\n");
            if (o.Version == 2)
            {
                sb.Append("Host: ").Append(o.Host);
                if (o.Port != 80) sb.Append(':').Append(o.Port.ToString(CultureInfo.InvariantCulture));
                sb.Append("\r\nNtrip-Version: Ntrip/2.0\r\n");
            }
            sb.Append("User-Agent: NTRIP AgOpenGPS-Recorder/1.0\r\n");
            if (o.User.Length > 0 || o.Pass.Length > 0)
                sb.Append("Authorization: Basic ")
                  .Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(o.User + ":" + o.Pass))).Append("\r\n");
            if (o.Version == 2 && o.Gga != null) sb.Append("Ntrip-GGA: ").Append(o.Gga.TrimEnd('\r', '\n')).Append("\r\n");
            if (o.Version == 2) sb.Append("Connection: close\r\n");
            sb.Append("\r\n");
            return sb.ToString();
        }

        private static void Event(StringBuilder log, Stopwatch sw, string text)
        {
            log.AppendLine("+" + sw.Elapsed.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture) + " s  " + text);
        }

        public static string Redact(string text, ProbeOptions o)
        {
            if (o.User.Length > 0 || o.Pass.Length > 0)
                text = text.Replace(Convert.ToBase64String(Encoding.UTF8.GetBytes(o.User + ":" + o.Pass)), "<removed>");
            // Very short secrets are not replaced as text: "p" would mangle every word in the log.
            // The Basic auth value above is always removed.
            if (o.Pass.Length >= MinSecretLength) text = text.Replace(o.Pass, "<password>");
            if (o.User.Length >= MinSecretLength) text = text.Replace(o.User, "<user>");
            return text;
        }

        /// <summary>Overwrite every occurrence of <paramref name="secret"/> in the data with 'x'.</summary>
        private static void BlankOut(byte[] data, string secret)
        {
            if (secret.Length < MinSecretLength) return;
            byte[] s = Encoding.UTF8.GetBytes(secret);
            for (int i = 0; i + s.Length <= data.Length; i++)
            {
                int j = 0;
                while (j < s.Length && data[i + j] == s[j]) j++;
                if (j == s.Length)
                {
                    for (int k = 0; k < s.Length; k++) data[i + k] = (byte)'x';
                    i += s.Length - 1;
                }
            }
        }
    }
}
