using System;
using System.Reflection;

namespace UnityRemix
{
    // A completed Remix image has no matching motion vectors in Unity's camera.
    // Only suppress a second temporal resolve; spatial AA remains game-owned.
    internal sealed class TemporalAntialiasingOverride
    {
        private readonly object target;
        private readonly PropertyInfo property;
        private readonly FieldInfo field;
        private readonly Type valueType;
        private object original, applied;
        public string Current => Read()?.ToString() ?? "unknown";

        public TemporalAntialiasingOverride(object target, string member)
        {
            this.target = target;
            var flags = BindingFlags.Instance | BindingFlags.Public;
            property = target.GetType().GetProperty(member, flags);
            if (property != null && (!property.CanRead || !property.CanWrite)) property = null;
            if (property == null) field = target.GetType().GetField(member, flags);
            valueType = property?.PropertyType ?? field?.FieldType;
        }

        public bool Supported => valueType != null && valueType.IsEnum && Enum.IsDefined(valueType, "None");
        private object Read() => property != null ? property.GetValue(target) : field?.GetValue(target);
        private void Write(object value)
        {
            if (property != null) property.SetValue(target, value); else field.SetValue(target, value);
        }

        public bool Update()
        {
            if (!Supported) return false;
            object current = Read();
            string name = current.ToString();
            if (name.IndexOf("Temporal", StringComparison.OrdinalIgnoreCase) >= 0 ||
                string.Equals(name, "TAA", StringComparison.OrdinalIgnoreCase))
            {
                original = current;
                applied = Enum.Parse(valueType, "None");
                Write(applied);
                return true;
            }
            if (applied != null && !Equals(current, applied))
            {
                // The game/user selected another mode while Remix was active.
                original = applied = null;
            }
            return false;
        }

        public void Restore()
        {
            if (applied == null) return;
            if (Equals(Read(), applied)) Write(original);
            original = applied = null;
        }
    }
}
