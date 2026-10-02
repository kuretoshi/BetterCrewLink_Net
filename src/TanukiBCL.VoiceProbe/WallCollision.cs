using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.VoiceProbe;

// TanukiBCL v3.2.7 ColliderMap.ts uses SVG-path intersections after mapping
// game coordinates to (x + 40, 40 - y). The path data is embedded unchanged.
internal static class WallCollision
{
    private const double IntersectionTolerance = 1e-9d;
    private const double CurveFlatness = 0.001d;
    private static readonly Regex PathTokens = new(@"[MLHVCZ]|[-+]?(?:\d*\.)?\d+(?:[eE][-+]?\d+)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Lazy<CollisionMapData> Data = new(LoadData);

    public static bool Intersects(Player listener, Player speaker, MapType map, IReadOnlyList<int> closedDoors)
    {
        var firstX = listener.X;
        var secondX = speaker.X;
        if (map == MapType.TheSkeldApril)
        {
            firstX = -firstX;
            secondX = -secondX;
            map = MapType.TheSkeld;
        }

        if (!Data.Value.Colliders.TryGetValue(map, out var colliders)) return false;
        var voiceLine = new Segment(
            new Point(firstX + 40d, 40d - listener.Y),
            new Point(secondX + 40d, 40d - speaker.Y));
        if (colliders.Any(collider => SegmentsIntersect(voiceLine, collider))) return true;

        if (!Data.Value.Doors.TryGetValue(map, out var doors)) return false;
        foreach (var doorId in closedDoors)
        {
            if (doors.TryGetValue(doorId, out var doorSegments) &&
                doorSegments.Any(door => SegmentsIntersect(voiceLine, door)))
            {
                return true;
            }
        }
        return false;
    }

    private static CollisionMapData LoadData()
    {
        using var stream = typeof(WallCollision).Assembly.GetManifestResourceStream(
            "TanukiBCL.VoiceProbe.Assets.ColliderMap.v3.2.7.json")
            ?? throw new InvalidOperationException("Embedded TanukiBCL v3.2.7 collider map is missing.");
        using var document = JsonDocument.Parse(stream);
        var colliders = new Dictionary<MapType, Segment[]>();
        foreach (var map in document.RootElement.GetProperty("colliders").EnumerateObject())
        {
            if (!TryParseMap(map.Name, out var mapType)) continue;
            colliders[mapType] = map.Value.EnumerateArray()
                .SelectMany(path => ParsePath(path.GetString()!)).ToArray();
        }

        var doors = new Dictionary<MapType, Dictionary<int, Segment[]>>();
        foreach (var map in document.RootElement.GetProperty("doors").EnumerateObject())
        {
            if (!TryParseMap(map.Name, out var mapType)) continue;
            doors[mapType] = map.Value.EnumerateObject().ToDictionary(
                door => int.Parse(door.Name, CultureInfo.InvariantCulture),
                door => ParsePath(door.Value.GetString()!).ToArray());
        }
        return new CollisionMapData(colliders, doors);
    }

    private static bool TryParseMap(string name, out MapType map)
    {
        map = name switch
        {
            "THE_SKELD" => MapType.TheSkeld,
            "MIRA_HQ" => MapType.MiraHq,
            "POLUS" => MapType.Polus,
            "AIRSHIP" => MapType.Airship,
            "SUBMERGED" => MapType.Submerged,
            "FUNGLE" => MapType.Fungle,
            _ => MapType.Unknown
        };
        return map != MapType.Unknown;
    }

    private static IEnumerable<Segment> ParsePath(string path)
    {
        var tokens = PathTokens.Matches(path).Select(match => match.Value).ToArray();
        var segments = new List<Segment>();
        var current = new Point(0d, 0d);
        var subpathStart = current;
        var command = '\0';
        var index = 0;
        while (index < tokens.Length)
        {
            if (tokens[index].Length == 1 && char.IsLetter(tokens[index][0]))
            {
                command = tokens[index++][0];
                if (command == 'Z')
                {
                    AddSegment(current, subpathStart);
                    current = subpathStart;
                    command = '\0';
                }
                continue;
            }

            switch (command)
            {
                case 'M':
                    current = ReadPoint();
                    subpathStart = current;
                    command = 'L';
                    break;
                case 'L':
                    var lineEnd = ReadPoint();
                    AddSegment(current, lineEnd);
                    current = lineEnd;
                    break;
                case 'H':
                    var horizontalEnd = new Point(ReadNumber(), current.Y);
                    AddSegment(current, horizontalEnd);
                    current = horizontalEnd;
                    break;
                case 'V':
                    var verticalEnd = new Point(current.X, ReadNumber());
                    AddSegment(current, verticalEnd);
                    current = verticalEnd;
                    break;
                case 'C':
                    var control1 = ReadPoint();
                    var control2 = ReadPoint();
                    var curveEnd = ReadPoint();
                    AddCubic(current, control1, control2, curveEnd, segments, 0);
                    current = curveEnd;
                    break;
                default:
                    throw new FormatException($"Unsupported collider path near token {index}: {path}");
            }
        }
        return segments;

        double ReadNumber()
        {
            if (index == tokens.Length || char.IsLetter(tokens[index][0]))
                throw new FormatException($"Incomplete collider path: {path}");
            return double.Parse(tokens[index++], CultureInfo.InvariantCulture);
        }

        Point ReadPoint() => new(ReadNumber(), ReadNumber());

        void AddSegment(Point start, Point end)
        {
            if (start != end) segments.Add(new Segment(start, end));
        }
    }

    private static void AddCubic(Point start, Point control1, Point control2, Point end,
        List<Segment> segments, int depth)
    {
        if (depth >= 12 ||
            (DistanceToLine(control1, start, end) <= CurveFlatness &&
             DistanceToLine(control2, start, end) <= CurveFlatness))
        {
            if (start != end) segments.Add(new Segment(start, end));
            return;
        }

        var a = Midpoint(start, control1);
        var b = Midpoint(control1, control2);
        var c = Midpoint(control2, end);
        var d = Midpoint(a, b);
        var e = Midpoint(b, c);
        var middle = Midpoint(d, e);
        AddCubic(start, a, d, middle, segments, depth + 1);
        AddCubic(middle, e, c, end, segments, depth + 1);
    }

    private static Point Midpoint(Point a, Point b) => new((a.X + b.X) / 2d, (a.Y + b.Y) / 2d);

    private static double DistanceToLine(Point point, Point start, Point end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        return length < IntersectionTolerance
            ? Math.Sqrt(Math.Pow(point.X - start.X, 2d) + Math.Pow(point.Y - start.Y, 2d))
            : Math.Abs(dx * (start.Y - point.Y) - (start.X - point.X) * dy) / length;
    }

    private static bool SegmentsIntersect(Segment first, Segment second)
    {
        if (Math.Max(first.Start.X, first.End.X) + IntersectionTolerance < Math.Min(second.Start.X, second.End.X) ||
            Math.Max(second.Start.X, second.End.X) + IntersectionTolerance < Math.Min(first.Start.X, first.End.X) ||
            Math.Max(first.Start.Y, first.End.Y) + IntersectionTolerance < Math.Min(second.Start.Y, second.End.Y) ||
            Math.Max(second.Start.Y, second.End.Y) + IntersectionTolerance < Math.Min(first.Start.Y, first.End.Y))
        {
            return false;
        }

        var a = Cross(first.Start, first.End, second.Start);
        var b = Cross(first.Start, first.End, second.End);
        var c = Cross(second.Start, second.End, first.Start);
        var d = Cross(second.Start, second.End, first.End);
        return OppositeOrZero(a, b) && OppositeOrZero(c, d);
    }

    private static double Cross(Point a, Point b, Point c) =>
        (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

    private static bool OppositeOrZero(double a, double b) =>
        (a <= IntersectionTolerance && b >= -IntersectionTolerance) ||
        (b <= IntersectionTolerance && a >= -IntersectionTolerance);

    private readonly record struct Point(double X, double Y);
    private readonly record struct Segment(Point Start, Point End);
    private sealed record CollisionMapData(
        Dictionary<MapType, Segment[]> Colliders,
        Dictionary<MapType, Dictionary<int, Segment[]>> Doors);
}
