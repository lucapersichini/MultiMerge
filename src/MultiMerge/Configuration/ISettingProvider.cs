// Modified by Luca Persichini in 2026 for the MultiMerge fork; see NOTICE.txt.
namespace MultiMerge
{
    internal interface ISettingProvider
    {
        T ReadValue<T>(string key);
        bool TryReadValue<T>(string key, out T value);
        void WriteValue<T>(string key, T value);
    }
}
