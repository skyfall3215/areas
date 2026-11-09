using System.Drawing;

namespace SecondScreen.Display;

public sealed record DisplayInfo(
    int Index,
    string DeviceName,
    string FriendlyName,
    Rectangle Bounds,
    bool IsPrimary,
    bool IsVirtual
);

