using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace IGTAP.EngineSim
{
    public sealed class Scheduler : IScheduler
    {
        public struct Pending
        {
            public int Due;
            public MonoBehaviour Target;
            public string Method;
            public float Repeat;
        }

        /// <summary>Where in a tick a parked coroutine resumes (Unity's player loop at playback's frame rate).</summary>
        public enum Wait
        {
            /// <summary><c>WaitForSeconds</c>: at the start of the tick <see cref="Routine.Due"/>, as an Invoke of that delay.</summary>
            Seconds,
            /// <summary><c>WaitForFixedUpdate</c>: right after the next physics step (one yielded inside its callbacks: that step).</summary>
            FixedUpdate,
            /// <summary><c>yield return null</c>: after the next <c>Update</c>.</summary>
            Frame,
            /// <summary><c>WaitForEndOfFrame</c>: at the tick's end of frame.</summary>
            EndOfFrame,
        }

        public sealed class Routine
        {
            public MonoBehaviour Target;
            public IEnumerator Body;
            public Coroutine Handle;
            public Wait Wait;
            public int Due;
        }

        public readonly List<Pending> Queue = new List<Pending>();
        /// <summary>Coroutines parked on a yield, in start order.</summary>
        public readonly List<Routine> Routines = new List<Routine>();
        public int Tick;
        /// <summary>
        /// Targets whose coroutines run on past their first yield. The rest run to it and stop there: their later steps are
        /// presentation or real-time work turbo never runs within a run (enginesim/README.md).
        /// </summary>
        public Predicate<MonoBehaviour> Continues;
        /// <summary>Called before a coroutine step changes its target (Simulation.Touch keeps the target's fields in snapshots).</summary>
        public Action<MonoBehaviour> Stepping;
        /// <summary>A coroutine yielded something this scheduler doesn't model.</summary>
        public Action<string> Unmodeled;
        static readonly System.Collections.Concurrent.ConcurrentDictionary<(Type, string), MethodInfo> Methods =
            new System.Collections.Concurrent.ConcurrentDictionary<(Type, string), MethodInfo>();
        static readonly Func<object, object> MemberwiseClone = (Func<object, object>)Delegate.CreateDelegate(typeof(Func<object, object>),
            typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic));

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
            var handle = new Coroutine { routine = routine };
            bool continues = Continues != null && Continues(target);
            if (continues) Stepping?.Invoke(target);
            if (routine.MoveNext() && continues) Park(new Routine { Target = target, Body = routine, Handle = handle }, routine.Current);
            return handle;
        }

        void Park(Routine r, object yielded)
        {
            switch (yielded)
            {
                case WaitForSeconds seconds:
                    r.Wait = Wait.Seconds;
                    r.Due = Tick + Mathf.FloorToInt(seconds.m_Seconds / Time.fixedDeltaTime) + 1;
                    break;
                case WaitForFixedUpdate _:
                    r.Wait = Wait.FixedUpdate;
                    break;
                case WaitForEndOfFrame _:
                    r.Wait = Wait.EndOfFrame;
                    break;
                case null:
                    r.Wait = Wait.Frame;
                    break;
                default:
                    Unmodeled?.Invoke(r.Target.GetType().Name + " coroutine yields " + yielded.GetType().Name);
                    return;
            }
            Routines.Add(r);
        }

        /// <summary>Resumes every coroutine parked on <paramref name="wait"/> that is due, each one step, in start order.</summary>
        public void Resume(Wait wait)
        {
            int count = Routines.Count;
            if (count == 0) return;
            Routine[] due = null;
            int n = 0;
            for (int i = 0; i < count; i++)
            {
                Routine r = Routines[i];
                if (r.Wait != wait || wait == Wait.Seconds && r.Due > Tick) continue;
                (due ??= new Routine[count])[n++] = r;
            }
            for (int i = 0; i < n; i++)
            {
                Routine r = due[i];
                Routines.Remove(r);
                if (r.Handle.stopped || !r.Target) continue;
                Stepping?.Invoke(r.Target);
                if (r.Body.MoveNext()) Park(r, r.Body.Current);
            }
        }

        /// <summary>A parked coroutine's own copy (the compiler's iterator object, fields shallow-copied), for a snapshot.</summary>
        public static IEnumerator Copy(IEnumerator body) { return (IEnumerator)MemberwiseClone(body); }

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
