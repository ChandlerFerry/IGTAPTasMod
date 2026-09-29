using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq.Expressions;
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
        static readonly System.Collections.Concurrent.ConcurrentDictionary<(Type, string), Action<MonoBehaviour>> Methods =
            new System.Collections.Concurrent.ConcurrentDictionary<(Type, string), Action<MonoBehaviour>>();
        static readonly Func<object, object> MemberwiseClone = (Func<object, object>)Delegate.CreateDelegate(typeof(Func<object, object>),
            typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic));

        // While FireDue runs, Queue[0.._kept) are the entries it has passed over (kept, in order), Queue[_kept.._next) the
        // slots of the ones it fired (dropped when the pass ends), and Queue[_next..] the rest, in order; _kept is -1 outside
        // a pass. _skip is how many entries the pass still steps over unfired: an index loop over a list that an Invoke'd
        // method's CancelInvoke shrank behind the index moves past as many entries as it lost there.
        int _kept = -1, _next, _skip;

        public void Invoke(MonoBehaviour target, string method, float delay, float repeatRate)
        {
            Queue.Add(new Pending { Due = Tick + Mathf.FloorToInt(delay / Time.fixedDeltaTime) + 1, Target = target, Method = method, Repeat = repeatRate });
            if (_kept >= 0) Skip();
        }

        public void CancelInvoke(MonoBehaviour target, string method)
        {
            int end;
            if (_kept < 0)
            {
                end = Drop(0, Queue.Count, target, method);
                Queue.RemoveRange(end, Queue.Count - end);
                return;
            }
            int kept = Drop(0, _kept, target, method);
            _skip += _kept - kept;
            _kept = kept;
            end = Drop(_next, Queue.Count, target, method);
            Queue.RemoveRange(end, Queue.Count - end);
            Skip();
        }

        /// <summary>Drops the entries of Queue[from..to) a CancelInvoke matches, the others kept in order from <paramref name="from"/>; returns where they end.</summary>
        int Drop(int from, int to, MonoBehaviour target, string method)
        {
            int end = from;
            for (int i = from; i < to; i++)
                if (!Cancels(Queue[i], target, method)) Queue[end++] = Queue[i];
            return end;
        }

        static bool Cancels(Pending p, MonoBehaviour target, string method)
        {
            return ReferenceEquals(p.Target, target) && (method == null || p.Method == method);
        }

        /// <summary>Mid-pass: the entries the pass steps over unfired join the kept ones.</summary>
        void Skip()
        {
            for (; _skip > 0 && _next < Queue.Count; _skip--) Queue[_kept++] = Queue[_next++];
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

        /// <summary>Time.deltaTime of the rendered frame that resumes WaitForSeconds and frame waits (the playback clock's crossing frame).</summary>
        public float FrameDeltaTime;

        // Resume's due routines, reused call to call.
        Routine[] _due = Array.Empty<Routine>();

        /// <summary>Resumes every coroutine parked on <paramref name="wait"/> that is due, each one step, in start order.</summary>
        public void Resume(Wait wait)
        {
            int count = Routines.Count;
            if (count == 0) return;
            if (_due.Length < count) _due = new Routine[count];
            int n = 0;
            for (int i = 0; i < count; i++)
            {
                Routine r = Routines[i];
                if (r.Wait != wait || wait == Wait.Seconds && r.Due > Tick) continue;
                _due[n++] = r;
            }
            if (n == 0) return;
            // As the game's player loop: a WaitForFixedUpdate resumes in the physics step (deltaTime the fixed step), the
            // others in a rendered frame (a zip mover's first tween step after its WaitForSeconds adds that frame's time).
            Time.resumeDeltaTime = wait == Wait.FixedUpdate ? Time.fixedDeltaTime : FrameDeltaTime;
            int resumed = 0;
            try
            {
                while (resumed < n)
                {
                    Routine r = _due[resumed++];
                    if (!r.Target) continue;
                    Stepping?.Invoke(r.Target);
                    if (r.Body.MoveNext()) Park(r, r.Body.Current);
                }
            }
            finally
            {
                Time.resumeDeltaTime = 0f;
                // Each resumed routine leaves its place in one pass (nothing reads the list while they step): the due ones sit
                // in Routines[0..count) in _due's order, and one parked again, or a routine one of them started, past count.
                int kept = 0, left = 0;
                for (int i = 0; i < Routines.Count; i++)
                {
                    Routine r = Routines[i];
                    if (i < count && left < resumed && ReferenceEquals(r, _due[left])) left++;
                    else Routines[kept++] = r;
                }
                Routines.RemoveRange(kept, Routines.Count - kept);
                Array.Clear(_due, 0, n);
            }
        }

        /// <summary>A parked coroutine's own copy (the compiler's iterator object, fields shallow-copied), for a snapshot.</summary>
        public static IEnumerator Copy(IEnumerator body) { return (IEnumerator)MemberwiseClone(body); }

        /// <summary>
        /// Fires the due invokes in queue order; a repeating one goes back at the end. Behaves as an index loop over the list
        /// that removes each fired entry (see <see cref="_kept"/>), in one pass.
        /// </summary>
        public void FireDue()
        {
            _kept = _next = _skip = 0;
            try
            {
                while (_next < Queue.Count)
                {
                    Pending p = Queue[_next++];
                    if (p.Due > Tick)
                    {
                        Queue[_kept++] = p;
                        continue;
                    }
                    if (p.Repeat >= 0f) Queue.Add(new Pending { Due = Tick + Math.Max(1, Mathf.FloorToInt(p.Repeat / Time.fixedDeltaTime)), Target = p.Target, Method = p.Method, Repeat = p.Repeat });
                    if (!p.Target) continue;
                    Resolve(p.Target.GetType(), p.Method)?.Invoke(p.Target);
                }
            }
            finally
            {
                Queue.RemoveRange(_kept, _next - _kept);
                _kept = -1;
                _skip = 0;
            }
        }

        /// <summary>The parameterless instance method Invoke calls by name (the nearest declaring type's), compiled once: MethodInfo.Invoke per call is reflection.</summary>
        static Action<MonoBehaviour> Resolve(Type type, string name)
        {
            return Methods.GetOrAdd((type, name), key =>
            {
                MethodInfo found = null;
                for (Type t = key.Item1; t != null && found == null; t = t.BaseType)
                    found = t.GetMethod(key.Item2, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                if (found == null) return null;
                ParameterExpression target = Expression.Parameter(typeof(MonoBehaviour));
                return Expression.Lambda<Action<MonoBehaviour>>(Expression.Call(Expression.Convert(target, found.DeclaringType), found), target).Compile();
            });
        }
    }
}
