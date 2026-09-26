using System;
using System.Collections;
using System.Collections.Generic;
using IGTAP.EngineSim;

namespace UnityEngine
{
    public class Object
    {
        internal int m_InstanceID = Engine.NextInstanceId--;
        internal bool m_Destroyed;
        string m_Name = "";

        public string name { get { return m_Name; } set { m_Name = value; } }
        public HideFlags hideFlags { get; set; }
        public int GetInstanceID() { return m_InstanceID; }
        public void SetInstanceID(int id) { m_InstanceID = id; }
        public override int GetHashCode() { return m_InstanceID; }
        public override bool Equals(object other) { return other is Object o && CompareBaseObjects(this, o); }
        public override string ToString() { return m_Name; }

        static bool CompareBaseObjects(Object lhs, Object rhs)
        {
            bool lhsNull = (object)lhs == null || lhs.m_Destroyed;
            bool rhsNull = (object)rhs == null || rhs.m_Destroyed;
            if (lhsNull && rhsNull) return true;
            if (lhsNull || rhsNull) return false;
            return ReferenceEquals(lhs, rhs);
        }

        public static bool operator ==(Object x, Object y) { return CompareBaseObjects(x, y); }
        public static bool operator !=(Object x, Object y) { return !CompareBaseObjects(x, y); }
        public static implicit operator bool(Object exists) { return (object)exists != null && !exists.m_Destroyed; }

        public static void Destroy(Object obj) { if ((object)obj != null) obj.m_Destroyed = true; }
        public static void Destroy(Object obj, float t) { Destroy(obj); }
        public static void DestroyImmediate(Object obj) { Destroy(obj); }
        public static void DontDestroyOnLoad(Object target) { }
        public static T Instantiate<T>(T original) where T : Object { throw new NotSupportedException("Instantiate is not supported offline."); }
    }

    public enum HideFlags { None = 0 }

    public class Component : Object
    {
        internal GameObject m_GameObject;

        public GameObject gameObject { get { return m_GameObject; } }
        public Transform transform { get { return m_GameObject.m_Transform; } }
        public string tag { get { return m_GameObject.tag; } set { m_GameObject.tag = value; } }
        public bool CompareTag(string tag) { return m_GameObject.tag == tag; }
        public T GetComponent<T>() { return m_GameObject.GetComponent<T>(); }
        public Component GetComponent(Type type) { return m_GameObject.GetComponent(type); }
        public Component GetComponent(string type) { return m_GameObject.GetComponent(type); }
        public bool TryGetComponent<T>(out T component) { component = GetComponent<T>(); return component != null; }
        public T[] GetComponents<T>() { return m_GameObject.GetComponents<T>(); }
        public T GetComponentInParent<T>() { return m_GameObject.GetComponentInParent<T>(false); }
        public T GetComponentInParent<T>(bool includeInactive) { return m_GameObject.GetComponentInParent<T>(includeInactive); }
        public T GetComponentInChildren<T>() { return m_GameObject.GetComponentInChildren<T>(false); }
        public T GetComponentInChildren<T>(bool includeInactive) { return m_GameObject.GetComponentInChildren<T>(includeInactive); }
        public T[] GetComponentsInChildren<T>() { return m_GameObject.GetComponentsInChildren<T>(false); }
        public T[] GetComponentsInChildren<T>(bool includeInactive) { return m_GameObject.GetComponentsInChildren<T>(includeInactive); }
        public void SendMessage(string methodName) { }
    }

    public class Behaviour : Component
    {
        internal bool m_Enabled = true;

        public bool enabled
        {
            get { return m_Enabled; }
            set
            {
                if (m_Enabled == value) return;
                IGTAP.EngineSim.Engine.EnabledChanging?.Invoke(this);
                m_Enabled = value;
                OnEnabledChanged();
            }
        }

        public bool isActiveAndEnabled { get { return m_Enabled && m_GameObject.activeInHierarchy; } }
        internal virtual void OnEnabledChanged() { }
    }

    public class MonoBehaviour : Behaviour
    {
        public bool useGUILayout { get; set; }

        public void Invoke(string methodName, float time) { Engine.Scheduler.Invoke(this, methodName, time, -1f); }
        public void InvokeRepeating(string methodName, float time, float repeatRate) { Engine.Scheduler.Invoke(this, methodName, time, repeatRate); }
        public void CancelInvoke(string methodName) { Engine.Scheduler.CancelInvoke(this, methodName); }
        public void CancelInvoke() { Engine.Scheduler.CancelInvoke(this, null); }
        public bool IsInvoking(string methodName) { return Engine.Scheduler.IsInvoking(this, methodName); }
        public bool IsInvoking() { return Engine.Scheduler.IsInvoking(this, null); }
        public Coroutine StartCoroutine(IEnumerator routine) { return Engine.Scheduler.StartCoroutine(this, routine); }
        public void StopCoroutine(Coroutine routine) { if (routine != null) routine.stopped = true; }
        public void StopCoroutine(IEnumerator routine) { }
        public void StopAllCoroutines() { }
        public static void print(object message) { }
    }

    public sealed class Coroutine : YieldInstruction
    {
        internal bool stopped;
        internal IEnumerator routine;
    }

    public class YieldInstruction { }
    public sealed class WaitForSeconds : YieldInstruction { internal readonly float m_Seconds; public WaitForSeconds(float seconds) { m_Seconds = seconds; } }
    public sealed class WaitForSecondsRealtime : CustomYieldInstruction { public WaitForSecondsRealtime(float time) { } public override bool keepWaiting { get { return false; } } }
    public sealed class WaitForFixedUpdate : YieldInstruction { }
    public sealed class WaitForEndOfFrame : YieldInstruction { }
    public abstract class CustomYieldInstruction : IEnumerator
    {
        public abstract bool keepWaiting { get; }
        public object Current { get { return null; } }
        public bool MoveNext() { return keepWaiting; }
        public virtual void Reset() { }
    }

    public sealed class GameObject : Object
    {
        internal Transform m_Transform;
        internal readonly List<Component> m_Components = new List<Component>(4);
        internal bool m_ActiveSelf = true;
        internal bool m_ActiveInHierarchy = true;
        internal int m_Layer;

        public GameObject() { m_Transform = new Transform(); AttachInternal(m_Transform); }
        public GameObject(string name) : this() { this.name = name; }

        public Transform transform { get { return m_Transform; } }
        public GameObject gameObject { get { return this; } }
        public int layer
        {
            get { return m_Layer; }
            set
            {
                if (m_Layer == value) return;
                IGTAP.EngineSim.Engine.LayerChanging?.Invoke(this);
                m_Layer = value;
                if (Engine.Physics != null) Engine.Physics.LayerChanged(this);
            }
        }
        public string tag { get; set; } = "Untagged";
        public bool activeSelf { get { return m_ActiveSelf; } }
        public bool activeInHierarchy { get { return m_ActiveInHierarchy; } }
        public bool CompareTag(string tag) { return this.tag == tag; }

        public void AttachInternal(Component component)
        {
            component.m_GameObject = this;
            m_Components.Add(component);
        }

        public void SetActive(bool value)
        {
            if (m_ActiveSelf == value) return;
            IGTAP.EngineSim.Engine.ActiveChanging?.Invoke(this);
            m_ActiveSelf = value;
            RecomputeActive(m_Transform.m_Parent == null || m_Transform.m_Parent.m_GameObject.m_ActiveInHierarchy);
        }

        public void RecomputeActive(bool parentActive)
        {
            bool active = parentActive && m_ActiveSelf;
            bool changed = active != m_ActiveInHierarchy;
            m_ActiveInHierarchy = active;
            foreach (Transform child in m_Transform.m_Children) child.m_GameObject.RecomputeActive(active);
            if (changed && Engine.Physics != null) Engine.Physics.ActiveChanged(this);
        }

        public T GetComponent<T>()
        {
            List<Component> list = m_Components;
            for (int i = 0; i < list.Count; i++)
                if (list[i] is T match) return match;
            return default;
        }

        public Component GetComponent(Type type)
        {
            foreach (Component c in m_Components) if (type.IsInstanceOfType(c)) return c;
            return null;
        }

        public Component GetComponent(string type)
        {
            foreach (Component c in m_Components) if (c.GetType().Name == type) return c;
            return null;
        }

        public bool TryGetComponent<T>(out T component) { component = GetComponent<T>(); return component != null; }

        public T[] GetComponents<T>()
        {
            var result = new List<T>();
            foreach (Component c in m_Components) if (c is T match) result.Add(match);
            return result.ToArray();
        }

        public T GetComponentInParent<T>(bool includeInactive)
        {
            for (Transform t = m_Transform; t != null; t = t.m_Parent)
            {
                if (!includeInactive && !t.m_GameObject.m_ActiveInHierarchy) continue;
                T found = t.m_GameObject.GetComponent<T>();
                if (found != null) return found;
            }
            return default;
        }

        public T GetComponentInChildren<T>(bool includeInactive)
        {
            if (!includeInactive && !m_ActiveInHierarchy) return default;
            T found = GetComponent<T>();
            if (found != null) return found;
            foreach (Transform child in m_Transform.m_Children)
            {
                found = child.m_GameObject.GetComponentInChildren<T>(includeInactive);
                if (found != null) return found;
            }
            return default;
        }

        public T[] GetComponentsInChildren<T>(bool includeInactive)
        {
            var result = new List<T>();
            Collect(this, includeInactive, result);
            return result.ToArray();
        }

        static void Collect<T>(GameObject go, bool includeInactive, List<T> result)
        {
            if (!includeInactive && !go.m_ActiveInHierarchy) return;
            foreach (Component c in go.m_Components) if (c is T match) result.Add(match);
            foreach (Transform child in go.m_Transform.m_Children) Collect(child.m_GameObject, includeInactive, result);
        }

        public T AddComponent<T>() where T : Component, new()
        {
            var component = new T();
            AttachInternal(component);
            return component;
        }

        public static GameObject Find(string name) { return null; }
    }

    public class Transform : Component, IEnumerable
    {
        internal Transform m_Parent;
        internal readonly List<Transform> m_Children = new List<Transform>();
        internal Vector3 m_Position, m_LocalPosition, m_LocalScale = Vector3.one, m_LossyScale = Vector3.one;
        internal Quaternion m_Rotation = Quaternion.identity, m_LocalRotation = Quaternion.identity;

        public Transform parent { get { return m_Parent; } set { SetParent(value); } }
        public int childCount { get { return m_Children.Count; } }
        public Transform GetChild(int index) { return m_Children[index]; }
        public IEnumerator GetEnumerator() { return m_Children.GetEnumerator(); }
        public Transform root { get { Transform t = this; while (t.m_Parent != null) t = t.m_Parent; return t; } }

        public void SetParent(Transform newParent) { SetParent(newParent, true); }
        public void SetParent(Transform newParent, bool worldPositionStays)
        {
            m_Parent?.m_Children.Remove(this);
            m_Parent = newParent;
            newParent?.m_Children.Add(this);
        }

        public Transform Find(string n)
        {
            foreach (Transform child in m_Children) if (child.m_GameObject.name == n) return child;
            return null;
        }

        public Vector3 position
        {
            get { return m_Position; }
            set { StoreWorldPosition(value); Changed(true); }
        }

        internal void StoreWorldPosition(Vector3 value)
        {
            if (m_Parent == null) { m_Position = value; m_LocalPosition = value; return; }
            m_LocalPosition = value - m_Parent.m_Position;
            m_Position = m_Parent.m_Position + m_LocalPosition;
        }

        public Vector3 localPosition
        {
            get { return m_LocalPosition; }
            set
            {
                m_LocalPosition = value;
                m_Position = m_Parent == null ? value : m_Parent.m_Position + value;
                Changed(true);
            }
        }

        public Quaternion rotation
        {
            get { return m_Rotation; }
            set { m_Rotation = value; m_LocalRotation = value; Changed(true); }
        }

        public Quaternion localRotation
        {
            get { return m_LocalRotation; }
            set { m_LocalRotation = value; m_Rotation = value; Changed(true); }
        }

        public Vector3 eulerAngles { get { return m_Rotation.eulerAngles; } set { rotation = Quaternion.Euler(value); } }
        public Vector3 localEulerAngles { get { return m_LocalRotation.eulerAngles; } set { localRotation = Quaternion.Euler(value); } }

        public Vector3 localScale
        {
            get { return m_LocalScale; }
            set
            {
                m_LocalScale = value;
                m_LossyScale = m_Parent == null ? value : Vector3.Scale(m_Parent.m_LossyScale, value);
                Changed(false);
            }
        }

        public Vector3 lossyScale { get { return m_LossyScale; } }
        public Vector3 up { get { return m_Rotation * Vector3.up; } }
        public Vector3 right { get { return m_Rotation * Vector3.right; } }
        public Vector3 forward { get { return m_Rotation * Vector3.forward; } }
        public bool hasChanged { get; set; }

        public Vector3 TransformPoint(Vector3 p) { return m_Position + m_Rotation * Vector3.Scale(m_LossyScale, p); }

        void Changed(bool pose)
        {
            hasChanged = true;
            Engine.Physics?.TransformChanged(this, pose);
        }

        public void InitPose(Vector3 position, Quaternion rotation, Vector3 lossyScale, Vector3 localPosition, Quaternion localRotation, Vector3 localScale)
        {
            m_Position = position; m_Rotation = rotation; m_LossyScale = lossyScale;
            m_LocalPosition = localPosition; m_LocalRotation = localRotation; m_LocalScale = localScale;
        }
    }

    public sealed class RectTransform : Transform { }
}
