using System.Runtime.Versioning;
using SwarmCore.RemoteControl.Mac.Mapping;

namespace SwarmCore.RemoteControl.Mac.Native;

/// <summary>
/// Owns one +1 CoreFoundation reference for a lexical scope: <c>using var x = new CfRef(CFCopy...())</c>.
/// <see cref="Detach"/> hands ownership to the caller.
/// </summary>
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("maccatalyst")]
internal ref struct CfRef
{
    private nint _value;

    public CfRef(nint owned) { _value = owned; }

    public readonly nint Value => _value;
    public readonly bool IsNull => _value == 0;

    /// <summary>Stop owning the reference and return it; the caller now releases it.</summary>
    public nint Detach()
    {
        var v = _value;
        _value = 0;
        return v;
    }

    public void Dispose()
    {
        CF.Release(_value);
        _value = 0;
    }
}

/// <summary>
/// CFStrings for attribute/action/key names, created on first use and released by <see cref="Dispose"/>,
/// so every CFString this adapter creates has an owner.
/// </summary>
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("maccatalyst")]
internal sealed class CfStringCache : IDisposable
{
    private readonly Dictionary<string, nint> _strings = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private bool _disposed;

    public nint this[string name]
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_strings.TryGetValue(name, out var existing)) return existing;
                var created = Cf.CreateString(name);
                _strings[name] = created;
                return created;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var s in _strings.Values) CF.Release(s);
            _strings.Clear();
        }
    }
}

/// <summary>Conversions between CF objects and managed values. None of these take ownership of their argument.</summary>
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("maccatalyst")]
internal static unsafe class Cf
{
    private static nuint s_stringType, s_numberType, s_booleanType, s_arrayType, s_dictionaryType, s_axValueType, s_axElementType;

    public static nuint StringType => s_stringType != 0 ? s_stringType : (s_stringType = CF.CFStringGetTypeID());
    public static nuint NumberType => s_numberType != 0 ? s_numberType : (s_numberType = CF.CFNumberGetTypeID());
    public static nuint BooleanType => s_booleanType != 0 ? s_booleanType : (s_booleanType = CF.CFBooleanGetTypeID());
    public static nuint ArrayType => s_arrayType != 0 ? s_arrayType : (s_arrayType = CF.CFArrayGetTypeID());
    public static nuint DictionaryType => s_dictionaryType != 0 ? s_dictionaryType : (s_dictionaryType = CF.CFDictionaryGetTypeID());
    public static nuint AxValueType => s_axValueType != 0 ? s_axValueType : (s_axValueType = AX.AXValueGetTypeID());
    public static nuint AxElementType => s_axElementType != 0 ? s_axElementType : (s_axElementType = AX.AXUIElementGetTypeID());

    public static bool Is(nint cf, nuint typeId) => cf != 0 && CF.CFGetTypeID(cf) == typeId;

    /// <summary>Creates a +1 CFString from UTF-16 (CFStringCreateWithCharacters copies the buffer).</summary>
    public static nint CreateString(string value)
    {
        fixed (char* p = value)
        {
            var s = CF.CFStringCreateWithCharacters(0, p, value.Length);
            if (s == 0) throw new DesktopAutomationException("macOS could not create a string (CFStringCreateWithCharacters returned NULL).", FailureCategories.OsRefused);
            return s;
        }
    }

    /// <summary>Reads a CFString as UTF-16 via CFStringGetLength/CFStringGetCharacters; null when <paramref name="cf"/> is not a CFString.</summary>
    public static string? ToStringOrNull(nint cf)
    {
        if (!Is(cf, StringType)) return null;
        var length = CF.CFStringGetLength(cf);
        if (length <= 0) return string.Empty;
        return string.Create(checked((int)length), cf, static (span, str) =>
        {
            fixed (char* p = span)
            {
                CF.CFStringGetCharacters(str, new CFRange(0, span.Length), p);
            }
        });
    }

    public static double? ToDouble(nint cf)
    {
        if (Is(cf, NumberType))
        {
            double d;
            // CFNumberGetValue converts between number types; it returns false only for lossy conversion, and still stores the value.
            CF.CFNumberGetValue(cf, CF.kCFNumberDoubleType, &d);
            return d;
        }
        if (Is(cf, BooleanType)) return CF.CFBooleanGetValue(cf) ? 1 : 0;
        return null;
    }

    public static long? ToInt64(nint cf)
    {
        if (Is(cf, NumberType))
        {
            long v;
            CF.CFNumberGetValue(cf, CF.kCFNumberSInt64Type, &v);
            return v;
        }
        if (Is(cf, BooleanType)) return CF.CFBooleanGetValue(cf) ? 1 : 0;
        return null;
    }

    public static bool? ToBool(nint cf)
    {
        if (Is(cf, BooleanType)) return CF.CFBooleanGetValue(cf);
        if (Is(cf, NumberType)) return ToInt64(cf) != 0;
        return null;
    }

    /// <summary>Managed view of an AX attribute value: string, double (integral numbers too), bool, or null.</summary>
    public static object? ToScalar(nint cf)
    {
        if (cf == 0) return null;
        var type = CF.CFGetTypeID(cf);
        if (type == StringType) return ToStringOrNull(cf);
        if (type == BooleanType) return CF.CFBooleanGetValue(cf);
        if (type == NumberType) return ToDouble(cf);
        return null;
    }

    public static CGPoint? ToPoint(nint cf)
    {
        if (!Is(cf, AxValueType) || AX.AXValueGetType(cf) != AX.kAXValueTypeCGPoint) return null;
        CGPoint p;
        return AX.AXValueGetValue(cf, AX.kAXValueTypeCGPoint, &p) ? p : null;
    }

    public static CGSize? ToSize(nint cf)
    {
        if (!Is(cf, AxValueType) || AX.AXValueGetType(cf) != AX.kAXValueTypeCGSize) return null;
        CGSize s;
        return AX.AXValueGetValue(cf, AX.kAXValueTypeCGSize, &s) ? s : null;
    }

    public static CGRect? ToRect(nint cf)
    {
        if (Is(cf, AxValueType))
        {
            if (AX.AXValueGetType(cf) != AX.kAXValueTypeCGRect) return null;
            CGRect r;
            return AX.AXValueGetValue(cf, AX.kAXValueTypeCGRect, &r) ? r : null;
        }
        if (Is(cf, DictionaryType))
        {
            return CG.CGRectMakeWithDictionaryRepresentation(cf, out var r) ? r : null;
        }
        return null;
    }

    /// <summary>Strings of a CFArray of CFStrings (non-strings skipped).</summary>
    public static List<string> ToStringList(nint array)
    {
        var result = new List<string>();
        if (!Is(array, ArrayType)) return result;
        var count = CF.CFArrayGetCount(array);
        for (nint i = 0; i < count; i++)
        {
            var s = ToStringOrNull(CF.CFArrayGetValueAtIndex(array, i));
            if (s is not null) result.Add(s);
        }
        return result;
    }

    /// <summary>The elements of a CFArray, each CFRetained (+1) for the caller.</summary>
    public static List<nint> RetainArrayItems(nint array)
    {
        var result = new List<nint>();
        if (!Is(array, ArrayType)) return result;
        var count = CF.CFArrayGetCount(array);
        result.Capacity = (int)count;
        for (nint i = 0; i < count; i++)
        {
            var item = CF.CFArrayGetValueAtIndex(array, i);
            if (item != 0) result.Add(CF.CFRetain(item));
        }
        return result;
    }

    /// <summary>A +1 CFArray of the given CF objects (retained by the array via kCFTypeArrayCallBacks).</summary>
    public static nint CreateArray(ReadOnlySpan<nint> values)
    {
        fixed (nint* p = values)
        {
            return CF.CFArrayCreate(0, p, values.Length, CF.TypeArrayCallBacks);
        }
    }

    /// <summary>A +1 one-entry CFDictionary.</summary>
    public static nint CreateDictionary(nint key, nint value)
    {
        nint k = key, v = value;
        return CF.CFDictionaryCreate(0, &k, &v, 1, CF.TypeDictionaryKeyCallBacks, CF.TypeDictionaryValueCallBacks);
    }

    /// <summary>Copies a CFData's bytes.</summary>
    public static byte[] DataToBytes(nint data)
    {
        var length = (int)CF.CFDataGetLength(data);
        var bytes = new byte[length];
        if (length > 0) new ReadOnlySpan<byte>(CF.CFDataGetBytePtr(data), length).CopyTo(bytes);
        return bytes;
    }
}
