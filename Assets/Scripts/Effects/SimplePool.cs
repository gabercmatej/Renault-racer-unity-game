using System.Collections.Generic;
using UnityEngine;

namespace RenaultRacer.Effects
{
    /// <summary>
    /// Minimal GameObject pool (plan item 4.1).
    ///
    /// Pre-warms a fixed set of instances at load and hands them out by
    /// activate/deactivate. Nothing is instantiated or destroyed during play - the
    /// plan forbids it outright (section 16), because Instantiate spikes are exactly
    /// what turns a 60 FPS mobile frame into a stutter.
    ///
    /// Deliberately NOT generic over components: callers pool GameObjects and fetch
    /// whatever they need, which keeps this small and avoids per-Get lookups.
    /// </summary>
    public sealed class SimplePool
    {
        private readonly GameObject _prefab;
        private readonly Transform _parent;
        private readonly List<GameObject> _all;
        private readonly Stack<GameObject> _free;

        public int Capacity { get { return _all.Count; } }
        public int ActiveCount { get { return _all.Count - _free.Count; } }
        public int FreeCount { get { return _free.Count; } }

        /// <param name="prefab">Template to clone. Never itself handed out.</param>
        /// <param name="parent">Container transform for the instances.</param>
        /// <param name="capacity">Hard cap. Get() returns null once exhausted.</param>
        public SimplePool(GameObject prefab, Transform parent, int capacity)
        {
            _prefab = prefab;
            _parent = parent;
            _all = new List<GameObject>(capacity);
            _free = new Stack<GameObject>(capacity);

            for (int i = 0; i < capacity; i++)
            {
                GameObject go = Object.Instantiate(prefab, parent);
                go.name = prefab.name + "_" + i;
                go.SetActive(false);
                _all.Add(go);
                _free.Push(go);
            }
        }

        /// <summary>
        /// Take an instance, or null if the pool is exhausted. A null return is a
        /// deliberate hard cap, not an error - callers simply skip the effect rather
        /// than allocating and blowing the budget.
        /// </summary>
        public GameObject Get()
        {
            if (_free.Count == 0) return null;
            GameObject go = _free.Pop();
            go.SetActive(true);
            return go;
        }

        public void Release(GameObject go)
        {
            if (go == null || !go.activeSelf) return;
            go.SetActive(false);
            go.transform.SetParent(_parent, false);
            _free.Push(go);
        }

        /// <summary>Return everything to the pool. Used on race restart.</summary>
        public void ReleaseAll()
        {
            for (int i = 0; i < _all.Count; i++)
            {
                if (_all[i] != null && _all[i].activeSelf)
                {
                    _all[i].SetActive(false);
                    _free.Push(_all[i]);
                }
            }
        }
    }
}
