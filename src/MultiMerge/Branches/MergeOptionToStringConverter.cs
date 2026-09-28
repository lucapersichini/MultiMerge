// Modified by Luca Persichini in 2026 for the MultiMerge fork; see NOTICE.txt.
using System;
using System.Globalization;
using System.Windows.Data;

namespace MultiMerge
{
    public class MergeOptionToStringConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value != null)
            {
                var mergeOption = (MergeOption)value;
                switch (mergeOption)
                {
                    case MergeOption.KeepTarget:
                        return "Keep target (discard)";
                    case MergeOption.ManualResolveConflict:
                        return "Manual";
                    case MergeOption.OverwriteTarget:
                        return "Overwrite target";
                }
            }

            return "Unknown mode";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value != null)
            {
                var mergeOptionDisplay = (string) value;
                if (mergeOptionDisplay == "Keep target (discard)")
                    return MergeOption.KeepTarget;

                if (mergeOptionDisplay == "Manual")
                    return MergeOption.ManualResolveConflict;

                if (mergeOptionDisplay == "Overwrite target")
                    return MergeOption.OverwriteTarget;

            }

            return MergeOption.ManualResolveConflict;
        }
    }
}
