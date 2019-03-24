using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace MultiValueDictionary
{
    class Program
    {
        static void Main(string[] args)
        {
            var d = new MultiValueDictionary<int, int> { { 1, 1 }, { 1, 2 }, { 2, 2 } };
            Debug.Assert(d.Add(2, 2) == false);
            Debug.Assert(d.Add(1, 2) == false);
            Debug.Assert(d.Count() == 3);

            Debug.Assert(d.Add(1, 3) == true);
            Debug.Assert(d.Count() == 4);

            d.Remove(1, 3);
            Debug.Assert(d.Count() == 3);

            d.Remove(1);
            Debug.Assert(d.Count() == 1);
        }
    }

    // Works like a dictionary, but each key there can be associated with
    // multiple, but unique (per key) values. 
    public interface IMultiValueDictionary<K, V> : IEnumerable<KeyValuePair<K, V>>
    {
        // Returns true if the collection is modified; false otherwise.
        // Must return false if (key, value) pair already exists in the dictionary.
        bool Add(K key, V value);
        // Enumerates all the values for the specified key.
        // Must throw an exception, if there are no such values and failIfNone == true;
        // otherwise (i.e. if there are no such values, but failIfNone == false)
        // it should return an empty sequence.
        IEnumerable<V> Get(K key, bool failIfNone = true);
        // Removes (key, value) pair
        void Remove(K key, V value);
        // Removes all (key, value) pairs with the matching key
        void Remove(K key);
    }

    public class MultiValueDictionary<K, V> : IMultiValueDictionary<K, V>
    {
        private readonly Dictionary<K, HashSet<V>> _internalDictionary = new Dictionary<K, HashSet<V>>();

        public bool Add(K key, V value)
        {
            if (!_internalDictionary.ContainsKey(key))
            {
                _internalDictionary.Add(key, new HashSet<V> { value });
            }
            else
            {
                if (_internalDictionary[key].Contains(value))
                {
                    return false;
                }
                else
                {
                    _internalDictionary[key].Add(value);
                }
            }
            return true;
        }

        public IEnumerable<V> Get(K key, bool failIfNone = true)
        {
            bool result = _internalDictionary.TryGetValue(key, out HashSet<V> value);
            if (!result)
            {
                if (failIfNone)
                {
                    throw new Exception("Alarma!!!!!!");
                }
                else
                {
                    return Enumerable.Empty<V>();
                }
            }
            else
            {
                return _internalDictionary[key].Select(v => v);
            }
        }

        public void Remove(K key, V value)
        {
            bool result = _internalDictionary.TryGetValue(key, out HashSet<V> foundedValue);
            if (result)
            {
                foundedValue.Remove(value);
                if (foundedValue.Count == 0)
                {
                    _internalDictionary.Remove(key);
                }
            }
        }

        public void Remove(K key)
        {
            bool result = _internalDictionary.TryGetValue(key, out HashSet<V> foundedValue);
            if (result)
            {
                _internalDictionary.Remove(key);
            }
        }

        public IEnumerator GetEnumerator()
        {
            return GetEnumerator();
        }

        IEnumerator<KeyValuePair<K, V>> IEnumerable<KeyValuePair<K, V>>.GetEnumerator()
        {
            foreach (var item in _internalDictionary)
            {
                foreach (var nestedItem in item.Value)
                {
                    yield return new KeyValuePair<K, V>(item.Key, nestedItem);
                }
            }
        }
    }
}