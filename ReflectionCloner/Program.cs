using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;

namespace ReflectionCloner
{
    public interface ICloningService
    {
        T Clone<T>(T source);
        T IterateClone<T>(T source);
        T CloneInternal<T>(T source);
    }

    public class CloningService : ICloningService
    {
        private static readonly Func<Type, string, Type, MethodInfo> GetTypedMethod = (classType, methodName, genericType) =>
        {
            return classType.GetMethod(methodName).MakeGenericMethod(genericType);
        };

        private readonly Dictionary<Type, MethodInfo> _methodInfoCache = new Dictionary<Type, MethodInfo>();
        private static MethodInfo GetTypedCloneMethodInfo(
            Dictionary<Type, MethodInfo> methodInfoCache, ICloningService cloner, Type fieldType)
        {
            if (methodInfoCache.TryGetValue(fieldType, out MethodInfo existMethodInfo))
            {
                return existMethodInfo;
            }
            else
            {
                MethodInfo newCloneMethodInfo = GetTypedMethod(typeof(ICloningService), "Clone", fieldType);
                methodInfoCache.Add(fieldType, newCloneMethodInfo);
                return newCloneMethodInfo;
            }
        }

        private static readonly Func<FieldInfo, object, object> FieldGetterFunc = (Func<FieldInfo, object, object>)Delegate.CreateDelegate(
            typeof(Func<FieldInfo, object, object>), typeof(FieldInfo).GetMethod("GetValue", BindingFlags.Public | BindingFlags.Instance)
        );

        private static readonly Action<FieldInfo, object, object> FieldSetterFunc = (Action<FieldInfo, object, object>)Delegate.CreateDelegate(
            typeof(Action<FieldInfo, object, object>), typeof(FieldInfo).GetMethod(
                "SetValue", BindingFlags.Public | BindingFlags.Instance, null, new Type[] { typeof(object), typeof(object) }, new ParameterModifier[] { })
        );

        private static readonly Func<PropertyInfo, object, object> PropertyGetterFunc = (Func<PropertyInfo, object, object>)Delegate.CreateDelegate(
            typeof(Func<PropertyInfo, object, object>), typeof(PropertyInfo).GetMethod(
                "GetValue", BindingFlags.Public | BindingFlags.Instance, null, new Type[] { typeof(object) }, new ParameterModifier[] { })
        );

        private static readonly Action<PropertyInfo, object, object> PropertySetterFunc = (Action<PropertyInfo, object, object>)Delegate.CreateDelegate(
            typeof(Action<PropertyInfo, object, object>), typeof(PropertyInfo).GetMethod(
                "SetValue", BindingFlags.Public | BindingFlags.Instance, null, new Type[] { typeof(object), typeof(object) }, new ParameterModifier[] { })
        );

        private readonly Dictionary<MemberInfo, CloneableAttribute> _attributeCache = new Dictionary<MemberInfo, CloneableAttribute>();
        private static CloneableAttribute GetCachedAttribute(
            Dictionary<MemberInfo, CloneableAttribute> attributeCache, MemberInfo memberInfo)
        {
            if (attributeCache.TryGetValue(memberInfo, out CloneableAttribute existAttribute))
            {
                return existAttribute;
            }
            else
            {
                CloneableAttribute newAttribute = memberInfo.GetCustomAttribute<CloneableAttribute>(false);
                attributeCache.Add(memberInfo, newAttribute);
                return newAttribute;
            }
        }

        private static readonly Func<object, object> BaseCloneFunc = (Func<object, object>)Delegate.CreateDelegate(
            typeof(Func<object, object>), typeof(object).GetMethod("MemberwiseClone", BindingFlags.NonPublic | BindingFlags.Instance)
        );

        private readonly Dictionary<object, object> _clonedMap = new Dictionary<object, object>();
        private readonly Action<Dictionary<object, object>, object, object> AddToCacheMap = (clonedMap, sourceItem, clonedItem) =>
        {
            if (sourceItem != null)
                if (!clonedMap.TryGetValue(sourceItem, out object value))
                {
                    clonedMap.Add(sourceItem, clonedItem);
                }
        };

        #region Iteration version

        public T CloneInternal<T>(T source)
        {
            if(!CheckIfDeepCopyAwailable(source))
            {
                return source;
            }

            if (_clonedMap.TryGetValue(source, out object value))
            {
                return (T)_clonedMap[source];
            }
            object destination = BaseCloneFunc(source);
            AddToCacheMap(_clonedMap, source, destination);

            return (T)destination;
        }

        private readonly Func<object, bool> CheckIfDeepCopyAwailable = (o) =>
        {
            if (o == null)
            {
                return false;
            }

            Type sourceType = o.GetType();

            if (
                sourceType.Equals(typeof(string)) ||
                sourceType.IsPrimitive ||
                sourceType.IsEnum
            )
            {
                return false;
            }

            return true;
        };

        private readonly Action<object, MemberStorage> SetElementsForSourceObject =
            (source, memberStorage) =>
            {
                Type sourceType = source.GetType();
                memberStorage.SourceType = sourceType;
                if (sourceType.IsArray)
                {
                    if (!sourceType.GetElementType().IsPrimitive)
                    {
                        memberStorage.ArrayElements = source as object[];
                    }
                    memberStorage.IsArray = true;
                }
                else if (sourceType.IsGenericType && typeof(IEnumerable).IsAssignableFrom(sourceType))
                {
                    memberStorage.GenericElements = source as IList;
                    memberStorage.IsGeneric = true;
                }
                else if (sourceType.IsClass || sourceType.IsValueType)
                {
                    memberStorage.FieldInfoElements = sourceType.GetFields(BindingFlags.Public | BindingFlags.Instance);
                    memberStorage.PropertyInfoElements = sourceType.GetProperties(BindingFlags.Public | BindingFlags.Instance);
                    memberStorage.IsClassOrValueType = true;
                }
            };

        private class MemberStorage
        {
            public MemberStorage()
            {
                ArrayElements = Array.Empty<object>();
                GenericElements = Array.Empty<object>();
                FieldInfoElements = Array.Empty<FieldInfo>();
                PropertyInfoElements = Array.Empty<PropertyInfo>();
                SourceType = null;
                IsArray = false;
                IsGeneric = false;
                IsClassOrValueType = false;
            }
            public object[] ArrayElements { get; set; }
            public IList GenericElements { get; set; }
            public FieldInfo[] FieldInfoElements { get; set; }
            public PropertyInfo[] PropertyInfoElements { get; set; }
            public Type SourceType { get; set; }
            public bool IsArray { get; set; }
            public bool IsGeneric { get; set; }
            public bool IsClassOrValueType { get; set; }
        }

        public T IterateClone<T>(T source)
        {
            Stack<KeyValuePair<object, object>> stack = new Stack<KeyValuePair<object, object>>();

            object destination = BaseCloneFunc(source);
            AddToCacheMap(_clonedMap, source, destination);

            stack.Push(new KeyValuePair<object, object>(source, destination));

            while (stack.Count > 0)
            {
                KeyValuePair<object, object> nextStackElement = stack.Pop();
                object sourceObject = nextStackElement.Key;
                object destinationObject = nextStackElement.Value;
                MemberStorage memberStorage = new MemberStorage();
                SetElementsForSourceObject(sourceObject, memberStorage);

                int aeLength = memberStorage.ArrayElements.Length;
                int geCount = memberStorage.GenericElements.Count;
                int fieCount = memberStorage.FieldInfoElements.Count();
                int pieCount = memberStorage.PropertyInfoElements.Count();
                if (aeLength > 0 || geCount > 0 || fieCount > 0 || pieCount > 0)
                {
                    if (memberStorage.IsArray)
                    {
                        if (!memberStorage.SourceType.GetElementType().IsPrimitive)
                        {
                            Array destinationArray = destinationObject as Array;

                            for (int i = 0; i < aeLength; i++)
                            {
                                object sourceItem = memberStorage.ArrayElements.GetValue(i);
                                Type elementType = sourceItem.GetType();
                                int clonedMapCountDiff = _clonedMap.Count;
                                object clonedItem = GetTypedCloneMethodInfo(_methodInfoCache, this, elementType).Invoke(this, new object[] { sourceItem });
                                clonedMapCountDiff = _clonedMap.Count - clonedMapCountDiff;
                                destinationArray.SetValue(clonedItem, i);

                                if (CheckIfDeepCopyAwailable(sourceItem) && clonedMapCountDiff == 1)
                                {
                                    stack.Push(new KeyValuePair<object, object>(sourceItem, clonedItem));
                                }
                            }
                        }
                    }
                    else if (memberStorage.IsGeneric)
                    {
                        IList destinationList = destinationObject as IList;
                        Type elementType = memberStorage.SourceType.GenericTypeArguments.First();

                        for (int i = 0; i < geCount; i++)
                        {
                            object sourceItem = memberStorage.GenericElements[i];
                            int clonedMapCountDiff = _clonedMap.Count;
                            object clonedItem = GetTypedCloneMethodInfo(_methodInfoCache, this, elementType).Invoke(this, new object[] { sourceItem });
                            clonedMapCountDiff = _clonedMap.Count - clonedMapCountDiff;
                            destinationList[i] = clonedItem;

                            if (CheckIfDeepCopyAwailable(sourceItem) && clonedMapCountDiff == 1)
                            {
                                stack.Push(new KeyValuePair<object, object>(sourceItem, clonedItem));
                            }
                        }
                    }
                    else if (memberStorage.IsClassOrValueType)
                    {
                        foreach (FieldInfo field in memberStorage.FieldInfoElements)
                        {
                            var cloneableAttribute = GetCachedAttribute(_attributeCache, field);
                            if (cloneableAttribute == null)
                            {
                                object sourceItem = field.GetValue(sourceObject);
                                int clonedMapCountDiff = _clonedMap.Count;
                                object clonedItem = GetTypedCloneMethodInfo(_methodInfoCache, this, field.FieldType).Invoke(this, new object[] { sourceItem });
                                clonedMapCountDiff = _clonedMap.Count - clonedMapCountDiff;
                                field.SetValue(destinationObject, clonedItem);

                                if (CheckIfDeepCopyAwailable(sourceItem) && clonedMapCountDiff == 1)
                                {
                                    stack.Push(new KeyValuePair<object, object>(sourceItem, clonedItem));
                                }
                            }
                            else
                            {
                                switch (cloneableAttribute.Mode)
                                {
                                    case CloningMode.Shallow:
                                        {
                                            field.SetValue(destinationObject, field.GetValue(sourceObject));
                                            break;
                                        }
                                    case CloningMode.Ignore:
                                        {
                                            field.SetValue(destinationObject, null);
                                            break;
                                        }
                                    case CloningMode.Deep:
                                    default:
                                        {
                                            object sourceItem = field.GetValue(sourceObject);
                                            int clonedMapCountDiff = _clonedMap.Count;
                                            object clonedItem = GetTypedCloneMethodInfo(_methodInfoCache, this, field.FieldType).Invoke(this, new object[] { sourceItem });
                                            clonedMapCountDiff = _clonedMap.Count - clonedMapCountDiff;
                                            field.SetValue(destinationObject, clonedItem);

                                            if (CheckIfDeepCopyAwailable(sourceItem) && clonedMapCountDiff == 1)
                                            {
                                                stack.Push(new KeyValuePair<object, object>(sourceItem, clonedItem));
                                            }
                                            break;
                                        }
                                }
                            }
                        }

                        foreach (PropertyInfo property in memberStorage.PropertyInfoElements)
                        {
                            var cloneableAttribute = GetCachedAttribute(_attributeCache, property);
                            if (cloneableAttribute == null)
                            {
                                if (property.SetMethod != null)
                                {
                                    object sourceItem = property.GetValue(sourceObject);
                                    int clonedMapCountDiff = _clonedMap.Count;
                                    object clonedItem = GetTypedCloneMethodInfo(_methodInfoCache, this, property.PropertyType).Invoke(this, new object[] { sourceItem });
                                    clonedMapCountDiff = _clonedMap.Count - clonedMapCountDiff;
                                    property.SetValue(destinationObject, clonedItem);

                                    if (CheckIfDeepCopyAwailable(sourceItem) && clonedMapCountDiff == 1)
                                    {
                                        stack.Push(new KeyValuePair<object, object>(sourceItem, clonedItem));
                                    }
                                }
                                continue;
                            }
                            else
                            {
                                switch (cloneableAttribute.Mode)
                                {
                                    case CloningMode.Shallow:
                                        {
                                            property.SetValue(destinationObject, property.GetValue(sourceObject));
                                            break;
                                        }
                                    case CloningMode.Ignore:
                                        {
                                            property.SetValue(destinationObject, null);
                                            break;
                                        }
                                    case CloningMode.Deep:
                                    default:
                                        {
                                            if (property.SetMethod != null)
                                            {
                                                object sourceItem = property.GetValue(sourceObject);
                                                int clonedMapCountDiff = _clonedMap.Count;
                                                object clonedItem = GetTypedCloneMethodInfo(_methodInfoCache, this, property.PropertyType).Invoke(this, new object[] { sourceItem });
                                                clonedMapCountDiff = _clonedMap.Count - clonedMapCountDiff;
                                                property.SetValue(destinationObject, clonedItem);

                                                if (CheckIfDeepCopyAwailable(sourceItem) && clonedMapCountDiff == 1)
                                                {
                                                    stack.Push(new KeyValuePair<object, object>(sourceItem, clonedItem));
                                                }
                                                break;
                                            }
                                            continue;
                                        }
                                }
                            }
                        }
                    }
                    else
                    {
                        throw new ArgumentException($"I don't know hot to copy type {memberStorage.SourceType}");
                    }
                }
            }

            return (T)destination;
        }

        #endregion

        #region Recursive version

        public T Clone<T>(T source)
        {
            if (source == null)
            {
                return source;
            }

            Type sourceType = typeof(T);

            if (
                sourceType.Equals(typeof(string)) ||
                sourceType.IsPrimitive ||
                sourceType.IsEnum
            )
            {
                return source;
            }

            if (_clonedMap.TryGetValue(source, out object value))
            {
                return (T)_clonedMap[source];
            }
            object clonedObject = BaseCloneFunc(source);
            AddToCacheMap(_clonedMap, source, clonedObject);

            if (sourceType.IsArray)
            {
                if (!sourceType.GetElementType().IsPrimitive)
                {
                    Array sourceArray = source as Array;
                    Array destinationArray = clonedObject as Array;

                    for (int i = 0; i < sourceArray.Length; i++)
                    {
                        object sourceItem = sourceArray.GetValue(i);
                        Type elementType = sourceItem.GetType();
                        object clonedItem = GetTypedCloneMethodInfo(_methodInfoCache, this, elementType).Invoke(this, new object[] { sourceItem });
                        destinationArray.SetValue(clonedItem, i);
                    }
                }
            }

            else if (sourceType.IsGenericType && typeof(IEnumerable).IsAssignableFrom(sourceType))
            {
                IList sourceList = source as IList;
                IList destinationList = clonedObject as IList;
                Type elementType = sourceType.GenericTypeArguments.First();

                for (int i = 0; i < sourceList.Count; i++)
                {
                    object sourceItem = sourceList[i];
                    object clonedItem = GetTypedCloneMethodInfo(_methodInfoCache, this, elementType).Invoke(this, new object[] { sourceItem });
                    destinationList[i] = clonedItem;
                }
            }

            else if (sourceType.IsClass || sourceType.IsValueType)
            {
                FieldInfo[] fields = sourceType.GetFields(BindingFlags.Public | BindingFlags.Instance);
                PropertyInfo[] properties = sourceType.GetProperties(BindingFlags.Public | BindingFlags.Instance);

                foreach (FieldInfo field in fields)
                {
                    var cloneableAttribute = GetCachedAttribute(_attributeCache, field);
                    if (cloneableAttribute == null)
                    {
                        object sourceItem = FieldGetterFunc(field, source);
                        object clonedItem = GetTypedCloneMethodInfo(_methodInfoCache, this, field.FieldType).Invoke(this, new object[] { sourceItem });
                        FieldSetterFunc(field, clonedObject, clonedItem);
                    }
                    else
                    {
                        switch (cloneableAttribute.Mode)
                        {
                            case CloningMode.Shallow:
                                {
                                    FieldSetterFunc(field, clonedObject, FieldGetterFunc(field, source));
                                    break;
                                }
                            case CloningMode.Ignore:
                                {
                                    FieldSetterFunc(field, clonedObject, null);
                                    break;
                                }
                            case CloningMode.Deep:
                            default:
                                {
                                    object sourceItem = FieldGetterFunc(field, source);
                                    object clonedItem = GetTypedCloneMethodInfo(_methodInfoCache, this, field.FieldType).Invoke(this, new object[] { sourceItem });
                                    FieldSetterFunc(field, clonedObject, clonedItem);
                                    break;
                                }
                        }
                    }
                }

                foreach (PropertyInfo property in properties)
                {
                    var cloneableAttribute = GetCachedAttribute(_attributeCache, property);
                    if (cloneableAttribute == null)
                    {
                        if (property.SetMethod != null)
                        {
                            object sourceItem = PropertyGetterFunc(property, source);
                            object clonedItem = GetTypedCloneMethodInfo(_methodInfoCache, this, property.PropertyType).Invoke(this, new object[] { sourceItem });
                            PropertySetterFunc(property, clonedObject, clonedItem);
                        }
                        continue;
                    }
                    else
                    {
                        switch (cloneableAttribute.Mode)
                        {
                            case CloningMode.Shallow:
                                {
                                    PropertySetterFunc(property, clonedObject, PropertyGetterFunc(property, source));
                                    break;
                                }
                            case CloningMode.Ignore:
                                {
                                    PropertySetterFunc(property, clonedObject, null);
                                    break;
                                }
                            case CloningMode.Deep:
                            default:
                                {
                                    if (property.SetMethod != null)
                                    {
                                        object sourceItem = PropertyGetterFunc(property, source);
                                        object clonedItem = GetTypedCloneMethodInfo(_methodInfoCache, this, property.PropertyType).Invoke(this, new object[] { sourceItem });
                                        PropertySetterFunc(property, clonedObject, clonedItem);
                                        break;
                                    }
                                    continue;
                                }
                        }
                    }
                }
            }

            else
            {
                throw new ArgumentException($"I don't know hot to copy type {sourceType}");
            }

            return (T)clonedObject;
        }

        #endregion
    }

    public enum CloningMode
    {
        Deep = 0,
        Shallow = 1,
        Ignore = 2,
    }

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public sealed class CloneableAttribute : Attribute
    {
        public CloningMode Mode { get; }

        public CloneableAttribute(CloningMode mode)
        {
            Mode = mode;
        }
    }

    public class CloningServiceTest
    {
        public class Simple
        {
            public int I;
            public string S { get; set; }
            [Cloneable(CloningMode.Ignore)]
            public string Ignored { get; set; }
            [Cloneable(CloningMode.Shallow)]
            public object Shallow { get; set; }

            public virtual string Computed => S + I + Shallow;
        }

        public struct SimpleStruct
        {
            public int I;
            public string S { get; set; }
            [Cloneable(CloningMode.Ignore)]
            public string Ignored { get; set; }

            public string Computed => S + I;

            public SimpleStruct(int i, string s)
            {
                I = i;
                S = s;
                Ignored = null;
            }
        }

        public class Simple2 : Simple
        {
            public double D;
            public SimpleStruct SS;
            public override string Computed => S + I + D + SS.Computed;
        }

        public class Node
        {
            public Node Left;
            public Node Right;
            public object Value;
            public int TotalNodeCount =>
                1 + (Left?.TotalNodeCount ?? 0) + (Right?.TotalNodeCount ?? 0);
        }

        public ICloningService Cloner = new CloningService();
        public Action[] AllTests => new Action[] {
            SimpleTest,
            SimpleStructTest,
            Simple2Test,
            NodeTest,
            ArrayTest,
            CollectionTest,
            ArrayTest2,
            CollectionTest2,
            MixedCollectionTest,
            RecursionTest,
            RecursionTest2,
            PerformanceTest,
        };

        public static void Assert(bool criteria)
        {
            if (!criteria)
                throw new InvalidOperationException("Assertion failed.");
        }

        public void MeasureWarmup(string title, Action test)
        {
            var sw_0 = new Stopwatch();
            sw_0.Start();
            test(); // Warmup
            sw_0.Stop();
            Console.WriteLine($"(Warmup) {title}: {sw_0.Elapsed.TotalMilliseconds:0.0000}ms");
        }

        public void Measure(string title, Action test)
        {
            var sw = new Stopwatch();
            GC.Collect();
            sw.Start();
            test();
            sw.Stop();
            Console.WriteLine($"{title}: {sw.Elapsed.TotalMilliseconds:0.0000}ms");
        }

        public void SimpleTest()
        {
            var s = new Simple() { I = 1, S = "2", Ignored = "3", Shallow = new object() };
            var c = Cloner.Clone(s);
            Assert(s != c);
            Assert(s.Computed == c.Computed);
            Assert(c.Ignored == null);
            Assert(ReferenceEquals(s.Shallow, c.Shallow));
        }

        public void SimpleStructTest()
        {
            var s = new SimpleStruct(1, "2") { Ignored = "3" };
            var c = Cloner.Clone(s);
            Assert(s.Computed == c.Computed);
            Assert(c.Ignored == null);
        }

        public void Simple2Test()
        {
            var s = new Simple2()
            {
                I = 1,
                S = "2",
                D = 3,
                SS = new SimpleStruct(3, "4"),
            };
            var c = Cloner.Clone(s);
            Assert(s != c);
            Assert(s.Computed == c.Computed);
        }

        public void NodeTest()
        {
            var s = new Node
            {
                Left = new Node
                {
                    Right = new Node()
                },
                Right = new Node()
            };
            var c = Cloner.Clone(s);
            Assert(s != c);
            Assert(s.TotalNodeCount == c.TotalNodeCount);
        }

        public void ArrayTest()
        {
            var n = new Node
            {
                Left = new Node
                {
                    Right = new Node()
                },
                Right = new Node()
            };
            var s = new[] { n, n };
            var c = Cloner.Clone(s);
            Assert(s != c);
            Assert(s.Sum(n1 => n1.TotalNodeCount) == c.Sum(n1 => n1.TotalNodeCount));
            Assert(c[0] == c[1]);
        }

        public void CollectionTest()
        {
            var n = new Node
            {
                Left = new Node
                {
                    Right = new Node()
                },
                Right = new Node()
            };
            var s = new List<Node>() { n, n };
            var c = Cloner.Clone(s);
            Assert(s != c);
            Assert(s.Sum(n1 => n1.TotalNodeCount) == c.Sum(n1 => n1.TotalNodeCount));
            Assert(c[0] == c[1]);
        }

        public void ArrayTest2()
        {
            var s = new[] { new[] { 1, 2, 3 }, new[] { 4, 5 } };
            var c = Cloner.Clone(s);
            Assert(s != c);
            Assert(15 == c.SelectMany(a => a).Sum());
        }

        public void CollectionTest2()
        {
            var s = new List<List<int>> { new List<int> { 1, 2, 3 }, new List<int> { 4, 5 } };
            var c = Cloner.Clone(s);
            Assert(s != c);
            Assert(15 == c.SelectMany(a => a).Sum());
        }

        public void MixedCollectionTest()
        {
            var s = new List<IEnumerable<int[]>> {
                new List<int[]> {new [] {1}},
                new List<int[]> {new [] {2, 3}},
            };
            var c = Cloner.Clone(s);
            Assert(s != c);
            Assert(6 == c.SelectMany(a => a.SelectMany(b => b)).Sum());
        }

        public void RecursionTest()
        {
            var s = new Node();
            s.Left = s;
            var c = Cloner.Clone(s);
            Assert(s != c);
            Assert(null == c.Right);
            Assert(c == c.Left);
        }

        public void RecursionTest2()
        {
            var l = new List<Node>();
            var n = new Node { Value = l };
            n.Left = n;
            l.Add(n);
            var s = new object[] { null, l, n };
            s[0] = s;
            var c = Cloner.Clone(s);
            Assert(s != c);
            Assert(c[0] == c);
            var cl = (List<Node>)c[1];
            Assert(l != cl);
            var cn = cl[0];
            Assert(n != cn);
            Assert(cl == cn.Value);
            Assert(cn.Left == cn);
        }

        public void PerformanceTest()
        {
            Func<int, Node> makeTree = null;
            makeTree = depth => {
                if (depth == 0)
                    return null;
                return new Node
                {
                    Value = depth,
                    Left = makeTree(depth - 1),
                    Right = makeTree(depth - 1),
                };
            };
            for (var i = 10; i <= 20; i++)
            {
                var root = makeTree(i);
                MeasureWarmup($"Cloning {root.TotalNodeCount} nodes", () => {
                    var copy = Cloner.Clone(root);
                    Assert(root != copy);
                });
                Measure($"Cloning {root.TotalNodeCount} nodes", () => {
                    var copy = Cloner.Clone(root);
                    Assert(root != copy);
                });
            }
        }

        public void RunAllTests()
        {
            foreach (var test in AllTests)
                test.Invoke();
            Console.WriteLine("Done.");
        }
    }

    public class Solution
    {
        public static void Main(string[] args)
        {
            var cloningServiceTest = new CloningServiceTest();
            var allTests = cloningServiceTest.AllTests;
            while (true)
            {
                var line = Console.ReadLine();
                if (string.IsNullOrEmpty(line))
                    break;
                var test = allTests[int.Parse(line)];
                try
                {
                    test.Invoke();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed on {test.GetMethodInfo().Name}. Exception: {ex}.");
                }
            }
            Console.WriteLine("Done.");
        }
    }
}