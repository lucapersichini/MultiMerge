// Modified by Luca Persichini in 2026 for the MultiMerge fork; see NOTICE.txt.
using System;
using System.Windows;
using System.Windows.Controls;

namespace MultiMerge
{
    // Pannello della barra segmentata della scheda "Merge from Task". I figli con IsFixedWidth (i
    // punti di check-in tra le parti) prendono la loro larghezza; gli altri (un segmento per passo) si
    // dividono in parti uguali lo spazio che resta, cosi' ogni parte e' larga in proporzione ai suoi
    // passi. Tra segmenti larghi almeno MinWidthForGap resta uno stacco di Gap px; piu' stretti si
    // toccano, e centinaia di passi restano comunque tutti visibili.
    public sealed class TaskMergeStepBarPanel : Panel
    {
        private const double Gap = 1.0;
        private const double MinWidthForGap = 3.0;

        public static readonly DependencyProperty IsFixedWidthProperty = DependencyProperty.RegisterAttached(
            "IsFixedWidth",
            typeof(bool),
            typeof(TaskMergeStepBarPanel),
            new FrameworkPropertyMetadata(false,
                FrameworkPropertyMetadataOptions.AffectsParentMeasure | FrameworkPropertyMetadataOptions.AffectsParentArrange));

        public static bool GetIsFixedWidth(DependencyObject element)
        {
            if (element == null)
                throw new ArgumentNullException("element");
            return (bool)element.GetValue(IsFixedWidthProperty);
        }

        public static void SetIsFixedWidth(DependencyObject element, bool value)
        {
            if (element == null)
                throw new ArgumentNullException("element");
            element.SetValue(IsFixedWidthProperty, value);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            var height = 0.0;
            var fixedWidth = 0.0;
            var flexible = 0;

            foreach (UIElement child in InternalChildren)
            {
                if (child == null)
                    continue;
                if (GetIsFixedWidth(child))
                {
                    child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
                    fixedWidth += child.DesiredSize.Width;
                    height = Math.Max(height, child.DesiredSize.Height);
                }
                else
                {
                    flexible++;
                }
            }

            var finiteWidth = !double.IsInfinity(availableSize.Width);
            var share = flexible == 0 || !finiteWidth ? 0.0 : Math.Max(0.0, (availableSize.Width - fixedWidth) / flexible);
            foreach (UIElement child in InternalChildren)
            {
                if (child == null || GetIsFixedWidth(child))
                    continue;
                child.Measure(new Size(share, availableSize.Height));
                height = Math.Max(height, child.DesiredSize.Height);
            }

            var width = finiteWidth ? availableSize.Width : fixedWidth;
            if (!double.IsInfinity(availableSize.Height))
                height = Math.Min(height, availableSize.Height);
            return new Size(width, height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var fixedWidth = 0.0;
            var flexible = 0;
            foreach (UIElement child in InternalChildren)
            {
                if (child == null)
                    continue;
                if (GetIsFixedWidth(child))
                    fixedWidth += child.DesiredSize.Width;
                else
                    flexible++;
            }

            var share = flexible == 0 ? 0.0 : Math.Max(0.0, (finalSize.Width - fixedWidth) / flexible);
            var gap = share >= MinWidthForGap ? Gap : 0.0;
            var x = 0.0;
            foreach (UIElement child in InternalChildren)
            {
                if (child == null)
                    continue;
                if (GetIsFixedWidth(child))
                {
                    var width = child.DesiredSize.Width;
                    child.Arrange(new Rect(x, 0, width, finalSize.Height));
                    x += width;
                }
                else
                {
                    child.Arrange(new Rect(x, 0, Math.Max(0.0, share - gap), finalSize.Height));
                    x += share;
                }
            }
            return finalSize;
        }
    }
}
