using System.Collections.Generic;
using IGTAP.EngineSim;

namespace UnityEngine
{
    public enum SimulationMode2D { FixedUpdate, Update, Script }
    public enum RigidbodyType2D { Dynamic, Kinematic, Static }
    public enum RigidbodyInterpolation2D { None, Interpolate, Extrapolate }
    public enum CollisionDetectionMode2D { Discrete, Continuous }
    public enum RigidbodySleepMode2D { NeverSleep, StartAwake, StartAsleep }
    public enum ForceMode2D { Force, Impulse }
    [System.Flags] public enum RigidbodyConstraints2D { None = 0, FreezePositionX = 1, FreezePositionY = 2, FreezeRotation = 4, FreezePosition = 3, FreezeAll = 7 }

    public static class Physics2D
    {
        public static SimulationMode2D simulationMode { get; set; } = SimulationMode2D.Script;
        public static Vector2 gravity { get; set; }
        public static bool queriesHitTriggers { get; set; } = true;
        public static bool queriesStartInColliders { get; set; } = true;
        public static bool autoSyncTransforms { get; set; }

        public static RaycastHit2D Raycast(Vector2 origin, Vector2 direction, float distance, int layerMask)
        {
            return Engine.Physics.Raycast(origin, direction, distance, layerMask);
        }

        public static RaycastHit2D Raycast(Vector2 origin, Vector2 direction, float distance)
        {
            return Engine.Physics.Raycast(origin, direction, distance, -5);
        }

        public static RaycastHit2D Raycast(Vector2 origin, Vector2 direction)
        {
            return Engine.Physics.Raycast(origin, direction, float.PositiveInfinity, -5);
        }

        public static bool Simulate(float deltaTime) { Engine.Physics.Simulate(deltaTime); return true; }
        public static void SyncTransforms() { Engine.Physics.SyncTransforms(); }
    }

    public sealed class PhysicsMaterial2D : Object
    {
        public float friction { get; set; } = 0.4f;
        public float bounciness { get; set; }
    }

    public sealed class Rigidbody2D : Component
    {
        public object BackendData;

        public RigidbodyType2D bodyType { get; set; }
        public bool simulated { get; set; } = true;
        public float mass { get; set; } = 1f;
        public float gravityScale { get; set; } = 1f;
        public RigidbodyInterpolation2D interpolation { get; set; }
        public CollisionDetectionMode2D collisionDetectionMode { get; set; }
        public RigidbodyConstraints2D constraints { get; set; }
        public bool freezeRotation { get; set; }
        public PhysicsMaterial2D sharedMaterial { get; set; }

        public Vector2 position { get { return Engine.Physics.GetPosition(this); } set { Engine.Physics.SetPosition(this, value); } }
        public float rotation { get { return Engine.Physics.GetRotation(this); } set { Engine.Physics.SetRotation(this, value); } }
        public Vector2 linearVelocity { get { return Engine.Physics.GetVelocity(this); } set { Engine.Physics.SetVelocity(this, value); } }
        public float angularVelocity { get { return Engine.Physics.GetAngularVelocity(this); } set { Engine.Physics.SetAngularVelocity(this, value); } }

        public float linearVelocityX
        {
            get { return linearVelocity.x; }
            set { Vector2 v = linearVelocity; v.x = value; linearVelocity = v; }
        }

        public float linearVelocityY
        {
            get { return linearVelocity.y; }
            set { Vector2 v = linearVelocity; v.y = value; linearVelocity = v; }
        }

        public Vector2 velocity { get { return linearVelocity; } set { linearVelocity = value; } }
        public int GetContacts(ContactPoint2D[] contacts) { return Engine.Physics.GetContacts(this, contacts); }
        public void MovePosition(Vector2 position) { this.position = position; }
        public void AddForce(Vector2 force) { }
        public void AddForce(Vector2 force, ForceMode2D mode) { }
        public void WakeUp() { }
        public void Sleep() { }
        public bool IsAwake() { return true; }
    }

    public class Collider2D : Behaviour
    {
        public object BackendData;
        internal Vector2 m_Offset;
        internal LayerMask m_ExcludeLayers, m_IncludeLayers;
        internal bool m_IsTrigger;

        public Vector2 offset { get { return m_Offset; } set { if (m_Offset != value) { m_Offset = value; Changed(); } } }
        public bool isTrigger { get { return m_IsTrigger; } set { if (m_IsTrigger != value) { m_IsTrigger = value; Changed(); } } }
        public LayerMask excludeLayers { get { return m_ExcludeLayers; } set { if (m_ExcludeLayers.value != value.value) { m_ExcludeLayers = value; Changed(); } } }
        public LayerMask includeLayers { get { return m_IncludeLayers; } set { if (m_IncludeLayers.value != value.value) { m_IncludeLayers = value; Changed(); } } }
        public PhysicsMaterial2D sharedMaterial { get; set; }
        public Rigidbody2D attachedRigidbody { get; set; }
        public bool usedByEffector { get; set; }
        public float friction { get; set; }
        public float bounciness { get; set; }

        public ColliderDistance2D Distance(Collider2D collider) { return Engine.Physics.Distance(this, collider); }
        public bool IsTouching(Collider2D collider) { return Distance(collider).distance <= 0f; }

        protected void Changed() { Engine.Physics?.ColliderChanged(this); }
        internal override void OnEnabledChanged() { Changed(); }

        public void InitCollider(Vector2 offset, bool isTrigger, int excludeLayers, int includeLayers, bool enabled)
        {
            m_Offset = offset; m_IsTrigger = isTrigger; m_ExcludeLayers = excludeLayers; m_IncludeLayers = includeLayers; m_Enabled = enabled;
        }
    }

    public sealed class BoxCollider2D : Collider2D
    {
        internal Vector2 m_Size = Vector2.one;
        public float edgeRadius { get; set; }
        public bool autoTiling { get; set; }
        public Vector2 size { get { return m_Size; } set { if (m_Size != value) { m_Size = value; Changed(); } } }
        public void InitSize(Vector2 size) { m_Size = size; }
    }

    public sealed class CircleCollider2D : Collider2D { public float radius { get; set; } }
    public enum CapsuleDirection2D { Vertical, Horizontal }

    public sealed class CapsuleCollider2D : Collider2D
    {
        internal Vector2 m_Size = Vector2.one;
        internal CapsuleDirection2D m_Direction;
        public Vector2 size { get { return m_Size; } set { if (m_Size != value) { m_Size = value; Changed(); } } }
        public CapsuleDirection2D direction { get { return m_Direction; } set { if (m_Direction != value) { m_Direction = value; Changed(); } } }
        public void InitShape(Vector2 size, int direction) { m_Size = size; m_Direction = (CapsuleDirection2D)direction; }
    }
    public sealed class PolygonCollider2D : Collider2D { public int pathCount { get; set; } }
    public sealed class EdgeCollider2D : Collider2D { public float edgeRadius { get; set; } }
    public sealed class CompositeCollider2D : Collider2D { public int pathCount { get; set; } }
    public class Effector2D : Behaviour { }
    public class Joint2D : Behaviour { }

    public struct RaycastHit2D
    {
        internal Vector2 m_Centroid, m_Point, m_Normal;
        internal float m_Distance, m_Fraction;
        internal Collider2D m_Collider;

        public RaycastHit2D(Collider2D collider, Vector2 point, Vector2 normal, float distance, float fraction)
        {
            m_Collider = collider; m_Point = point; m_Normal = normal; m_Distance = distance; m_Fraction = fraction; m_Centroid = point;
        }

        public Vector2 centroid { get { return m_Centroid; } set { m_Centroid = value; } }
        public Vector2 point { get { return m_Point; } set { m_Point = value; } }
        public Vector2 normal { get { return m_Normal; } set { m_Normal = value; } }
        public float distance { get { return m_Distance; } set { m_Distance = value; } }
        public float fraction { get { return m_Fraction; } set { m_Fraction = value; } }
        public Collider2D collider { get { return m_Collider; } }
        public Rigidbody2D rigidbody { get { return m_Collider == null ? null : m_Collider.attachedRigidbody; } }
        public Transform transform { get { return m_Collider == null ? null : m_Collider.transform; } }
        public static implicit operator bool(RaycastHit2D hit) { return hit.m_Collider != null; }
    }

    public struct ColliderDistance2D
    {
        public Vector2 pointA { get; set; }
        public Vector2 pointB { get; set; }
        public Vector2 normal { get; set; }
        public float distance { get; set; }
        public bool isOverlapped { get { return distance < 0f; } }
        public bool isValid { get; set; }
    }

    public struct ContactPoint2D
    {
        public Vector2 point { get; set; }
        public Vector2 normal { get; set; }
        public float separation { get; set; }
        public float normalImpulse { get; set; }
        public float tangentImpulse { get; set; }
        public Vector2 relativeVelocity { get; set; }
        public Collider2D collider { get; set; }
        public Collider2D otherCollider { get; set; }
        public Rigidbody2D rigidbody { get { return collider == null ? null : collider.attachedRigidbody; } }
        public Rigidbody2D otherRigidbody { get { return otherCollider == null ? null : otherCollider.attachedRigidbody; } }
        public bool enabled { get; set; }
    }

    public class Collision2D
    {
        public Collider2D collider { get; set; }
        public Collider2D otherCollider { get; set; }
        public Vector2 relativeVelocity { get; set; }
        public readonly List<ContactPoint2D> Contacts = new List<ContactPoint2D>(2);

        public Rigidbody2D rigidbody { get { return collider == null ? null : collider.attachedRigidbody; } }
        public Rigidbody2D otherRigidbody { get { return otherCollider == null ? null : otherCollider.attachedRigidbody; } }
        public GameObject gameObject { get { Rigidbody2D rb = rigidbody; return rb != null ? rb.gameObject : collider.gameObject; } }
        public Transform transform { get { return gameObject.transform; } }
        public int contactCount { get { return Contacts.Count; } }
        public bool enabled { get; set; } = true;
        public ContactPoint2D GetContact(int index) { return Contacts[index]; }
        public int GetContacts(ContactPoint2D[] contacts)
        {
            int n = System.Math.Min(contacts.Length, Contacts.Count);
            for (int i = 0; i < n; i++) contacts[i] = Contacts[i];
            return n;
        }
    }
}
