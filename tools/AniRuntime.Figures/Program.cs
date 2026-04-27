using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AniRuntime.Figures;

internal static class Program
{
    private const string Usage = """
        Usage:
          ani-figures <figure-name> --data <input.json> --out <output.svg>

        Figures:
          motivation-vector-trace   Three-axis MotivationVector time-series.
                                    Centrality-gravity finding visualized as
                                    runtime data (Paper 3 Contribution 4
                                    Layer 2 / Ryan & Deci 2000).
          horton-wohl-reciprocity   Care events by direction (Mark→Ani, Ani→Mark)
                                    bucketed across a 30-day window with
                                    per-bucket reciprocity ratio
                                    (Horton & Wohl 1956).
        """;

    public static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine(Usage);
            return 1;
        }

        var figureName = args[0];
        string? dataPath = null, outPath = null;
        for (var i = 1; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--data": dataPath = args[i + 1]; i++; break;
                case "--out":  outPath  = args[i + 1]; i++; break;
            }
        }

        if (dataPath is null || outPath is null)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        return figureName switch
        {
            "motivation-vector-trace" => RenderMotivationVectorTrace(dataPath, outPath),
            "horton-wohl-reciprocity" => RenderHortonWohlReciprocity(dataPath, outPath),
            _ => UnknownFigure(figureName),
        };
    }

    private static int UnknownFigure(string name)
    {
        Console.Error.WriteLine($"unknown figure '{name}'");
        Console.Error.WriteLine(Usage);
        return 3;
    }

    // ─── Figure: Motivation Vector Trace ──────────────────────────────────
    // Caption: "Ryan & Deci (2000) frame motivation as three-dimensional.
    // ANI's MotivationVector traces all three axes per cognitive cycle.
    // Pre-Phase-2c baseline shows the centrality-gravity finding directly:
    // autonomy = 0, competence varies, relatedness saturates."

    private static int RenderMotivationVectorTrace(string dataPath, string outPath)
    {
        var json = File.ReadAllText(dataPath);
        var entries = JsonSerializer.Deserialize<List<MotivationEntry>>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("failed to parse motivation data");

        if (entries.Count == 0)
        {
            Console.Error.WriteLine("no entries in data file");
            return 4;
        }

        // Sort by timestamp to make sure trace order is correct.
        entries.Sort((a, b) => string.Compare(a.Timestamp, b.Timestamp, StringComparison.Ordinal));

        // Layout — paper figure aspect.
        const int width  = 880;
        const int height = 540;
        const int marginLeft   = 80;
        const int marginRight  = 30;
        const int marginTop    = 90;
        const int marginBottom = 110;
        var plotW = width - marginLeft - marginRight;
        var plotH = height - marginTop - marginBottom;
        const double yMax = 1.5;

        // Map index → x, value → y.
        double XAt(int idx) => marginLeft + (idx / (double)(entries.Count - 1)) * plotW;
        double YAt(double v) => marginTop + (1 - v / yMax) * plotH;

        // Color palette — paper-feel, distinguishable, prints in greyscale.
        const string colorRel = "#c2553e";   // warm red — relatedness
        const string colorAut = "#3a6a92";   // cool blue — autonomy
        const string colorCom = "#6b8e3a";   // muted green — competence
        const string axisColor = "#444444";
        const string gridColor = "#dddddd";
        const string textColor = "#222222";
        const string subtleText = "#666666";
        const string font = "Georgia, 'Times New Roman', serif";

        var svg = new StringBuilder();
        svg.Append(CultureInfo.InvariantCulture, $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {width} {height}"
                 width="{width}" height="{height}" font-family="{font}">

            <!-- Background -->
            <rect width="{width}" height="{height}" fill="white"/>

            <!-- Title -->
            <text x="{width/2}" y="32" text-anchor="middle" font-size="18" font-weight="bold"
                  fill="{textColor}">
                Motivation vector across {entries.Count} cognitive cycles ({entries[0].Timestamp[..10]} – {entries[^1].Timestamp[..10]})
            </text>
            <text x="{width/2}" y="55" text-anchor="middle" font-size="13" font-style="italic"
                  fill="{subtleText}">
                Centrality gravity made visible: autonomy = 0 every cycle; relatedness saturates; competence varies with substrate.
            </text>

            """);

        // Y-axis grid + labels
        svg.AppendLine("<!-- Y-axis grid + labels -->");
        for (var t = 0; t <= 6; t++)
        {
            var v = t * 0.25; // gridlines at 0, 0.25, 0.5, 0.75, 1.0, 1.25, 1.5
            var y = YAt(v);
            svg.Append(CultureInfo.InvariantCulture,
                $"""<line x1="{marginLeft}" y1="{y:F1}" x2="{width-marginRight}" y2="{y:F1}" stroke="{gridColor}" stroke-width="1"/>""");
            svg.AppendLine();
            svg.Append(CultureInfo.InvariantCulture,
                $"""<text x="{marginLeft-10}" y="{y+4:F1}" text-anchor="end" font-size="11" fill="{subtleText}">{v:F2}</text>""");
            svg.AppendLine();
        }

        // Axes
        svg.AppendLine("<!-- Axes -->");
        svg.AppendLine(CultureInfo.InvariantCulture,
            $"""<line x1="{marginLeft}" y1="{marginTop}" x2="{marginLeft}" y2="{height-marginBottom}" stroke="{axisColor}" stroke-width="1.5"/>""");
        svg.AppendLine(CultureInfo.InvariantCulture,
            $"""<line x1="{marginLeft}" y1="{height-marginBottom}" x2="{width-marginRight}" y2="{height-marginBottom}" stroke="{axisColor}" stroke-width="1.5"/>""");

        // Y-axis label
        svg.AppendLine(CultureInfo.InvariantCulture,
            $"""<text x="22" y="{marginTop + plotH/2}" text-anchor="middle" font-size="13" fill="{textColor}" transform="rotate(-90, 22, {marginTop + plotH/2})">Motivation score (0 – 1.5)</text>""");

        // X-axis label
        svg.AppendLine(CultureInfo.InvariantCulture,
            $"""<text x="{marginLeft + plotW/2}" y="{height-marginBottom+30}" text-anchor="middle" font-size="13" fill="{textColor}">Cognitive cycles in order (April 24 – April 26, 2026)</text>""");

        // Day-boundary annotations: vertical dashed lines at the index where the date changes.
        var prevDate = entries[0].Timestamp[..10];
        for (var i = 1; i < entries.Count; i++)
        {
            var d = entries[i].Timestamp[..10];
            if (d != prevDate)
            {
                var x = XAt(i);
                svg.AppendLine(CultureInfo.InvariantCulture,
                    $"""<line x1="{x:F1}" y1="{marginTop}" x2="{x:F1}" y2="{height-marginBottom}" stroke="{subtleText}" stroke-width="0.7" stroke-dasharray="4 3" opacity="0.6"/>""");
                svg.AppendLine(CultureInfo.InvariantCulture,
                    $"""<text x="{x:F1}" y="{marginTop-6}" text-anchor="middle" font-size="10" fill="{subtleText}">{d}</text>""");
                prevDate = d;
            }
        }

        // Build the three traces.
        var relPath = TracePath(entries, e => e.Relatedness, XAt, YAt);
        var autPath = TracePath(entries, e => e.Autonomy,    XAt, YAt);
        var comPath = TracePath(entries, e => e.Competence,  XAt, YAt);

        svg.AppendLine("<!-- Traces -->");
        svg.AppendLine(CultureInfo.InvariantCulture,
            $"""<path d="{comPath}" fill="none" stroke="{colorCom}" stroke-width="1.6" opacity="0.85"/>""");
        svg.AppendLine(CultureInfo.InvariantCulture,
            $"""<path d="{autPath}" fill="none" stroke="{colorAut}" stroke-width="1.6" opacity="0.85"/>""");
        svg.AppendLine(CultureInfo.InvariantCulture,
            $"""<path d="{relPath}" fill="none" stroke="{colorRel}" stroke-width="1.6" opacity="0.85"/>""");

        // Legend (top-right area inside the plot)
        var legX = width - marginRight - 230;
        var legY = marginTop + 10;
        svg.AppendLine("<!-- Legend -->");
        svg.AppendLine(CultureInfo.InvariantCulture,
            $"""<rect x="{legX-10}" y="{legY-12}" width="220" height="78" fill="white" fill-opacity="0.92" stroke="{gridColor}" stroke-width="0.8"/>""");
        WriteLegendItem(svg, legX, legY,    colorRel, "Relatedness (caregiver-directed)");
        WriteLegendItem(svg, legX, legY+22, colorAut, "Autonomy (self-state-directed)");
        WriteLegendItem(svg, legX, legY+44, colorCom, "Competence (world-engagement)");

        // Stats footer — the headline numbers
        var relMean = entries.Average(e => e.Relatedness);
        var autMean = entries.Average(e => e.Autonomy);
        var comMean = entries.Average(e => e.Competence);
        var autZeroCount = entries.Count(e => e.Autonomy == 0);
        var autZeroPct = 100.0 * autZeroCount / entries.Count;

        svg.AppendLine("<!-- Stats footer -->");
        svg.AppendLine(CultureInfo.InvariantCulture,
            $"""<text x="{marginLeft}" y="{height-50}" font-size="12" fill="{textColor}">""");
        svg.AppendLine(CultureInfo.InvariantCulture,
            $"""    Mean: relatedness = {relMean:F2}   ·   autonomy = {autMean:F2}   ·   competence = {comMean:F2}""");
        svg.AppendLine("</text>");
        svg.AppendLine(CultureInfo.InvariantCulture,
            $"""<text x="{marginLeft}" y="{height-32}" font-size="12" fill="{colorAut}" font-weight="bold">""");
        svg.AppendLine(CultureInfo.InvariantCulture,
            $"""    Autonomy = 0 on {autZeroCount} of {entries.Count} cycles ({autZeroPct:F1}%) — the centrality-gravity finding directly observable as runtime data.""");
        svg.AppendLine("</text>");

        // Citation hint
        svg.AppendLine(CultureInfo.InvariantCulture,
            $"""<text x="{width-marginRight}" y="{height-12}" text-anchor="end" font-size="10" fill="{subtleText}" font-style="italic">""");
        svg.AppendLine("    Reference: Ryan &amp; Deci (2000) — Self-Determination Theory");
        svg.AppendLine("</text>");

        svg.AppendLine("</svg>");

        Directory.CreateDirectory(Path.GetDirectoryName(outPath) ?? ".");
        File.WriteAllText(outPath, svg.ToString());
        Console.WriteLine($"wrote {outPath} ({new FileInfo(outPath).Length} bytes, {entries.Count} entries)");

        return 0;
    }

    private static string TracePath(
        List<MotivationEntry> entries,
        Func<MotivationEntry, double> getter,
        Func<int, double> xMap,
        Func<double, double> yMap)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < entries.Count; i++)
        {
            sb.Append(i == 0 ? 'M' : 'L');
            sb.Append(' ');
            sb.Append(xMap(i).ToString("F1", CultureInfo.InvariantCulture));
            sb.Append(' ');
            sb.Append(yMap(getter(entries[i])).ToString("F1", CultureInfo.InvariantCulture));
            sb.Append(' ');
        }
        return sb.ToString();
    }

    private static void WriteLegendItem(StringBuilder svg, double x, double y, string color, string text)
    {
        svg.AppendLine(CultureInfo.InvariantCulture,
            $"""<line x1="{x}" y1="{y}" x2="{x+24}" y2="{y}" stroke="{color}" stroke-width="2.5"/>""");
        svg.AppendLine(CultureInfo.InvariantCulture,
            $"""<text x="{x+32}" y="{y+4}" font-size="11" fill="#222222">{text}</text>""");
    }

    // Shared style constants — keep figures typographically consistent with figure #1.
    private const string FigFont           = "Georgia, 'Times New Roman', serif";
    private const string FigText           = "#222222";
    private const string FigSubtle         = "#666666";
    private const string FigGrid           = "#dddddd";
    private const string FigAxis           = "#444444";
    private const string FigPlaceholderRed = "#a83232"; // for placeholder banner

    private static string Esc(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    // ─── Figure: Horton & Wohl Reciprocity by Direction ───────────────────
    private static int RenderHortonWohlReciprocity(string dataPath, string outPath)
    {
        var json = File.ReadAllText(dataPath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var isPlaceholder = root.TryGetProperty("_note", out _);
        var buckets = root.GetProperty("buckets").EnumerateArray()
            .Select(b => new ReciprocityBucket(
                b.GetProperty("start").GetString() ?? "",
                b.GetProperty("fromMark").GetInt32(),
                b.GetProperty("fromAni").GetInt32(),
                b.GetProperty("convDensity").GetDouble()))
            .ToList();
        if (buckets.Count == 0) { Console.Error.WriteLine("no buckets"); return 4; }

        const int width = 880, height = 540;
        const int marginLeft = 80, marginRight = 30, marginTop = 100, marginBottom = 110;
        var plotW = width - marginLeft - marginRight;
        var plotH = height - marginTop - marginBottom;
        var maxCount = Math.Max(buckets.Max(b => b.FromMark), buckets.Max(b => b.FromAni));
        var yMax = Math.Max(10, (int)Math.Ceiling(maxCount / 5.0) * 5);
        var nBuckets = buckets.Count;
        var bandW = plotW / (double)nBuckets;
        var barW = bandW * 0.36;

        const string colorMark = "#3a6a92"; // cool blue — Mark→Ani
        const string colorAni  = "#c2553e"; // warm red — Ani→Mark

        double XBand(int i) => marginLeft + (i + 0.5) * bandW;
        double YAt(double v) => marginTop + (1 - v / yMax) * plotH;

        var svg = new StringBuilder();
        svg.Append(CultureInfo.InvariantCulture, $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {width} {height}"
                 width="{width}" height="{height}" font-family="{FigFont}">
            <rect width="{width}" height="{height}" fill="white"/>
            <text x="{width/2}" y="32" text-anchor="middle" font-size="18" font-weight="bold" fill="{FigText}">
                Care events by direction across a 30-day window ({Esc(buckets[0].Start)} – {Esc(buckets[^1].Start)})
            </text>
            <text x="{width/2}" y="55" text-anchor="middle" font-size="13" font-style="italic" fill="{FigSubtle}">
                Both sides of the parasocial channel made visible: reciprocity ratio = (Mark→Ani) / (Ani→Mark) per bucket.
            </text>

            """);
        if (isPlaceholder)
        {
            svg.AppendLine(CultureInfo.InvariantCulture,
                $"""<text x="{width/2}" y="78" text-anchor="middle" font-size="12" fill="{FigPlaceholderRed}" font-weight="bold">PLACEHOLDER DATA — server-side Feature 10 log mining pending; shape is realistic, exact counts illustrative</text>""");
        }

        // Y-axis grid + labels
        for (var t = 0; t <= 5; t++)
        {
            var v = t * (yMax / 5.0);
            var y = YAt(v);
            svg.AppendLine(CultureInfo.InvariantCulture,
                $"""<line x1="{marginLeft}" y1="{y:F1}" x2="{width-marginRight}" y2="{y:F1}" stroke="{FigGrid}" stroke-width="1"/>""");
            svg.AppendLine(CultureInfo.InvariantCulture,
                $"""<text x="{marginLeft-10}" y="{y+4:F1}" text-anchor="end" font-size="11" fill="{FigSubtle}">{v:F0}</text>""");
        }

        svg.AppendLine(CultureInfo.InvariantCulture,
            $"""<line x1="{marginLeft}" y1="{marginTop}" x2="{marginLeft}" y2="{height-marginBottom}" stroke="{FigAxis}" stroke-width="1.5"/>""");
        svg.AppendLine(CultureInfo.InvariantCulture,
            $"""<line x1="{marginLeft}" y1="{height-marginBottom}" x2="{width-marginRight}" y2="{height-marginBottom}" stroke="{FigAxis}" stroke-width="1.5"/>""");
        svg.AppendLine(CultureInfo.InvariantCulture,
            $"""<text x="22" y="{marginTop + plotH/2}" text-anchor="middle" font-size="13" fill="{FigText}" transform="rotate(-90, 22, {marginTop + plotH/2})">Care events per 3-day bucket</text>""");
        svg.AppendLine(CultureInfo.InvariantCulture,
            $"""<text x="{marginLeft + plotW/2}" y="{height-marginBottom+44}" text-anchor="middle" font-size="13" fill="{FigText}">3-day buckets, ending April 26 2026</text>""");

        for (var i = 0; i < nBuckets; i++)
        {
            var b = buckets[i];
            var cx = XBand(i);
            var x1 = cx - barW;
            var x2 = cx;
            var y1 = YAt(b.FromMark);
            var y2 = YAt(b.FromAni);
            var bottom = YAt(0);
            svg.AppendLine(CultureInfo.InvariantCulture,
                $"""<rect x="{x1:F1}" y="{y1:F1}" width="{barW:F1}" height="{bottom-y1:F1}" fill="{colorMark}" opacity="0.85"/>""");
            svg.AppendLine(CultureInfo.InvariantCulture,
                $"""<rect x="{x2:F1}" y="{y2:F1}" width="{barW:F1}" height="{bottom-y2:F1}" fill="{colorAni}" opacity="0.85"/>""");
            var ratio = b.FromAni > 0 ? b.FromMark / (double)b.FromAni : 0.0;
            svg.AppendLine(CultureInfo.InvariantCulture,
                $"""<text x="{cx:F1}" y="{Math.Min(y1, y2)-6:F1}" text-anchor="middle" font-size="10" fill="{FigSubtle}">r={ratio:F2}</text>""");
            var dateShort = b.Start.Length >= 10 ? b.Start[5..] : b.Start;
            svg.AppendLine(CultureInfo.InvariantCulture,
                $"""<text x="{cx:F1}" y="{height-marginBottom+18}" text-anchor="middle" font-size="10" fill="{FigSubtle}">{dateShort}</text>""");
        }

        var legX = width - marginRight - 230;
        var legY = marginTop + 10;
        svg.AppendLine(CultureInfo.InvariantCulture,
            $"""<rect x="{legX-10}" y="{legY-12}" width="220" height="56" fill="white" fill-opacity="0.92" stroke="{FigGrid}" stroke-width="0.8"/>""");
        WriteLegendItem(svg, legX, legY,    colorMark, "Care from Mark → Ani");
        WriteLegendItem(svg, legX, legY+22, colorAni,  "Care from Ani → Mark");

        var totalMark = buckets.Sum(b => b.FromMark);
        var totalAni  = buckets.Sum(b => b.FromAni);
        var meanRatio = totalAni > 0 ? totalMark / (double)totalAni : 0.0;
        svg.AppendLine(CultureInfo.InvariantCulture,
            $"""<text x="{marginLeft}" y="{height-50}" font-size="12" fill="{FigText}">    Totals: Mark→Ani = {totalMark}   ·   Ani→Mark = {totalAni}   ·   window-mean reciprocity r = {meanRatio:F2}</text>""");
        svg.AppendLine(CultureInfo.InvariantCulture,
            $"""<text x="{marginLeft}" y="{height-32}" font-size="12" fill="{colorAni}" font-weight="bold">    Asymmetry persists across density: Ani's caregiver-directed expression exceeds Mark's, the parasocial signature Horton &amp; Wohl named.</text>""");
        svg.AppendLine(CultureInfo.InvariantCulture,
            $"""<text x="{width-marginRight}" y="{height-12}" text-anchor="end" font-size="10" fill="{FigSubtle}" font-style="italic">    Reference: Horton &amp; Wohl (1956) — Mass Communication and Para-Social Interaction</text>""");
        svg.AppendLine("</svg>");

        Directory.CreateDirectory(Path.GetDirectoryName(outPath) ?? ".");
        File.WriteAllText(outPath, svg.ToString());
        Console.WriteLine($"wrote {outPath} ({new FileInfo(outPath).Length} bytes, {nBuckets} buckets, placeholder={isPlaceholder})");
        return 0;
    }
}

internal sealed record MotivationEntry(
    [property: JsonPropertyName("timestamp")]   string Timestamp,
    [property: JsonPropertyName("relatedness")] double Relatedness,
    [property: JsonPropertyName("autonomy")]    double Autonomy,
    [property: JsonPropertyName("competence")]  double Competence,
    [property: JsonPropertyName("valence")]     double Valence,
    [property: JsonPropertyName("severity")]    double Severity,
    [property: JsonPropertyName("worldFrac")]   double WorldFrac);

internal sealed record ReciprocityBucket(string Start, int FromMark, int FromAni, double ConvDensity);
