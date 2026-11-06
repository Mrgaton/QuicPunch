using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuicNet.Serialization;

/// <summary>Release contract checks use synthetic values without peers or user data.</summary>
public static class AotContractValidation
{
    public static int Run()
    {
        var types = typeof(SharedJsonContext).GetCustomAttributesData().Where(a => a.AttributeType == typeof(JsonSerializableAttribute)).Select(a => (Type)a.ConstructorArguments[0].Value!).Distinct();
        int tested = 0;
        foreach (var type in types)
        {
            object? value = Sample(type, 0);
            byte[] generated = JsonSerializer.SerializeToUtf8Bytes(value, type, SharedJson.Options);
            if (RuntimeFeature.IsDynamicCodeSupported)
            {
                byte[] original = JsonSerializer.SerializeToUtf8Bytes(value, type);
                if (!generated.AsSpan().SequenceEqual(original))
                    throw new InvalidDataException("Generated JSON changed the contract: " + type.FullName);
            }
            object? decoded = JsonSerializer.Deserialize(generated, type, SharedJson.Options);
            byte[] roundTrip = JsonSerializer.SerializeToUtf8Bytes(decoded, type, SharedJson.Options);
            if (!generated.AsSpan().SequenceEqual(roundTrip))
                throw new InvalidDataException("Generated JSON round-trip failed: " + type.FullName);
            tested++;
        }
        return tested;
    }
    private static object? Sample(Type type, int depth)
    {
        if (Nullable.GetUnderlyingType(type) != null) return null;
        if (type == typeof(string)) return "á<&\" / sample";
        if (type == typeof(object)) return "sample";
        if (type == typeof(bool)) return true;
        if (type == typeof(Guid)) return Guid.Parse("327e0b69-f3f6-4340-bbeb-19742ddde601");
        if (type == typeof(DateTime)) return new DateTime(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc);
        if (type == typeof(DateTimeOffset)) return new DateTimeOffset(2026, 10, 15, 10, 0, 0, TimeSpan.Zero);
        if (type == typeof(TimeSpan)) return TimeSpan.FromMilliseconds(15);
        if (type == typeof(JsonElement)) return JsonDocument.Parse("{\"sample\":true}").RootElement.Clone();
        if (type.IsEnum) return Enum.GetValues(type).GetValue(0);
        if (type.IsPrimitive || type == typeof(decimal)) return Convert.ChangeType(17, type, System.Globalization.CultureInfo.InvariantCulture);
        if (type.IsArray)
        {
            var array = Array.CreateInstance(type.GetElementType()!, 1);
            array.SetValue(Sample(type.GetElementType()!, depth + 1), 0);
            return array;
        }
        if (depth > 5) return type.IsValueType ? Activator.CreateInstance(type) : null;
        if (type.IsInterface || type.IsAbstract) return null;
        var constructor = type.GetConstructors().OrderBy(c => c.GetParameters().Length).FirstOrDefault();
        if (constructor == null) return type.IsValueType ? Activator.CreateInstance(type) : null;
        return constructor.Invoke(constructor.GetParameters().Select(p => Sample(p.ParameterType, depth + 1)).ToArray());
    }
}
