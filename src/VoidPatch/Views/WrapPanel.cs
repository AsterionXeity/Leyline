using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace VoidPatch.Views;

public sealed class WrapPanel : Panel
{
    public double HorizontalSpacing { get; set; } = 14.0;
    public double VerticalSpacing { get; set; } = 14.0;

    protected override Size MeasureOverride(Size availableSize)
    {
        double maxWidth = double.IsInfinity(availableSize.Width) ? 1200 : availableSize.Width;
        double currentRowWidth = 0;
        double currentRowHeight = 0;
        double totalWidth = 0;
        double totalHeight = 0;

        foreach (UIElement child in Children)
        {
            child.Measure(availableSize);
            Size desired = child.DesiredSize;

            if (currentRowWidth > 0 && currentRowWidth + desired.Width > maxWidth)
            {
                totalWidth = Math.Max(totalWidth, currentRowWidth - HorizontalSpacing);
                totalHeight += currentRowHeight + VerticalSpacing;
                currentRowWidth = 0;
                currentRowHeight = 0;
            }

            currentRowWidth += desired.Width + HorizontalSpacing;
            currentRowHeight = Math.Max(currentRowHeight, desired.Height);
        }

        if (currentRowWidth > 0)
        {
            totalWidth = Math.Max(totalWidth, currentRowWidth - HorizontalSpacing);
            totalHeight += currentRowHeight;
        }

        return new Size(totalWidth, totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double maxWidth = finalSize.Width;
        double x = 0;
        double y = 0;
        double rowHeight = 0;

        foreach (UIElement child in Children)
        {
            Size desired = child.DesiredSize;

            if (x > 0 && x + desired.Width > maxWidth)
            {
                x = 0;
                y += rowHeight + VerticalSpacing;
                rowHeight = 0;
            }

            child.Arrange(new Rect(x, y, desired.Width, desired.Height));
            x += desired.Width + HorizontalSpacing;
            rowHeight = Math.Max(rowHeight, desired.Height);
        }

        return finalSize;
    }
}
