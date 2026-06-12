using System.Reflection;

namespace BigAmbitionsMP
{
    /// <summary>
    /// Property-or-field reflection access.  The IL2CPP interop layer exposed
    /// every Unity-serialized field as a PROPERTY, so the 0.10 code reflected
    /// with GetProperty everywhere.  On Mono (EA 0.11) those members are plain
    /// fields again — GetProperty returns null and the call sites silently
    /// no-op.  These helpers resolve a property first, then a field, so the
    /// same lookup works on both shapes.
    /// </summary>
    public static class MPReflect
    {
        public const BindingFlags All =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        /// <summary>Property or field named <paramref name="name"/>, or null.</summary>
        public static MemberInfo? PropertyOrField(Type? t, string name, BindingFlags flags = All)
        {
            if (t == null) return null;
            try { return (MemberInfo?)t.GetProperty(name, flags) ?? t.GetField(name, flags); }
            catch { return null; }
        }

        /// <summary>Read a property/field value (null when unresolvable).</summary>
        public static object? Get(MemberInfo? m, object? obj)
        {
            try
            {
                return m switch
                {
                    PropertyInfo p => p.GetValue(obj),
                    FieldInfo f    => f.GetValue(obj),
                    _              => null,
                };
            }
            catch { return null; }
        }

        /// <summary>One-shot read by name.</summary>
        public static object? Get(Type? t, object? obj, string name, BindingFlags flags = All)
            => Get(PropertyOrField(t, name, flags), obj);

        /// <summary>Write a property/field value; false when unresolvable/read-only.</summary>
        public static bool Set(MemberInfo? m, object? obj, object? value)
        {
            try
            {
                switch (m)
                {
                    case PropertyInfo p when p.CanWrite: p.SetValue(obj, value); return true;
                    case FieldInfo f:                    f.SetValue(obj, value); return true;
                    default: return false;
                }
            }
            catch { return false; }
        }

        /// <summary>The value type of a property/field member (null if neither).</summary>
        public static Type? TypeOf(MemberInfo? m) => m switch
        {
            PropertyInfo p => p.PropertyType,
            FieldInfo f    => f.FieldType,
            _              => null,
        };
    }
}
