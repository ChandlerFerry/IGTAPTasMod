using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace IGTAP.EngineSim
{
    // MonoBehaviour.Invoke on fixed ticks, as TurboRunner reschedules Movement's: a call scheduled `delay` seconds
    // after tick n fires at the start of tick n + floor(delay / fixedDeltaTime) + 1 (>= 50 fps frame semantics).
    // Turbo leaves other scripts' Invokes to Unity's real-time clock; here every script uses the tick rule, which is
    // what uninterrupted real-time play does. Coroutines run to their first yield, like StartCoroutine does; turbo
    // advances none further within a run but a buy-max box's IE_BuyMax, which Shop models (it replaces the box's trigger).
    public sealed class Scheduler : IScheduler
    {
        public struct Pending
        {
            public int Due;
            public MonoBehaviour Target;
            public string Method;
            public float Repeat;
        }

        public readonly List<Pending> Queue = new List<Pending>();
        public int Tick;
        static readonly System.Collections.Concurrent.ConcurrentDictionary<(Type, string), MethodInfo> Methods =
            new System.Collections.Concurrent.ConcurrentDictionary<(Type, string), MethodInfo>();

        public void Invoke(MonoBehaviour target, string method, float delay, float repeatRate)
        {
            Queue.Add(new Pending { Due = Tick + Mathf.FloorToInt(delay / Time.fixedDeltaTime) + 1, Target = target, Method = method, Repeat = repeatRate });
        }

        public void CancelInvoke(MonoBehaviour target, string method)
        {
            Queue.RemoveAll(p => ReferenceEquals(p.Target, target) && (method == null || p.Method == method));
        }

        public bool IsInvoking(MonoBehaviour target, string method)
        {
            foreach (Pending p in Queue) if (ReferenceEquals(p.Target, target) && (method == null || p.Method == method)) return true;
            return false;
        }

        public Coroutine StartCoroutine(MonoBehaviour target, IEnumerator routine)
        {
            routine.MoveNext();
            return new Coroutine();
        }

        // Fires everything due at the start of the current tick, in scheduling order.
        public void FireDue()
        {
            for (int i = 0; i < Queue.Count; i++)
            {
                Pending p = Queue[i];
                if (p.Due > Tick) continue;
                Queue.RemoveAt(i--);
                if (p.Repeat >= 0f) Queue.Add(new Pending { Due = Tick + Math.Max(1, Mathf.FloorToInt(p.Repeat / Time.fixedDeltaTime)), Target = p.Target, Method = p.Method, Repeat = p.Repeat });
                if (!p.Target) continue;
                Resolve(p.Target.GetType(), p.Method)?.Invoke(p.Target, null);
            }
        }

        static MethodInfo Resolve(Type type, string name)
        {
            return Methods.GetOrAdd((type, name), key =>
            {
                MethodInfo found = null;
                for (Type t = key.Item1; t != null && found == null; t = t.BaseType)
                    found = t.GetMethod(key.Item2, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                return found;
            });
        }
    }
}
