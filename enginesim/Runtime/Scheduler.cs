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

        public enum Wait
        {
            Seconds,
            FixedUpdate,
            Frame,
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
        public readonly List<Routine> Routines = new List<Routine>();
        public int Tick;
        public int Frame;
        public Predicate<MonoBehaviour> Continues;
        // InUpdate targets' Invokes run in the first frame's Update phase (a teleporter's manualUpdate moves the player).
        public Predicate<MonoBehaviour> InUpdate;
        public Action<MonoBehaviour> Stepping;
        public Action<string> Unmodeled;
        static readonly System.Collections.Concurrent.ConcurrentDictionary<(Type, string), Action<MonoBehaviour>> Methods =
            new System.Collections.Concurrent.ConcurrentDictionary<(Type, string), Action<MonoBehaviour>>();
        static readonly Func<object, object> MemberwiseClone = (Func<object, object>)Delegate.CreateDelegate(typeof(Func<object, object>),
            typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic));

        // FireDue emulates an index loop removing fired entries in one pass: [0.._kept) kept, [_kept.._next) fired.
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

        void Skip()
        {
            for (; _skip > 0 && _next < Queue.Count; _skip--) Queue[_kept++] = Queue[_next++];
        }

        public Coroutine StartCoroutine(MonoBehaviour target, IEnumerator routine)
        {
            // Unity refuses a coroutine on an object inactive in hierarchy (a tree segment in an unloaded zone never grows).
            if (!target.gameObject.activeInHierarchy) return null;
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
                    r.Due = Frame;
                    break;
                default:
                    Unmodeled?.Invoke(r.Target.GetType().Name + " coroutine yields " + yielded.GetType().Name);
                    return;
            }
            Routines.Add(r);
        }

        public float FrameDeltaTime;

        Routine[] _due = Array.Empty<Routine>();

        public void Resume(Wait wait)
        {
            Routines.RemoveAll(r => !r.Target || !r.Target.gameObject.activeInHierarchy);
            int count = Routines.Count;
            if (count == 0) return;
            if (_due.Length < count) _due = new Routine[count];
            int n = 0;
            for (int i = 0; i < count; i++)
            {
                Routine r = Routines[i];
                if (r.Wait != wait || wait == Wait.Seconds && r.Due > Tick || wait == Wait.Frame && r.Due >= Frame) continue;
                _due[n++] = r;
            }
            if (n == 0) return;
            // WaitForFixedUpdate resumes in the physics step (fixed deltaTime), others in a rendered frame.
            Time.resumeDeltaTime = wait == Wait.FixedUpdate ? Time.fixedDeltaTime : FrameDeltaTime;
            int resumed = 0;
            try
            {
                while (resumed < n)
                {
                    Routine r = _due[resumed++];
                    if (!r.Target || !r.Target.gameObject.activeInHierarchy) continue;
                    Stepping?.Invoke(r.Target);
                    if (r.Body.MoveNext()) Park(r, r.Body.Current);
                }
            }
            finally
            {
                Time.resumeDeltaTime = null;
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

        public static IEnumerator Copy(IEnumerator body) { return (IEnumerator)MemberwiseClone(body); }

        public void FireDue() => Fire(false, Tick);

        public void FireInUpdate() => Fire(true, Tick + 1);

        void Fire(bool inUpdate, int now)
        {
            _kept = _next = _skip = 0;
            try
            {
                while (_next < Queue.Count)
                {
                    Pending p = Queue[_next++];
                    if (p.Due > now || (InUpdate != null && InUpdate(p.Target)) != inUpdate)
                    {
                        Queue[_kept++] = p;
                        continue;
                    }
                    if (p.Repeat >= 0f) Queue.Add(new Pending { Due = now + Math.Max(1, Mathf.FloorToInt(p.Repeat / Time.fixedDeltaTime)), Target = p.Target, Method = p.Method, Repeat = p.Repeat });
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
