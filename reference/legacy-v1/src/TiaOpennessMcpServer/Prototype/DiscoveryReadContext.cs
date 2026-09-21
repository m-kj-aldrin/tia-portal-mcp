using System.Collections;
using System.Globalization;

namespace TiaOpennessMcpServer.Prototype;

// Shared partial-read behavior. Never swallow a guard failure or continue after detected context loss.
internal sealed class DiscoveryReadContext
{
    private readonly List<DiscoveryError> _errors;
    private readonly Action _validate;

    public DiscoveryReadContext(List<DiscoveryError> errors, Action validate)
    {
        _errors = errors;
        _validate = validate;
    }

    public T? Read<T>(Func<T> read, string operation, string? path)
    {
        try { return read(); }
        catch (ConnectionFault) { throw; }
        catch (Exception ex)
        {
            Failure(ex, operation, path);
            return default;
        }
    }

    // Collection acquisition, MoveNext and individual element failures have separate boundaries.
    // Retain already-read siblings and continue after a failing element when its context remains valid.
    public List<R>? Collect<T, R>(Func<IEnumerable<T>> source, Func<T, R> map, string? path)
    {
        _validate();
        List<R>? result = null;
        try
        {
            using var iterator = source().GetEnumerator();
            result = new List<R>();
            while (iterator.MoveNext())
            {
                try { result.Add(map(iterator.Current)); }
                catch (ConnectionFault) { throw; }
                catch (Exception ex) { Failure(ex, "read", path); }
            }
        }
        catch (ConnectionFault) { throw; }
        catch (Exception ex) { Failure(ex, "enumerate", path); }
        return result;
    }

    public void Failure(Exception ex, string operation, string? path)
    {
        try { _validate(); }
        catch (ConnectionFault fault)
        {
            throw new ConnectionFault(fault.Code, fault.ProcessId, fault.Message, ex);
        }
        _errors.Add(new DiscoveryError { Operation = operation, Path = path, Message = ex.Message });
    }
}

internal static class DiscoveryValues
{
    public static string? Nonblank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    public static object? Convert(object? value)
    {
        if (value == null || value is string || value is bool || value is byte || value is sbyte ||
            value is short || value is ushort || value is int || value is uint || value is long ||
            value is ulong || value is decimal) return value;
        if (value is float single && !float.IsNaN(single) && !float.IsInfinity(single)) return single;
        if (value is double number && !double.IsNaN(number) && !double.IsInfinity(number)) return number;
        if (value is Enum) return value.ToString();
        if (value is DateTime date) return date.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        if (value is DateTimeOffset offset) return offset.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        // Only materialized simple collections; do not walk native compositions or arbitrary proxies.
        if (value is Array || value is IList)
        {
            var entries = new List<object?>();
            foreach (var item in (IEnumerable)value)
            {
                if (item is Array || item is IList || ReferenceEquals(value, item)) return Unserialized(value);
                entries.Add(Convert(item));
            }
            return entries;
        }
        return Unserialized(value);
    }

    private static object Unserialized(object value) => new Dictionary<string, object?>
    {
        ["nativeType"] = value.GetType().FullName,
        ["valueSerialized"] = false
    };
}
