using System.Drawing;

namespace SecondScreen.Input;

public static class TouchCoordinateMapper
{
    public const ushort MaxNormalizedCoord = 65535;

    /// <summary>
    /// Maps 0..65535 normalized tablet touch coordinates into absolute Windows desktop screen pixel coordinates.
    /// </summary>
    public static Point NormalizeToScreen(
        ushort normalizedX,
        ushort normalizedY,
        Rectangle screenBounds)
    {
        if (screenBounds.Width <= 0 || screenBounds.Height <= 0)
            return Point.Empty;

        double factorX = (double)normalizedX / MaxNormalizedCoord;
        double factorY = (double)normalizedY / MaxNormalizedCoord;

        int x = screenBounds.X + (int)Math.Round(factorX * (screenBounds.Width - 1));
        int y = screenBounds.Y + (int)Math.Round(factorY * (screenBounds.Height - 1));

        return new Point(x, y);
    }
}

