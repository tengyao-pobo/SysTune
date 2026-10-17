using System.Runtime.InteropServices;

namespace SysTune.App.Interop;

/// <summary>
/// Minimal wrapper over the Performance Data Helper (PDH) API. Counters are added with
/// <c>PdhAddEnglishCounter</c> so that paths work on localized Windows (e.g. zh-TW).
/// </summary>
internal sealed partial class PdhQuery : IDisposable
{
    private const uint PDH_FMT_DOUBLE = 0x00000200;
    private const uint PDH_FMT_NOCAP100 = 0x00008000;
    private const uint PDH_MORE_DATA = 0x800007D2;
    private const uint PDH_CSTATUS_VALID_DATA = 0;
    private const uint PDH_CSTATUS_NEW_DATA = 1;

    private nint _query;

    private PdhQuery(nint query) => _query = query;

    public static PdhQuery? TryOpen()
        => PdhOpenQuery(null, 0, out var q) == 0 ? new PdhQuery(q) : null;

    /// <summary>Adds a counter; returns null if the counter does not exist on this machine.</summary>
    public PdhCounter? TryAdd(string englishPath)
    {
        if (_query == 0) return null;
        return PdhAddEnglishCounter(_query, englishPath, 0, out var c) == 0 ? new PdhCounter(c) : null;
    }

    public bool Collect() => _query != 0 && PdhCollectQueryData(_query) == 0;

    public void Dispose()
    {
        if (_query != 0)
        {
            PdhCloseQuery(_query);
            _query = 0;
        }
    }

    internal sealed class PdhCounter
    {
        private readonly nint _handle;
        private nint _buffer;
        private uint _bufferSize;

        internal PdhCounter(nint handle) => _handle = handle;

        /// <summary>Reads a single-instance counter, or null if no valid data yet.</summary>
        public double? Read()
        {
            if (PdhGetFormattedCounterValue(_handle, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, out _, out var v) != 0) return null;
            return v.CStatus is PDH_CSTATUS_VALID_DATA or PDH_CSTATUS_NEW_DATA ? v.DoubleValue : null;
        }

        /// <summary>Reads all instances of a wildcard counter into <paramref name="into"/> (cleared first).</summary>
        public unsafe bool ReadArray(List<KeyValuePair<string, double>> into)
        {
            into.Clear();
            uint size = _bufferSize;
            var status = PdhGetFormattedCounterArray(_handle, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out var count, _buffer);
            if (status == PDH_MORE_DATA)
            {
                if (_buffer != 0) NativeMemory.Free((void*)_buffer);
                _bufferSize = size + 1024;
                _buffer = (nint)NativeMemory.Alloc(_bufferSize);
                size = _bufferSize;
                status = PdhGetFormattedCounterArray(_handle, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out count, _buffer);
            }
            if (status != 0) return false;

            var items = (PDH_FMT_COUNTERVALUE_ITEM*)_buffer;
            for (var i = 0; i < count; i++)
            {
                var item = items[i];
                if (item.FmtValue.CStatus is not (PDH_CSTATUS_VALID_DATA or PDH_CSTATUS_NEW_DATA)) continue;
                var name = Marshal.PtrToStringUni(item.szName) ?? "";
                into.Add(new KeyValuePair<string, double>(name, item.FmtValue.DoubleValue));
            }
            return true;
        }

        ~PdhCounter()
        {
            unsafe
            {
                if (_buffer != 0) NativeMemory.Free((void*)_buffer);
            }
        }
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PDH_FMT_COUNTERVALUE
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double DoubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE_ITEM
    {
        public nint szName;
        public PDH_FMT_COUNTERVALUE FmtValue;
    }

    [LibraryImport("pdh.dll", EntryPoint = "PdhOpenQueryW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhOpenQuery(string? dataSource, nint userData, out nint query);

    [LibraryImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhAddEnglishCounter(nint query, string fullCounterPath, nint userData, out nint counter);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCollectQueryData(nint query);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCloseQuery(nint query);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhGetFormattedCounterValue(nint counter, uint format, out uint type, out PDH_FMT_COUNTERVALUE value);

    [LibraryImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")]
    private static partial uint PdhGetFormattedCounterArray(nint counter, uint format, ref uint bufferSize, out uint itemCount, nint itemBuffer);
}
