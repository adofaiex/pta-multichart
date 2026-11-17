using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace PtaBridge
{
    internal static class ReflectInvoke
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static readonly Dictionary<string, Type> TypeCache = new Dictionary<string, Type>(StringComparer.Ordinal);

        public static object Execute(string typeName, string member, List<object> callArgs)
        {
            if (string.IsNullOrEmpty(typeName)) throw new BridgeError("invoke requires 'type'");
            if (string.IsNullOrEmpty(member)) throw new BridgeError("invoke requires 'member'");

            Type type = ResolveType(typeName);
            if (type == null) throw new BridgeError("type not found: " + typeName + " (try full name, e.g. scnEditor or ADOFAI.LevelData)");

            return InvokeMember(type, member, callArgs);
        }

        private static Type ResolveType(string name)
        {
            lock (TypeCache)
            {
                if (TypeCache.TryGetValue(name, out Type cached)) return cached;
            }

            string simple = name;
            int dot = name.LastIndexOf('.');
            if (dot >= 0 && dot + 1 < name.Length) simple = name.Substring(dot + 1);

            Type found = null;
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types; }
                catch { continue; }
                if (types == null) continue;

                foreach (Type t in types)
                {
                    if (t == null) continue;
                    if (string.Equals(t.FullName, name, StringComparison.Ordinal) ||
                        string.Equals(t.Name, simple, StringComparison.Ordinal))
                    {
                        found = t;
                        break;
                    }
                }
                if (found != null) break;
            }

            if (found != null)
            {
                lock (TypeCache) { TypeCache[name] = found; }
            }
            return found;
        }

        private static object InvokeMember(Type type, string member, List<object> args)
        {
            const BindingFlags F = BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy;
            int argc = args == null ? 0 : args.Count;

            PropertyInfo prop = null;
            try { prop = type.GetProperty(member, F); } catch { }
            if (prop != null && prop.GetIndexParameters().Length == 0)
            {
                if (argc == 0) return ToJsonValue(prop.GetValue(null, null));
                if (argc == 1)
                {
                    prop.SetValue(null, ConvertArg(args[0], prop.PropertyType), null);
                    return ToJsonValue(prop.GetValue(null, null));
                }
                throw new BridgeError("property " + type.Name + "." + member + " takes 0 or 1 arguments, got " + argc);
            }

            FieldInfo field = null;
            try { field = type.GetField(member, F); } catch { }
            if (field != null)
            {
                if (argc == 0) return ToJsonValue(field.GetValue(null));
                if (argc == 1)
                {
                    field.SetValue(null, ConvertArg(args[0], field.FieldType));
                    return ToJsonValue(field.GetValue(null));
                }
                throw new BridgeError("field " + type.Name + "." + member + " takes 0 or 1 arguments, got " + argc);
            }

            MethodInfo chosen = null;
            object[] converted = null;
            string lastError = null;
            MethodInfo[] methods;
            try { methods = type.GetMethods(F); } catch { methods = new MethodInfo[0]; }

            foreach (MethodInfo m in methods)
            {
                if (!string.Equals(m.Name, member, StringComparison.Ordinal)) continue;
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length != argc) continue;

                var conv = new object[argc];
                bool ok = true;
                for (int i = 0; i < argc; i++)
                {
                    try { conv[i] = ConvertArg(args[i], ps[i].ParameterType); }
                    catch (Exception ex) { lastError = ex.Message; ok = false; break; }
                }
                if (!ok) continue;
                chosen = m;
                converted = conv;
                break;
            }

            if (chosen == null)
            {
                string available = string.Join(", ", MemberNames(type));
                string detail = lastError == null ? string.Empty : " (last conversion error: " + lastError + ")";
                throw new BridgeError("member '" + member + "' with " + argc + " arg(s) not found on " +
                                      type.FullName + detail + "; available static members: " + available);
            }

            return ToJsonValue(chosen.Invoke(null, converted));
        }

        private static List<string> MemberNames(Type type)
        {
            var names = new List<string>();
            const BindingFlags F = BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy;
            try
            {
                foreach (PropertyInfo p in type.GetProperties(F))
                    if (p.GetIndexParameters().Length == 0) names.Add(p.Name);
                foreach (FieldInfo f in type.GetFields(F)) names.Add(f.Name);
                foreach (MethodInfo m in type.GetMethods(F))
                    if (!m.IsSpecialName && !names.Contains(m.Name)) names.Add(m.Name);
            }
            catch { }
            names.Sort(StringComparer.Ordinal);
            if (names.Count > 60) names = names.GetRange(0, 60);
            return names;
        }

        private static object ConvertArg(object value, Type target)
        {
            if (target == typeof(object)) return value;
            if (value == null)
            {
                if (target.IsValueType) throw new BridgeError("null not allowed for " + target.Name);
                return null;
            }
            if (target == typeof(string)) return (value as string) ?? Convert.ToString(value, Inv);
            if (target.IsEnum)
            {
                if (value is string str) return Enum.Parse(target, str, true);
                return Enum.ToObject(target, Convert.ToInt64(value, Inv));
            }
            if (target == typeof(bool))
            {
                if (value is string b) return bool.Parse(b);
                return Convert.ToBoolean(value, Inv);
            }
            if (target == typeof(int)) return Convert.ToInt32(value, Inv);
            if (target == typeof(long)) return Convert.ToInt64(value, Inv);
            if (target == typeof(short)) return Convert.ToInt16(value, Inv);
            if (target == typeof(byte)) return Convert.ToByte(value, Inv);
            if (target == typeof(sbyte)) return Convert.ToSByte(value, Inv);
            if (target == typeof(uint)) return Convert.ToUInt32(value, Inv);
            if (target == typeof(ulong)) return Convert.ToUInt64(value, Inv);
            if (target == typeof(ushort)) return Convert.ToUInt16(value, Inv);
            if (target == typeof(float)) return Convert.ToSingle(value, Inv);
            if (target == typeof(double)) return Convert.ToDouble(value, Inv);
            if (target == typeof(decimal)) return Convert.ToDecimal(value, Inv);
            if (target == typeof(char))
            {
                if (value is string str && str.Length == 1) return str[0];
                return Convert.ToChar(value, Inv);
            }
            if (target.IsInstanceOfType(value)) return value;
            return Convert.ChangeType(value, target, Inv);
        }

        private static object ToJsonValue(object value)
        {
            if (value == null) return null;
            if (value is string || value is bool) return value;
            Type t = value.GetType();
            if (t.IsPrimitive || value is decimal) return value;
            if (t.IsEnum) return value.ToString();
            try { return value.ToString(); }
            catch { return "<unprintable " + t.Name + ">"; }
        }
    }
}
