namespace CodexUsageTray;

internal static class PopupPositioning
{
    private const int BorderInset = 8;
    private const int SnapDistance = 12;

    internal static Point GetLocationWhenShown(
        Point currentLocation,
        bool pinned,
        Point cursorLocation,
        Size windowSize,
        Rectangle workingArea,
        int contentInset)
    {
        if (pinned)
        {
            return currentLocation;
        }

        var x = Math.Clamp(
            cursorLocation.X - windowSize.Width + contentInset,
            workingArea.Left,
            workingArea.Right - windowSize.Width);
        var y = workingArea.Bottom - windowSize.Height - BorderInset;
        return new Point(x, y);
    }

    internal static Point GetLocationWhenDragged(
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

    internal static Rectangle GetBoundsWhenResized(
        Rectangle currentBounds,
        Size targetSize,
        Rectangle screenBounds,
        Rectangle workingArea)
    {
        var targetY = currentBounds.Top == screenBounds.Top
            || currentBounds.Top == screenBounds.Top + BorderInset
            || currentBounds.Top == workingArea.Top
            || currentBounds.Top == workingArea.Top + BorderInset
                ? currentBounds.Top
                : currentBounds.Bottom - targetSize.Height;
        var location = KeepWithinBounds(
            new Point(currentBounds.Left, targetY),
            targetSize,
            screenBounds);
        return new Rectangle(location, targetSize);
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

    private static Point KeepWithinBounds(Point location, Size windowSize, Rectangle bounds)
    {
        var maximumX = Math.Max(bounds.Left, bounds.Right - windowSize.Width);
        var maximumY = Math.Max(bounds.Top, bounds.Bottom - windowSize.Height);
        return new Point(
            Math.Clamp(location.X, bounds.Left, maximumX),
            Math.Clamp(location.Y, bounds.Top, maximumY));
    }
}
