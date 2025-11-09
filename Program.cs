using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

class Program
{
    private static readonly Regex RazorCommentRx =
        new Regex(@"@\*.*?\*@", RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex RouteRx =
        new Regex(@"@page\s*""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HasAuthorizeRx =
        new Regex(@"(@attribute\s*\[\s*Authorize(?:\s*\([^)]*\))?\s*\]|\[\s*Authorize(?:\s*\([^)]*\))?\s*\]|@attribute\s+Authorize)",
                  RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex HasAllowAnonymousRx =
        new Regex(@"(@attribute\s*\[\s*AllowAnonymous\s*\]|\[\s*AllowAnonymous\s*\])",
                  RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex AuthorizeBlocksRx =
        new Regex(@"(@attribute\s*)?\[\s*Authorize(?:\s*\((?<args>[^)]*)\))?\s*\]",
                  RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex RolesArgRx =
        new Regex(@"\b(?:Role|Roles)\s*=\s*(?:""([^""]+)""|'([^']+)')",
                  RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HrefRx =
        new Regex(@"<\s*a\b[^>]*\bhref\s*=\s*(?:""(?<url>[^""]*)""|'(?<url>[^']*)')",
                  RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NavigateToRx =
        new Regex(@"\bNavigateTo\s*\(\s*(?:""(?<url>[^""]*)""|'(?<url>[^']*)')",
                  RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] LinkScanExtensions = new[] { ".razor", ".cs", ".cshtml", ".html" };

    static int Main(string[] args)
    {
        try
        {
            var root = (args.Length > 0 && !string.IsNullOrWhiteSpace(args[0])) ? args[0] : Directory.GetCurrentDirectory();
            var output = (args.Length > 1 && !string.IsNullOrWhiteSpace(args[1])) ? args[1] : "routes.md";
            var exportCsv = args.Any(a => a.Equals("--csv", StringComparison.OrdinalIgnoreCase));

            if (!Directory.Exists(root))
            {
                Console.Error.WriteLine("Root folder not found: " + root);
                return 2;
            }

            // Pass 1: Routes + Auth
            var razorFiles = Directory.GetFiles(root, "*.razor", SearchOption.AllDirectories);
            var routeRows = new List<RouteRow>();
            var globalRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in razorFiles)
            {
                var content = SafeReadAllText(file);
                if (content == null) continue;

                var routes = GetRoutesWithActive(content);
                if (routes.Count == 0) continue;

                var auth = GetAuthorizeInfo(content);
                foreach (var role in auth.Roles) globalRoles.Add(role);

                foreach (var r in routes)
                {
                    routeRows.Add(new RouteRow
                    {
                        Route = r.Route,
                        RouteActive = r.Active,
                        Authorized = auth.Authorized,
                        Roles = auth.Roles,
                        File = file
                    });
                }
            }

            var activeRoutePatterns = BuildActiveRouteRegexes(routeRows);

            // Pass 2: Link scan
            var linkRows = new List<LinkRow>();
            var allFiles = Directory.GetFiles(root, "*.*", SearchOption.AllDirectories)
                .Where(p => LinkScanExtensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
                .ToArray();

            foreach (var file in allFiles)
            {
                string[] lines;
                try { lines = File.ReadAllLines(file); } catch { continue; }

                for (int i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];

                    foreach (Match m in HrefRx.Matches(line))
                    {
                        var url = m.Groups["url"].Value.Trim();
                        var scheme = GetScheme(url);
                        var classified = ClassifyDestination(url, activeRoutePatterns);

                        linkRows.Add(new LinkRow
                        {
                            File = file,
                            LineNumber = i + 1,
                            DestinationRaw = url,
                            DestinationDisplay = "<u><em>" + EscapePipes(url) + "</em></u>", // underline+italic
                            Method = "href",
                            Scheme = scheme,
                            MatchHtml = classified.MatchHtml
                        });
                    }


                    foreach (Match m in NavigateToRx.Matches(line))
                    {
                        var url = m.Groups["url"].Value.Trim();
                        var scheme = GetScheme(url);
                        var classified = ClassifyDestination(url, activeRoutePatterns);

                        linkRows.Add(new LinkRow
                        {
                            File = file,
                            LineNumber = i + 1,
                            DestinationRaw = url,
                            DestinationDisplay = "<strong>" + EscapePipes(url) + "</strong>", // bold
                            Method = "NavigateTo",
                            Scheme = scheme,
                            MatchHtml = classified.MatchHtml
                        });
                    }

                }
            }

            var roleColumns = globalRoles.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            routeRows = routeRows.OrderBy(r => r.Route, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.File, StringComparer.OrdinalIgnoreCase).ToList();
            linkRows = linkRows.OrderBy(l => l.File, StringComparer.OrdinalIgnoreCase).ThenBy(l => l.LineNumber).ToList();

            var sb = new StringBuilder();
            sb.AppendLine(BuildRoutesMarkdown(routeRows, roleColumns));
            sb.AppendLine();
            sb.AppendLine("## Link Scan");
            sb.AppendLine();
            sb.AppendLine(BuildLinksMarkdown(linkRows));
            File.WriteAllText(output, sb.ToString(), new UTF8Encoding(false));
            Console.WriteLine("✅ Markdown report generated: " + Path.GetFullPath(output));

            if (exportCsv)
            {
                ExportRoutesCsv(routeRows, roleColumns, output);
                ExportLinksCsv(linkRows, output);
                Console.WriteLine("✅ CSV exports written alongside markdown");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    // CSV Export
    private static void ExportRoutesCsv(List<RouteRow> rows, List<string> roleColumns, string outputBase)
    {
        var csvFile = GetBaseName(outputBase) + "_routes.csv";
        using (var sw = new StreamWriter(csvFile, false, new UTF8Encoding(false)))
        {
            var header = new List<string> { "Route", "RouteActive", "Authorized" };
            header.AddRange(roleColumns);
            header.Add("File");
            sw.WriteLine(string.Join(",", header));

            foreach (var r in rows)
            {
                var authText = r.Authorized ? "yes" : "no";
                var activeText = r.RouteActive ? "yes" : "no";
                var roleMarks = roleColumns.Select(rc => r.Roles.Any(rr => rc.Equals(rr, StringComparison.OrdinalIgnoreCase)) ? "yes" : "-");
                var cells = new List<string> { r.Route, activeText, authText };
                cells.AddRange(roleMarks);
                cells.Add(r.File);
                sw.WriteLine(string.Join(",", cells.Select(CsvEscape)));
            }
        }
    }

    private static void ExportLinksCsv(List<LinkRow> rows, string outputBase)
    {
        var csvFile = GetBaseName(outputBase) + "_links.csv";
        using (var sw = new StreamWriter(csvFile, false, new UTF8Encoding(false)))
        {
            sw.WriteLine("File,Line,Method,Scheme,Destination,Match");

            foreach (var r in rows)
            {
                sw.WriteLine(string.Join(",", new[]
                {
                CsvEscape(r.File),
                r.LineNumber.ToString(),
                CsvEscape(r.Method),
                CsvEscape(r.Scheme),
                CsvEscape(r.DestinationRaw),
                StripHtml(r.MatchHtml)
            }));
            }
        }
    }


    private static string GetBaseName(string path)
    {
        var dir = Path.GetDirectoryName(path);
        var name = Path.GetFileNameWithoutExtension(path);
        return string.IsNullOrEmpty(dir) ? name : Path.Combine(dir, name);
    }

    private static string CsvEscape(string s)
    {
        if (s.IndexOf('"') >= 0 || s.IndexOf(',') >= 0 || s.IndexOf('\n') >= 0 || s.IndexOf('\r') >= 0)
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }

    private static string StripHtml(string s)
    {
        return Regex.Replace(s, "<.*?>", "").Trim();
    }

    private static string SafeReadAllText(string path)
    {
        try { return File.ReadAllText(path); } catch { return null; }
    }

    private struct Range { public int Start; public int End; }

    private static List<Range> GetRazorCommentRanges(string content)
    {
        var list = new List<Range>();
        var matches = RazorCommentRx.Matches(content);
        foreach (Match m in matches)
        {
            list.Add(new Range { Start = m.Index, End = m.Index + m.Length - 1 });
        }
        return list;
    }

    private static bool IsInRanges(int index, List<Range> ranges)
    {
        foreach (var r in ranges)
            if (index >= r.Start && index <= r.End) return true;
        return false;
    }

    private static List<(string Route, bool Active)> GetRoutesWithActive(string content)
    {
        var commentRanges = GetRazorCommentRanges(content);
        var matches = RouteRx.Matches(content);
        var items = new List<(string, bool)>();

        foreach (Match m in matches)
        {
            var route = m.Groups[1].Value;
            var idx = m.Index;
            var active = !IsInRanges(idx, commentRanges);
            items.Add((route, active));
        }
        return items;
    }

    private static (bool Authorized, List<string> Roles) GetAuthorizeInfo(string content)
    {
        var hasAllowAnon = HasAllowAnonymousRx.IsMatch(content);
        var hasAuthorize = HasAuthorizeRx.IsMatch(content);

        var roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var blocks = AuthorizeBlocksRx.Matches(content);
        foreach (Match m in blocks)
        {
            var args = m.Groups["args"] != null ? m.Groups["args"].Value : "";
            if (string.IsNullOrWhiteSpace(args)) continue;

            var roleMatches = RolesArgRx.Matches(args);
            foreach (Match rm in roleMatches)
            {
                var val = rm.Groups[1].Success ? rm.Groups[1].Value : (rm.Groups[2].Success ? rm.Groups[2].Value : null);
                if (string.IsNullOrWhiteSpace(val)) continue;

                var parts = val.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var p in parts)
                {
                    var trim = p.Trim();
                    if (trim.Length > 0) roles.Add(trim);
                }
            }
        }

        return (!hasAllowAnon && hasAuthorize, roles.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static List<Regex> BuildActiveRouteRegexes(List<RouteRow> routeRows)
    {
        var templates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in routeRows)
            if (r.RouteActive) templates.Add(r.Route);

        var list = new List<Regex>();
        foreach (var template in templates)
        {
            var t = template.StartsWith("/") ? template : ("/" + template);
            var escaped = Regex.Escape(t);
            escaped = Regex.Replace(escaped, @"\\\{\*\*[^}]+\\\}", ".+");
            escaped = Regex.Replace(escaped, @"\\\{[^}]+\\\}", "[^/]+");
            var pattern = "^" + escaped + "/?$";
            list.Add(new Regex(pattern, RegexOptions.IgnoreCase));
        }
        return list;
    }

    private static Classified ClassifyDestination(string raw, List<Regex> activeRoutePatterns)
    {
        var url = raw.Trim();
        var pathOnly = StripQueryAndFragment(url);

        if (IsExternal(url))
            return new Classified { MatchHtml = "<span style=\"color:blue\">external</span>", Kind = "external" };

        var path = pathOnly.StartsWith("/") ? pathOnly : ("/" + pathOnly);

        foreach (var rx in activeRoutePatterns)
            if (rx.IsMatch(path))
                return new Classified { MatchHtml = "<span style=\"color:green\">matched</span>", Kind = "matched" };

        return new Classified { MatchHtml = "<span style=\"color:red\">unknown</span>", Kind = "unknown" };
    }

    private static string StripQueryAndFragment(string url)
    {
        // Remove fragment
        int h = url.IndexOf('#');
        if (h >= 0) url = url.Substring(0, h);
        // Remove query
        int q = url.IndexOf('?');
        if (q >= 0) url = url.Substring(0, q);
        return url;
    }

    private static bool IsExternal(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        var u = url.Trim();
        if (u.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return true;
        if (u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return true;
        if (u.StartsWith("//")) return true;
        if (u.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) return true;
        if (u.StartsWith("tel:", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string EscapePipes(string s)
    {
        return s.Replace("|", "\\|");
    }

    private static string BuildRoutesMarkdown(List<RouteRow> rows, List<string> roleColumns)
    {
        var columns = new List<string> { "Route", "RouteActive", "Authorized" };
        columns.AddRange(roleColumns);
        columns.Add("File");

        var sb = new StringBuilder();
        sb.AppendLine("## Routes");
        sb.AppendLine();
        sb.Append("| ").Append(string.Join(" | ", columns)).AppendLine(" |");
        sb.Append("| ").Append(string.Join(" | ", columns.ConvertAll(_ => "---"))).AppendLine(" |");

        foreach (var r in rows)
        {
            var authText = r.Authorized ? "yes" : "no";
            var activeText = r.RouteActive ? "yes" : "no";
            var roleMarks = roleColumns.Select(rc => r.Roles.Any(rr => rc.Equals(rr, StringComparison.OrdinalIgnoreCase)) ? "yes" : "-");
            var cells = new List<string> { r.Route, activeText, authText };
            cells.AddRange(roleMarks);
            cells.Add(r.File);
            sb.Append("| ").Append(string.Join(" | ", cells)).AppendLine(" |");
        }

        return sb.ToString();
    }
    private static string GetScheme(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "relative";
        var u = url.Trim();

        if (u.StartsWith("//")) return "//"; // protocol-relative

        int colon = u.IndexOf(':');
        int slash = u.IndexOf('/');
        // if we see ":" before any "/" (or no slash at all), treat as scheme
        if (colon > 0 && (slash == -1 || colon < slash))
        {
            return u.Substring(0, colon).ToLowerInvariant(); // http, https, mailto, tel, etc.
        }

        return "relative";
    }

    private static string BuildLinksMarkdown(List<LinkRow> rows)
    {
        var columns = new[] { "File", "Line", "Method", "Scheme", "Destination", "Match" };
        var sb = new StringBuilder();

        // Header
        sb.Append("| ").Append(string.Join(" | ", columns)).AppendLine(" |");
        // Separator
        sb.Append("| ").Append(string.Join(" | ", columns.Select(_ => "---"))).AppendLine(" |");

        foreach (var r in rows)
        {
            var cells = new[]
            {
            r.File,
            r.LineNumber.ToString(),
            r.Method,
            r.Scheme,
            r.DestinationDisplay,
            r.MatchHtml
        };
            sb.Append("| ").Append(string.Join(" | ", cells)).AppendLine(" |");
        }
        return sb.ToString();
    }


    // Models
    private class RouteRow
    {
        public string Route = "";
        public bool RouteActive;
        public bool Authorized;
        public IEnumerable<string> Roles = new string[0];
        public string File = "";
    }

    private class LinkRow
    {
        public string File = "";
        public int LineNumber;
        public string DestinationRaw = "";
        public string DestinationDisplay = "";
        public string Method = ""; // "href" or "NavigateTo"
        public string Scheme = ""; // http/https/mailto/tel/relative///
        public string MatchHtml = ""; // colored label
    }


    private struct Classified
    {
        public string MatchHtml;
        public string Kind;
    }
}
