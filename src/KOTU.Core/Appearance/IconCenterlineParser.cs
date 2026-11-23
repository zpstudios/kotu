using System.Globalization;
using System.Text.RegularExpressions;

namespace KOTU.Core.Appearance;

internal sealed record IconFigure(DesignIconPoint[] Points, bool Closed);

/// <summary>Flattens the small, absolute SVG command vocabulary used by KOTU's original icon source.</summary>
internal static partial class IconCenterlineParser
{
    [GeneratedRegex("[A-Za-z]|[-+]?(?:[0-9]*[.])?[0-9]+(?:[eE][-+]?[0-9]+)?")]
    private static partial Regex Tokens();

    internal static IconFigure[] Parse(string path)
    {
        var tokens = Tokens().Matches(path).Select(m => m.Value).ToArray();
        var result = new List<IconFigure>();
        var points = new List<DesignIconPoint>();
        var current = new DesignIconPoint();
        var position = 0;
        var command = ' ';
        double N() => double.Parse(tokens[position++], CultureInfo.InvariantCulture);
        DesignIconPoint P() => new(N(), N());
        void End(bool closed)
        {
            if (points.Count > 1) result.Add(new(points.ToArray(), closed));
            points.Clear();
        }
        void Add(DesignIconPoint point) { points.Add(point); current = point; }
        while (position < tokens.Length)
        {
            if (char.IsLetter(tokens[position][0])) command = tokens[position++][0];
            var start = current;
            switch (command)
            {
                case 'M': End(false); Add(P()); command = 'L'; break;
                case 'L': Add(P()); break;
                case 'H': Add(new(N(), current.Y)); break;
                case 'V': Add(new(current.X, N())); break;
                case 'Z':
                    if (points.Count > 0) { current = points[0]; End(true); }
                    command = ' '; break;
                case 'Q':
                {
                    var control = P(); var end = P();
                    var steps = Steps(start, control, end);
                    for (var step = 1; step <= steps; step++)
                    {
                        var t = step / (double)steps; var u = 1 - t;
                        Add(new(u*u*start.X + 2*u*t*control.X + t*t*end.X,
                            u*u*start.Y + 2*u*t*control.Y + t*t*end.Y));
                    }
                    break;
                }
                case 'C':
                {
                    var first = P(); var second = P(); var end = P();
                    var steps = Steps(start, first, second, end);
                    for (var step = 1; step <= steps; step++)
                    {
                        var t = step / (double)steps; var u = 1 - t;
                        Add(new(u*u*u*start.X + 3*u*u*t*first.X + 3*u*t*t*second.X + t*t*t*end.X,
                            u*u*u*start.Y + 3*u*u*t*first.Y + 3*u*t*t*second.Y + t*t*t*end.Y));
                    }
                    break;
                }
                case 'A':
                {
                    var rx = N(); var ry = N(); var rotation = N();
                    var large = N() != 0; var sweep = N() != 0; var end = P();
                    foreach (var point in Arc(start, end, rx, ry, rotation, large, sweep)) Add(point);
                    break;
                }
                default: throw new InvalidDataException($"Unsupported icon command '{command}'.");
            }
        }
        End(false);
        return result.ToArray();
    }

    private static int Steps(params DesignIconPoint[] points) => Math.Clamp(
        (int)Math.Ceiling(points.Zip(points.Skip(1), (a,b) => Math.Sqrt((b.X-a.X)*(b.X-a.X)+(b.Y-a.Y)*(b.Y-a.Y))).Sum() * 2), 4, 64);

    private static IEnumerable<DesignIconPoint> Arc(DesignIconPoint start, DesignIconPoint end,
        double rx, double ry, double rotation, bool large, bool sweep)
    {
        rx = Math.Abs(rx); ry = Math.Abs(ry);
        if (rx == 0 || ry == 0 || start == end) { yield return end; yield break; }
        var phi = rotation * Math.PI / 180;
        var cos = Math.Cos(phi); var sin = Math.Sin(phi);
        var dx = (start.X-end.X)/2; var dy = (start.Y-end.Y)/2;
        var x = cos*dx + sin*dy; var y = -sin*dx + cos*dy;
        var scale = x*x/(rx*rx) + y*y/(ry*ry);
        if (scale > 1) { rx *= Math.Sqrt(scale); ry *= Math.Sqrt(scale); }
        var ratio = Math.Max(0, (rx*rx*ry*ry-rx*rx*y*y-ry*ry*x*x)/(rx*rx*y*y+ry*ry*x*x));
        var sign = large == sweep ? -1 : 1;
        var cx = sign*Math.Sqrt(ratio)*rx*y/ry; var cy = -sign*Math.Sqrt(ratio)*ry*x/rx;
        var centerX = cos*cx - sin*cy + (start.X+end.X)/2;
        var centerY = sin*cx + cos*cy + (start.Y+end.Y)/2;
        var from = Math.Atan2((y-cy)/ry, (x-cx)/rx);
        var to = Math.Atan2((-y-cy)/ry, (-x-cx)/rx);
        var delta = to-from;
        if (!sweep && delta > 0) delta -= 2*Math.PI;
        if (sweep && delta < 0) delta += 2*Math.PI;
        var steps = Math.Clamp((int)Math.Ceiling(Math.Abs(delta)*Math.Max(rx,ry)*2), 4, 96);
        for (var step = 1; step <= steps; step++)
        {
            var angle = from+delta*step/steps;
            yield return step == steps ? end : new(
                centerX+cos*rx*Math.Cos(angle)-sin*ry*Math.Sin(angle),
                centerY+sin*rx*Math.Cos(angle)+cos*ry*Math.Sin(angle));
        }
    }
}
