using System;
using System.Collections;
using UnityEngine;

namespace IGTAP.EngineSim
{
    public interface IPhysicsBackend
    {
        RaycastHit2D Raycast(Vector2 origin, Vector2 direction, float distance, int layerMask);
        void SyncTransforms();
        ColliderDistance2D Distance(Collider2D a, Collider2D b);
        void ColliderChanged(Collider2D collider);
        void TransformChanged(Transform transform, bool pose);
        void ActiveChanged(GameObject gameObject);
        void LayerChanged(GameObject gameObject);
        Vector2 GetPosition(Rigidbody2D body);
        void SetPosition(Rigidbody2D body, Vector2 position);
        Vector2 GetVelocity(Rigidbody2D body);
        void SetVelocity(Rigidbody2D body, Vector2 velocity);
        float GetAngularVelocity(Rigidbody2D body);
        void SetAngularVelocity(Rigidbody2D body, float velocity);
    }

    public interface IScheduler
    {
        void Invoke(MonoBehaviour target, string method, float delay, float repeatRate);
        void CancelInvoke(MonoBehaviour target, string method);
        Coroutine StartCoroutine(MonoBehaviour target, IEnumerator routine);
    }

    public static class Engine
    {
        [ThreadStatic] public static IPhysicsBackend Physics;
        [ThreadStatic] public static IScheduler Scheduler;
        [ThreadStatic] public static Func<Vector2> Stick;
        [ThreadStatic] public static Action<GameObject> ActiveChanging;
        [ThreadStatic] public static Action<Behaviour> EnabledChanging;
        [ThreadStatic] public static Action<GameObject> LayerChanging;
        [ThreadStatic] public static Action<Transform> TransformChanging;
        [ThreadStatic] public static Action<GameObject> Activated;
        public static string[] LayerNames = new string[32];
        public static int NextInstanceId = -1;
    }

    public static class Hooks
    {
        [ThreadStatic] public static Action<object> PlayerInput, Death;
        [ThreadStatic] public static Action<object, bool> WallTouch;
        [ThreadStatic] public static Action<object, GameObject, bool> CourseStop;
        [ThreadStatic] public static Action<object, Collider2D> UpgradeBoxStay;
        [ThreadStatic] public static Action<object, bool> ClonesOnScreen;
        [ThreadStatic] public static Action<object> CourseReward, Respawn;
        [ThreadStatic] public static Action<object, string> PlaySfx;
        [ThreadStatic] public static Action<object, int, float, float> PlayAudio;
        [ThreadStatic] public static Action<object, bool> SwapBlocks;
        [ThreadStatic] public static Action<object, float> TimeStop;
        [ThreadStatic] public static Action<object> RefillOrbState;
        [ThreadStatic] public static Action<object> AtomTick, OvergrowthLoad, Teleporter;
        [ThreadStatic] public static Action<object, Vector2> HitSpring;
        [ThreadStatic] public static Action<object> SteppedUp;
        [ThreadStatic] public static Action<object, float> SpecialWallJump;
        [ThreadStatic] public static Action<object> SuperJumpCheck, SpriteDirection, Intangible;

        public static void OnPlayerInput(object movement) { PlayerInput?.Invoke(movement); }
        public static void OnWallTouch(object movement, bool touchedGround) { WallTouch?.Invoke(movement, touchedGround); }
        public static void OnDeath(object movement) { Death?.Invoke(movement); }
        public static void OnCourseStop(object course, GameObject player, bool savePositionData) { CourseStop?.Invoke(course, player, savePositionData); }
        public static void OnUpgradeBoxStay(object box, Collider2D collision) { UpgradeBoxStay?.Invoke(box, collision); }
        public static void OnClonesOnScreen(object clones, bool newOnScreen) { ClonesOnScreen?.Invoke(clones, newOnScreen); }
        public static void OnCourseReward(object course) { CourseReward?.Invoke(course); }
        public static void OnRespawn(object movement, bool manuallyTriggered) { Respawn?.Invoke(movement); }
        public static void OnPlaySfx(object movement, string code) { PlaySfx?.Invoke(movement, code); }
        public static void OnPlayAudio(object audio, int id, float volume, float pitchVariance) { PlayAudio?.Invoke(audio, id, volume, pitchVariance); }
        public static void OnPlayAudioAtPoint(object audio, int id, Vector3 position, float volume, float pitchVariance) { PlayAudio?.Invoke(audio, id, volume, pitchVariance); }
        public static void OnSwapBlocks(object swapper, bool blueActive) { SwapBlocks?.Invoke(swapper, blueActive); }
        public static void OnTimeStop(object movement, float duration) { TimeStop?.Invoke(movement, duration); }
        public static void OnRefillOrbState(object orb, bool regrow) { RefillOrbState?.Invoke(orb); }
        public static void OnAtomTick(object collider) { AtomTick?.Invoke(collider); }
        public static void OnHitSpring(object movement, float upForce, float strength, float movementLock, Vector2 up) { HitSpring?.Invoke(movement, up); }
        public static void OnSpecialWallJump(object movement, float directionMagnitudeMod, float durationMult, Vector2 momentumToAdd) { SpecialWallJump?.Invoke(movement, directionMagnitudeMod); }
        public static void OnSteppedUp(object anim, Vector3 stepUpAmount) { SteppedUp?.Invoke(anim); }
        public static void OnCheckForSuperJump(object movement, bool isDashBounce) { SuperJumpCheck?.Invoke(movement); }
        public static void OnDetermineSpriteDirection(object movement) { SpriteDirection?.Invoke(movement); }
        public static void OnMakeIntangible(object mover) { Intangible?.Invoke(mover); }
        public static void OnOvergrowthLoad(object script) { OvergrowthLoad?.Invoke(script); }
        public static void OnTeleporter(object teleporter, Collider2D collision) { Teleporter?.Invoke(teleporter); }
        public static void OnTeleporterFailsafe(object teleporter, object player) { Teleporter?.Invoke(teleporter); }
    }
}
