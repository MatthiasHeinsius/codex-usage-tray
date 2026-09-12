namespace CodexUsageTray.Tests;

public sealed class UsagePopupFormTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReopeningKeepsThePopupAtItsInitialHeightAndVerticalPositionWhenFirstShown(bool compact)
    {
        const int SimulatedDpiHeightIncrease = 56;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var popup = new UsagePopupForm { Opacity = 0 };
                popup.CreateControl();
                popup.SetViewModeForScreenshot(compact);
                var firstShow = true;
                popup.VisibleChanged += (_, _) =>
                {
                    if (popup.Visible && firstShow)
                    {
                        firstShow = false;
                        popup.Height += SimulatedDpiHeightIncrease;
                    }
                };

                popup.ShowNearTray();
                Application.DoEvents();
                var initialBounds = popup.Bounds;
                var workingArea = Screen.FromRectangle(initialBounds).WorkingArea;
                popup.Hide();
                Application.DoEvents();

                popup.ShowNearTray();
                Application.DoEvents();

                Assert.Equal(initialBounds.Height, popup.Height);
                Assert.Equal(initialBounds.Top, popup.Top);
                Assert.Equal(8, workingArea.Bottom - popup.Bottom);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw failure;
        }
    }
}
