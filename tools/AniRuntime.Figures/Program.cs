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
}

internal sealed record MotivationEntry(
    [property: JsonPropertyName("timestamp")]   string Timestamp,
    [property: JsonPropertyName("relatedness")] double Relatedness,
    [property: JsonPropertyName("autonomy")]    double Autonomy,
    [property: JsonPropertyName("competence")]  double Competence,
    [property: JsonPropertyName("valence")]     double Valence,
    [property: JsonPropertyName("severity")]    double Severity,
    [property: JsonPropertyName("worldFrac")]   double WorldFrac);
