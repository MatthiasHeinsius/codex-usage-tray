namespace CodexUsageTray;

internal static class PopupPositioning
{
    internal const int BorderInset = 8;
    internal const int ContentInset = 20;
    private const int SnapDistance = 12;

    internal static Point GetLocationWhenShown(
        Point currentLocation,
        bool pinned,
        Point cursorLocation,
        Size windowSize,
        Rectangle workingArea)
    {
        if (pinned)
        {
            return currentLocation;
        }

        var x = Math.Clamp(
            cursorLocation.X - windowSize.Width + ContentInset,
            workingArea.Left,
            workingArea.Right - windowSize.Width);
        var y = workingArea.Bottom - windowSize.Height - BorderInset;
        return new Point(x, y);
    }

    internal static Point SnapToScreen(
        Point location,
        Size windowSize,
        Rectangle screenBounds,
        Rectangle workingArea)
    {
        var x = SnapCoordinate(
            location.X,
            windowSize.Width,
            screenBounds.Left,
            workingArea.Left,
            screenBounds.Right,
            workingArea.Right);
        var y = SnapCoordinate(
            location.Y,
            windowSize.Height,
            screenBounds.Top,
            workingArea.Top,
            screenBounds.Bottom,
            workingArea.Bottom);

        return new Point(x, y);
    }

    private static int SnapCoordinate(
        int location,
        int length,
        int boundsStart,
        int workingStart,
        int boundsEnd,
        int workingEnd)
    {
        var nearestOffset = SnapDistance + 1;
        Consider(boundsStart - location);
        Consider(boundsStart + BorderInset - location);
        Consider(workingStart - location);
        Consider(workingStart + BorderInset - location);
        Consider(boundsEnd - location - length);
        Consider(boundsEnd - BorderInset - location - length);
        Consider(workingEnd - location - length);
        Consider(workingEnd - BorderInset - location - length);
        return location + (Math.Abs(nearestOffset) <= SnapDistance ? nearestOffset : 0);

        void Consider(int offset)
        {
            if (Math.Abs(offset) < Math.Abs(nearestOffset))
            {
                nearestOffset = offset;
            }
        }
    }
}
