// Modified by Luca Persichini in 2026 for the MultiMerge fork; see NOTICE.txt.
using System;
using System.Globalization;
using System.Windows.Data;

namespace MultiMerge
{
    public class MergeModeToStringConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null)
                return null;
            var mergeMode = (MergeMode)value;
            switch (mergeMode)
            {
                case MergeMode.Merge:
                    return "Merge";
                case MergeMode.MergeAndCheckIn:
                    return "Merge & Check In";
                default:
                    return "Unknown mode";
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
