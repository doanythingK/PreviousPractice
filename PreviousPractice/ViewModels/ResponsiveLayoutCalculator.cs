namespace PreviousPractice.ViewModels;

internal readonly record struct ResponsiveLayoutMetrics(
    bool UseWideLayout,
    double ImageViewportWidth,
    double ImageViewportHeight);

internal static class ResponsiveLayoutCalculator
{
    internal static ResponsiveLayoutMetrics Calculate(
        double width,
        double height,
        double wideLayoutMinimumWidth,
        double wideHorizontalChrome,
        double narrowHorizontalChrome,
        double minimumViewportWidth,
        double maximumViewportWidth,
        double minimumViewportHeight,
        double maximumViewportHeight)
    {
        var safeWidth = double.IsFinite(width) && width > 0d
            ? width
            : wideLayoutMinimumWidth;
        var safeHeight = double.IsFinite(height) && height > 0d
            ? height
            : maximumViewportHeight / 0.55d;
        var useWideLayout = safeWidth >= wideLayoutMinimumWidth;
        var horizontalChrome = useWideLayout
            ? wideHorizontalChrome
            : narrowHorizontalChrome;
        var viewportWidth = Math.Clamp(
            safeWidth - horizontalChrome,
            minimumViewportWidth,
            maximumViewportWidth);
        var viewportHeight = Math.Clamp(
            safeHeight * (useWideLayout ? 0.58d : 0.44d),
            minimumViewportHeight,
            maximumViewportHeight);

        return new ResponsiveLayoutMetrics(
            useWideLayout,
            viewportWidth,
            viewportHeight);
    }
}
