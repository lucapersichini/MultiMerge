// Modified by Luca Persichini in 2026 for the MultiMerge fork; see NOTICE.txt.
using System;
using System.Windows;
using System.Windows.Controls;

namespace MultiMerge
{
    // Barra superiore del resolver: a sinistra il titolo (icona + nome del file + path), a destra la
    // navigazione fra i blocchi (contatore + frecce) e le scelte sul blocco corrente (Take source /
    // target / both). Invece di tagliare i pulsanti quando la colonna del resolver e' stretta, si
    // dispone su piu' righe:
    // - larga: tutto su una riga; il titolo prende lo spazio che resta (troncato, mai sotto
    //   MinTitleWidth o la sua larghezza naturale, se piu' piccola);
    // - media: il titolo sopra, navigazione + scelte sotto, allineate a sinistra;
    // - stretta: titolo, navigazione e scelte su tre righe (il separatore sparisce); le scelte vanno
    //   a capo da sole se stanno in una WrapPanel.
    //
    // Figli, in quest'ordine: titolo, navigazione, separatore, scelte (quelli mancanti o Collapsed
    // valgono zero).
    public sealed class ConflictHeaderPanel : Panel
    {
        public static readonly DependencyProperty MinTitleWidthProperty = DependencyProperty.Register(
            "MinTitleWidth", typeof(double), typeof(ConflictHeaderPanel),
            new FrameworkPropertyMetadata(160.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static readonly DependencyProperty ColumnSpacingProperty = DependencyProperty.Register(
            "ColumnSpacing", typeof(double), typeof(ConflictHeaderPanel),
            new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static readonly DependencyProperty RowSpacingProperty = DependencyProperty.Register(
            "RowSpacing", typeof(double), typeof(ConflictHeaderPanel),
            new FrameworkPropertyMetadata(4.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        private enum Layout
        {
            OneRow,
            TwoRows,
            ThreeRows
        }

        private Layout _layout;

        // Spazio minimo lasciato al titolo prima di mandare i pulsanti sotto.
        public double MinTitleWidth
        {
            get { return (double)GetValue(MinTitleWidthProperty); }
            set { SetValue(MinTitleWidthProperty, value); }
        }

        // Distanza fra il titolo e i pulsanti quando stanno sulla stessa riga.
        public double ColumnSpacing
        {
            get { return (double)GetValue(ColumnSpacingProperty); }
            set { SetValue(ColumnSpacingProperty, value); }
        }

        // Distanza fra le righe quando i pulsanti vanno sotto il titolo.
        public double RowSpacing
        {
            get { return (double)GetValue(RowSpacingProperty); }
            set { SetValue(RowSpacingProperty, value); }
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            var title = GetChild(0);
            var navigation = GetChild(1);
            var separator = GetChild(2);
            var choices = GetChild(3);

            // Larghezze naturali (una riga sola ciascuno).
            var unbounded = new Size(double.PositiveInfinity, double.PositiveInfinity);
            var titleNatural = MeasureWidth(title, unbounded);
            var navigationWidth = MeasureWidth(navigation, unbounded);
            var separatorWidth = MeasureWidth(separator, unbounded);
            var choicesWidth = MeasureWidth(choices, unbounded);
            var actionsWidth = navigationWidth + separatorWidth + choicesWidth;
            var gap = actionsWidth > 0 ? ColumnSpacing : 0;

            var width = availableSize.Width;
            var minTitle = Math.Min(titleNatural, MinTitleWidth);
            if (double.IsInfinity(width) || double.IsNaN(width) || actionsWidth + gap + minTitle <= width)
                _layout = Layout.OneRow;
            else if (actionsWidth <= width)
                _layout = Layout.TwoRows;
            else
                _layout = Layout.ThreeRows;

            double height;
            double desiredWidth;
            switch (_layout)
            {
                case Layout.OneRow:
                    {
                        var titleWidth = double.IsInfinity(width) || double.IsNaN(width)
                            ? double.PositiveInfinity
                            : Math.Max(0, width - actionsWidth - gap);
                        MeasureWidth(title, new Size(titleWidth, availableSize.Height));
                        height = Math.Max(HeightOf(title), Math.Max(HeightOf(navigation), Math.Max(HeightOf(separator), HeightOf(choices))));
                        desiredWidth = WidthOf(title) + gap + actionsWidth;
                        break;
                    }
                case Layout.TwoRows:
                    {
                        MeasureWidth(title, new Size(width, double.PositiveInfinity));
                        var actionsHeight = Math.Max(HeightOf(navigation), Math.Max(HeightOf(separator), HeightOf(choices)));
                        height = HeightOf(title) + RowSpacingIf(actionsWidth > 0) + actionsHeight;
                        desiredWidth = Math.Max(WidthOf(title), actionsWidth);
                        break;
                    }
                default:
                    {
                        var row = new Size(width, double.PositiveInfinity);
                        MeasureWidth(title, row);
                        MeasureWidth(navigation, row);
                        MeasureWidth(choices, row);
                        height = HeightOf(title) + RowSpacingIf(HeightOf(navigation) > 0) + HeightOf(navigation)
                                 + RowSpacingIf(HeightOf(choices) > 0) + HeightOf(choices);
                        desiredWidth = Math.Max(WidthOf(title), Math.Max(WidthOf(navigation), WidthOf(choices)));
                        break;
                    }
            }

            if (!double.IsInfinity(width) && !double.IsNaN(width))
                desiredWidth = Math.Min(desiredWidth, width);
            return new Size(desiredWidth, height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var title = GetChild(0);
            var navigation = GetChild(1);
            var separator = GetChild(2);
            var choices = GetChild(3);

            var width = finalSize.Width;
            var navigationWidth = WidthOf(navigation);
            var separatorWidth = WidthOf(separator);
            var choicesWidth = WidthOf(choices);

            switch (_layout)
            {
                case Layout.OneRow:
                    {
                        var actionsWidth = navigationWidth + separatorWidth + choicesWidth;
                        var gap = actionsWidth > 0 ? ColumnSpacing : 0;
                        var rowHeight = finalSize.Height;
                        var x = Math.Max(0, width - actionsWidth);
                        Place(title, 0, 0, Math.Max(0, x - gap), rowHeight);
                        Place(navigation, x, 0, navigationWidth, rowHeight);
                        x += navigationWidth;
                        Place(separator, x, 0, separatorWidth, rowHeight);
                        x += separatorWidth;
                        Place(choices, x, 0, choicesWidth, rowHeight);
                        break;
                    }
                case Layout.TwoRows:
                    {
                        var titleHeight = HeightOf(title);
                        Place(title, 0, 0, width, titleHeight);
                        var y = titleHeight + RowSpacingIf(navigationWidth + separatorWidth + choicesWidth > 0);
                        var rowHeight = Math.Max(0, finalSize.Height - y);
                        var x = 0.0;
                        Place(navigation, x, y, navigationWidth, rowHeight);
                        x += navigationWidth;
                        Place(separator, x, y, separatorWidth, rowHeight);
                        x += separatorWidth;
                        Place(choices, x, y, choicesWidth, rowHeight);
                        break;
                    }
                default:
                    {
                        var y = 0.0;
                        var titleHeight = HeightOf(title);
                        Place(title, 0, y, width, titleHeight);
                        y += titleHeight;
                        var navigationHeight = HeightOf(navigation);
                        y += RowSpacingIf(navigationHeight > 0);
                        Place(navigation, 0, y, Math.Min(navigationWidth, width), navigationHeight);
                        y += navigationHeight;
                        // Navigazione e scelte su righe diverse: il separatore verticale non separa niente.
                        Place(separator, 0, y, 0, 0);
                        var choicesHeight = HeightOf(choices);
                        y += RowSpacingIf(choicesHeight > 0);
                        Place(choices, 0, y, width, choicesHeight);
                        break;
                    }
            }
            return finalSize;
        }

        private UIElement GetChild(int index)
        {
            return index < InternalChildren.Count ? InternalChildren[index] : null;
        }

        private double RowSpacingIf(bool condition)
        {
            return condition ? RowSpacing : 0;
        }

        private static double MeasureWidth(UIElement element, Size constraint)
        {
            if (element == null)
                return 0;
            element.Measure(constraint);
            return element.DesiredSize.Width;
        }

        private static double WidthOf(UIElement element)
        {
            return element == null ? 0 : element.DesiredSize.Width;
        }

        private static double HeightOf(UIElement element)
        {
            return element == null ? 0 : element.DesiredSize.Height;
        }

        private static void Place(UIElement element, double x, double y, double width, double height)
        {
            if (element != null)
                element.Arrange(new Rect(x, y, Math.Max(0, width), Math.Max(0, height)));
        }
    }
}
