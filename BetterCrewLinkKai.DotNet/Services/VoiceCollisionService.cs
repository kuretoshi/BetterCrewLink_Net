using BetterCrewLinkKai.DotNet.Models;

namespace BetterCrewLinkKai.DotNet.Services;

public sealed class VoiceCollisionService
{
    private static readonly IReadOnlyDictionary<MapType, string[]> ColliderMaps = new Dictionary<MapType, string[]>
    {
        [MapType.TheSkeld] =
        [
            "M 33.65 35.32 V 37.57 H 25.3 V 36.35 H 21.71 L 20.41 37.46 V 41.75 H 22.2 V 44.05 H 20.97 V 42.62 H 19.4 V 40.96 H 18.27 L 16.43 42.12 V 48.04 L 18.25 49.14 H 19.4 V 47.49 H 20.97 V 46.18 H 22.2 V 48.66 H 20.36 V 53.04 L 21.69 54.06 H 25.3 V 52.41 H 27.05 V 55.28 H 34.79 L 36.95 57.58 H 41.08 V 52.97 H 44.18 V 53.54 H 41.39 V 56.52 L 42.47 57.54 H 45.52 L 46.63 56.52 V 53.54 H 46.02 V 52.97 H 46.94 V 55.02 H 49.48 L 51.75 52.82 V 49.58 H 50.39 V 47.14 H 52.75 V 45.44 H 55.46 V 46.79 H 57.45 L 59.18 45.39 V 43.45 L 57.47 41.96 H 55.46 V 43.31 H 52.75 V 41.96 H 50.39 V 41.01 H 51.75 V 37.76 L 49.51 35.61 H 46.94 V 37.57 H 45.06 V 36.2 L 42.04 33.26 H 35.75 Z",
            "M 48.07 41.01 H 48.6 V 41.96 H 47.86 V 41.57 H 45.54 V 41.77 L 43.97 43.61 V 45.23 H 47.86 V 44.09 H 50.96 V 45.01 H 48.6 V 49.58 H 48.03 L 46.95 50.84 H 41.08 V 48.23 H 40.21 V 47.88 H 41.87 V 50.45 H 46.13 L 47.07 49.51 V 45.75 H 40.21 V 44.62 H 42.46 L 45.06 42.03 V 39.74 H 46.94 V 39.84 Z",
            "M 29.98 39.79 V 40.22 H 28.75 V 44.85 L 29.61 45.97 H 34.96 V 44.53 L 33.21 42.82 V 40.22 H 31.77 V 39.79 H 33.65 V 42.07 L 36.21 44.62 H 38.37 V 48.23 H 36.38 L 34.79 49.8 V 53.1 H 31.33 V 52.54 H 32.61 L 33.69 51.52 V 49.45 L 34.96 48.25 V 46.84 H 29.63 V 53.1 H 28.8 V 50.27 H 25.3 V 48.66 H 23.99 V 46.18 H 25.39 V 47.45 H 28.23 V 42.73 L 27.47 41.96 H 26.37 L 25.39 42.92 V 44.05 H 23.99 V 41.75 H 25.3 V 39.79 Z"
        ],
        [MapType.MiraHq] =
        [
            "M 39.79 31.723 H 28.373 V 40.223 H 33.731 V 42.698 H 43.717 V 42.213 H 53.633 V 40.463 V 42.213 H 57.119 V 37.556 H 53.594 V 36.64 V 38.64 V 37.556 H 57.119 V 33.806 H 53.584 V 34.733 V 33.757 L 57.805 29.561 L 62 33.802 H 61.098 V 38.437 H 60.118 H 61.118 V 33.802 H 57.868 V 40.74 H 60.783 H 57.868 V 43.907 H 60.366 V 42.74 H 69.283 V 40.574 H 66.783 H 69.283 V 33.74 H 64.449 V 31.49 H 62.949 L 58.989 27.53 V 21.66 V 23.235 H 62.822 V 18.142 H 58.405 H 59.072 V 19.975 V 18.142 H 62.905 V 14.058 H 52.822 V 18.219 H 57.17 H 56.586 V 21.552 V 18.213 H 52.83 H 52.747 V 23.297 H 56.664 V 27.63 L 52.614 31.513 H 51.197 V 34.68 V 33.753 H 48.27 V 37.086 H 47.27 V 26.678 V 30.345 H 52.227 V 24.54 H 44.879 V 28.54 V 23.209 H 39.977 V 30.499 H 44.942 V 37.083 H 43.713 V 39.916 H 51.188 V 36.621 V 39.955 H 43.713 V 40.332 H 37.386 V 40.082 H 39.803 Z M 63.203 40.64 H 64.369"
        ],
        [MapType.Polus] =
        [
            "M 51.257 48.531 V 48.131 H 41.423 V 50.498 H 41.857 H 41.423 V 53.131 H 44.751 H 44.365 V 50.459 H 43.184 H 44.365 V 53.131 H 44.532 V 58.095 H 44.798 H 43.798 H 44.165 V 55.161 L 43.189 54.352 H 40.123 V 58.052 H 42.423 V 60.652 H 42.123 H 42.423 V 58.068 H 40.123 V 60.662 H 40.777 H 40.123 V 62.495 H 42.744 H 40.123 V 65.095 H 44.477 V 62.462 H 44.111 H 44.477 V 61.962 H 45.244",
            "M 46.577 61.962 H 47.011 V 58.095 H 46.244 V 53.131 H 46.111 H 46.577 V 50.544 H 46.811",
            "M 51.257 50.205 V 50.544 H 48.14",
            "M 54.09 46.221 V 38.754 H 59.289 V 46.216",
            "M 64.738 48.655 V 48.445 H 65.938",
            "M 70.711 48.578 H 67.311 V 48.445 H 68.611 V 46.178 H 72.544 V 46.545 V 44.845 H 75.678 V 45.245 V 45.011 H 77.044 V 46.011 H 81.144 V 48.578 H 79.944 H 76.944 H 77.996 V 48.711 V 48.578 H 81.144 V 50.967 H 79.756 H 79.956 V 59.364 H 79.389 V 61.364 L 78.129 62.557 H 75.003 L 73.701 61.223 V 61.023 V 61.347 H 68.844 V 65.376 H 65.474 V 65.807 H 62.693 V 63.954 H 61.626 H 62.693 V 63.121 H 63.226 H 62.693 V 65.807 H 60.646 V 63.94 V 65.807 H 59.546 V 62.907 H 57.403 V 62.541 V 62.907 H 59.713 V 59.874 H 60.313 H 59.279",
            "M 68.703 56.136 V 55.503 H 63.436 V 58.07 H 63.736 H 62.936 H 63.47 V 55.536 H 55.803 V 58.103 H 56.503 H 56.57 H 55.803 V 59.903 H 57.937 H 57.403 V 60.87",
            "M 57.936 58.097 H 61.602",
            "M 57.193 51.668 H 58.493 V 53.006 H 62.73 V 49.972 H 57.193",
            "M 51.646 58.923 V 57.79 H 53.313 V 54.857 H 50.113 V 58.923",
            "M 52.257 60.719 V 61.801 H 49.591 V 65.268 H 52.991 L 54.2 64.025 V 61.758 H 53.791 V 60.719"
        ]
    };

    private static readonly IReadOnlyDictionary<MapType, IReadOnlyDictionary<int, string>> DoorMaps = new Dictionary<MapType, IReadOnlyDictionary<int, string>>
    {
        [MapType.TheSkeld] = new Dictionary<int, string>
        {
            [0] = "M 45.059 37.568 V 39.744",
            [1] = "M 34.786 55.353 V 53.101",
            [2] = "M 25.3 39.787 V 37.568",
            [3] = "M 38.371 44.717 H 40.207",
            [4] = "M 22.196 48.663 H 24.169",
            [5] = "M 22.196 41.945 H 23.989",
            [6] = "M 25.154 44.051 V 46.183",
            [7] = "M 38.371 48.231 H 40.207",
            [8] = "M 33.649 37.568 V 39.787",
            [9] = "M 29.628 53.101 H 31.333",
            [10] = "M 29.977 39.787 H 31.77",
            [11] = "M 25.412 52.405 V 50.274",
            [12] = "M 41.081 52.971 V 50.839"
        },
        [MapType.MiraHq] = new Dictionary<int, string>
        {
            [0] = "M 44.942 37.086 H 47.27",
            [1] = "M 44.942 30.499 H 47.27"
        },
        [MapType.Polus] = new Dictionary<int, string>
        {
            [0] = "M 51.257 48.531 V 50.205",
            [1] = "M 48.14 50.544 H 46.811",
            [2] = "M 44.751 53.131 H 46.111",
            [3] = "M 44.798 58.095 H 46.244",
            [4] = "M 45.244 61.962 H 46.577",
            [5] = "M 52.257 60.719 H 53.791",
            [6] = "M 50.113 58.923 H 51.646",
            [7] = "M 68.703 56.136 V 57.763",
            [8] = "M 57.403 60.87 V 62.541",
            [9] = "M 65.938 48.445 H 67.311",
            [10] = "M 64.738 48.655 V 50.384",
            [11] = "M 57.193 49.972 V 51.668",
            [12] = "M 65.475 63.639 V 65.376",
            [13] = "M 63.226 63.121 H 64.575",
            [14] = "M 77.996 50.401 V 48.711",
            [15] = "M 78.363 50.967 H 79.756"
        }
    };

    private static readonly IReadOnlyDictionary<MapType, IReadOnlyDictionary<CameraLocation, Point2>> CameraMaps = new Dictionary<MapType, IReadOnlyDictionary<CameraLocation, Point2>>
    {
        [MapType.TheSkeld] = new Dictionary<CameraLocation, Point2>
        {
            [CameraLocation.East] = new(13.2417, -4.348),
            [CameraLocation.Central] = new(0.6216, -6.5642),
            [CameraLocation.Northeast] = new(-7.1503, 1.6709),
            [CameraLocation.South] = new(-17.8098, -4.8983)
        },
        [MapType.Polus] = new Dictionary<CameraLocation, Point2>
        {
            [CameraLocation.East] = new(29, -15.7),
            [CameraLocation.Central] = new(15.4, -15.4),
            [CameraLocation.Northeast] = new(24.4, -8.5),
            [CameraLocation.South] = new(17, -20.6),
            [CameraLocation.SouthWest] = new(4.7, -22.73),
            [CameraLocation.NorthWest] = new(11.6, -8.2)
        },
        [MapType.Airship] = new Dictionary<CameraLocation, Point2>
        {
            [CameraLocation.East] = new(-8.2872, 0.0527),
            [CameraLocation.Central] = new(-4.0477, 9.1447),
            [CameraLocation.Northeast] = new(23.5616, 9.8882),
            [CameraLocation.South] = new(4.881, -11.1688),
            [CameraLocation.SouthWest] = new(30.3702, -0.874),
            [CameraLocation.NorthWest] = new(3.3018, 16.2631)
        }
    };

    public bool PoseCollide(Player me, Player other, MapType map, IReadOnlyCollection<int> closedDoors)
    {
        var start = ToMapPoint(me.X, me.Y);
        var end = ToMapPoint(other.X, other.Y);

        if (map == MapType.TheSkeldApril)
        {
            start = ToMapPoint(me.X * -1, me.Y);
            end = ToMapPoint(other.X * -1, other.Y);
            map = MapType.TheSkeld;
        }

        if (!CollidesWithPaths(ColliderMaps.GetValueOrDefault(map), start, end))
        {
            return CollidesWithClosedDoors(map, closedDoors, start, end);
        }

        return true;
    }

    public bool TryGetCameraPan(Player other, MapType map, CameraLocation currentCamera, out double panX, out double panY)
    {
        panX = 0;
        panY = 0;
        if (!CameraMaps.TryGetValue(map, out var cameras) || cameras.Count == 0 || currentCamera == CameraLocation.None)
        {
            return false;
        }

        Point2 camera;
        if (currentCamera == CameraLocation.Skeld)
        {
            camera = cameras.Values
                .OrderBy(value => Distance(other.X, other.Y, value.X, value.Y))
                .First();
        }
        else if (!cameras.TryGetValue(currentCamera, out camera))
        {
            return false;
        }

        panX = other.X - camera.X;
        panY = other.Y - camera.Y;
        return true;
    }

    private static bool CollidesWithClosedDoors(MapType map, IReadOnlyCollection<int> closedDoors, Point2 start, Point2 end)
    {
        if (!DoorMaps.TryGetValue(map, out var doors))
        {
            return false;
        }

        foreach (var doorId in closedDoors)
        {
            if (doors.TryGetValue(doorId, out var doorPath) && CollidesWithPath(doorPath, start, end))
            {
                return true;
            }
        }

        return false;
    }

    private static bool CollidesWithPaths(string[]? paths, Point2 start, Point2 end)
    {
        if (paths is null)
        {
            return false;
        }

        return paths.Any(path => CollidesWithPath(path, start, end));
    }

    private static bool CollidesWithPath(string path, Point2 start, Point2 end)
    {
        foreach (var segment in ReadSegments(path))
        {
            if (SegmentsIntersect(start, end, segment.Start, segment.End))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<Segment> ReadSegments(string path)
    {
        var tokens = Tokenize(path);
        var index = 0;
        var command = ' ';
        var current = new Point2(0, 0);
        var start = new Point2(0, 0);

        while (index < tokens.Count)
        {
            if (char.IsLetter(tokens[index][0]))
            {
                command = char.ToUpperInvariant(tokens[index++][0]);
            }

            switch (command)
            {
                case 'M':
                    current = ReadPoint(tokens, ref index);
                    start = current;
                    command = 'L';
                    break;
                case 'L':
                    var lineEnd = ReadPoint(tokens, ref index);
                    yield return new Segment(current, lineEnd);
                    current = lineEnd;
                    break;
                case 'H':
                    var horizontal = new Point2(ReadDouble(tokens[index++]), current.Y);
                    yield return new Segment(current, horizontal);
                    current = horizontal;
                    break;
                case 'V':
                    var vertical = new Point2(current.X, ReadDouble(tokens[index++]));
                    yield return new Segment(current, vertical);
                    current = vertical;
                    break;
                case 'C':
                    var control1 = ReadPoint(tokens, ref index);
                    var control2 = ReadPoint(tokens, ref index);
                    var curveEnd = ReadPoint(tokens, ref index);
                    foreach (var segment in FlattenCubic(current, control1, control2, curveEnd))
                    {
                        yield return segment;
                    }

                    current = curveEnd;
                    break;
                case 'Z':
                    yield return new Segment(current, start);
                    current = start;
                    command = ' ';
                    break;
                default:
                    index++;
                    break;
            }
        }
    }

    private static IEnumerable<Segment> FlattenCubic(Point2 start, Point2 control1, Point2 control2, Point2 end)
    {
        var previous = start;
        for (var i = 1; i <= 12; i++)
        {
            var t = i / 12d;
            var next = Cubic(start, control1, control2, end, t);
            yield return new Segment(previous, next);
            previous = next;
        }
    }

    private static Point2 Cubic(Point2 start, Point2 control1, Point2 control2, Point2 end, double t)
    {
        var inverse = 1d - t;
        return new Point2(
            (inverse * inverse * inverse * start.X) + (3 * inverse * inverse * t * control1.X) + (3 * inverse * t * t * control2.X) + (t * t * t * end.X),
            (inverse * inverse * inverse * start.Y) + (3 * inverse * inverse * t * control1.Y) + (3 * inverse * t * t * control2.Y) + (t * t * t * end.Y));
    }

    private static List<string> Tokenize(string path)
    {
        var tokens = new List<string>();
        var token = string.Empty;
        foreach (var character in path)
        {
            if (char.IsLetter(character))
            {
                AddToken();
                tokens.Add(character.ToString());
            }
            else if (char.IsWhiteSpace(character) || character == ',')
            {
                AddToken();
            }
            else
            {
                token += character;
            }
        }

        AddToken();
        return tokens;

        void AddToken()
        {
            if (token.Length == 0)
            {
                return;
            }

            tokens.Add(token);
            token = string.Empty;
        }
    }

    private static Point2 ReadPoint(IReadOnlyList<string> tokens, ref int index)
    {
        return new Point2(ReadDouble(tokens[index++]), ReadDouble(tokens[index++]));
    }

    private static double ReadDouble(string token)
    {
        return double.Parse(token, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static Point2 ToMapPoint(double x, double y)
    {
        return new Point2(x + 40, 40 - y);
    }

    private static bool SegmentsIntersect(Point2 a, Point2 b, Point2 c, Point2 d)
    {
        var d1 = Direction(c, d, a);
        var d2 = Direction(c, d, b);
        var d3 = Direction(a, b, c);
        var d4 = Direction(a, b, d);

        if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) &&
            ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0)))
        {
            return true;
        }

        const double epsilon = 0.000001;
        return Math.Abs(d1) < epsilon && OnSegment(c, d, a) ||
               Math.Abs(d2) < epsilon && OnSegment(c, d, b) ||
               Math.Abs(d3) < epsilon && OnSegment(a, b, c) ||
               Math.Abs(d4) < epsilon && OnSegment(a, b, d);
    }

    private static double Direction(Point2 a, Point2 b, Point2 c)
    {
        return ((c.X - a.X) * (b.Y - a.Y)) - ((b.X - a.X) * (c.Y - a.Y));
    }

    private static bool OnSegment(Point2 a, Point2 b, Point2 c)
    {
        return Math.Min(a.X, b.X) <= c.X && c.X <= Math.Max(a.X, b.X) &&
               Math.Min(a.Y, b.Y) <= c.Y && c.Y <= Math.Max(a.Y, b.Y);
    }

    private static double Distance(double x1, double y1, double x2, double y2)
    {
        var dx = x2 - x1;
        var dy = y2 - y1;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private readonly record struct Point2(double X, double Y);

    private readonly record struct Segment(Point2 Start, Point2 End);
}
