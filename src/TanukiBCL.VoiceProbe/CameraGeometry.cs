using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.VoiceProbe;

// Camera coordinates from TanukiBCL v3.2.7 src/common/AmongusMap.ts.
internal static class CameraGeometry
{
    private static readonly (double X, double Y)[] Skeld =
    [
        (13.2417d, -4.348d), (0.6216d, -6.5642d),
        (-7.1503d, 1.6709d), (-17.8098d, -4.8983d)
    ];

    private static readonly (double X, double Y)[] Polus =
    [
        (29d, -15.7d), (15.4d, -15.4d), (24.4d, -8.5d),
        (17d, -20.6d), (4.7d, -22.73d), (11.6d, -8.2d)
    ];

    private static readonly (double X, double Y)[] Airship =
    [
        (-8.2872d, 0.0527d), (-4.0477d, 9.1447d), (23.5616d, 9.8882d),
        (4.881d, -11.1688d), (30.3702d, -0.874d), (3.3018d, 16.2631d)
    ];

    public static bool TryRelativePosition(MapType map, CameraLocation camera, Player speaker,
        out double deltaX, out double deltaY)
    {
        var positions = map switch
        {
            MapType.TheSkeld => Skeld,
            MapType.Polus => Polus,
            MapType.Airship => Airship,
            _ => []
        };
        if (positions.Length == 0)
        {
            deltaX = deltaY = 0d;
            return false;
        }

        (double X, double Y) position;
        if (camera == CameraLocation.Skeld && map == MapType.TheSkeld)
        {
            position = positions.MinBy(point =>
                Math.Pow(speaker.X - point.X, 2d) + Math.Pow(speaker.Y - point.Y, 2d));
        }
        else if ((int)camera >= 0 && (int)camera < positions.Length && map != MapType.TheSkeld)
        {
            position = positions[(int)camera];
        }
        else
        {
            deltaX = deltaY = 0d;
            return false;
        }

        deltaX = speaker.X - position.X;
        deltaY = speaker.Y - position.Y;
        return true;
    }
}
