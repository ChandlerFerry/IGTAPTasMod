# IGTAP.Box2D — offline Box2D re-host notes

Bit-faithful C# re-host of Unity 6.3's Box2D (2.3.x-derived fork) for offline route
search. Everything lives in `enginesim/Box2D/` (library) and `enginesim/Box2D.Smoke/`
(verification scenario).

## Source provenance

- **Base port:** Box2D.NetStandard v2.4 branch (github.com/codingben/box2d-netstandard,
  commit b34d6238011a97a4f777e88dc41aa31f6b984c7b), MIT, a C# port of Box2D v2.4.x
  with Box2DX lineage. Vendored into `src/` and mechanically transformed
  (`Box2D.NetStandard.*` namespaces flattened into `Box2D`, types renamed to the
  C++ `b2*` names, internals made public where the contract needs them).
- **C++ reference:** erincatto/box2d tag **v2.4.1** (commit 9ebbbcd960ad424e03e5de6e66a40764c16f51bc)
  and tag **v2.3.1** (commit 7e633c4fb86a8bf072fb8ae67ea2c060114750e), used to
  verify/repair expression order and to port the legacy edge algorithm.
- Licenses: `LICENSE-Box2D.txt` = erincatto/box2d v2.4.1 MIT text;
  `LICENSE-Box2D.NetStandard.txt` = the port's MIT license (incl. Box2DX credit).
- New code (not vendored): `api/b2Vec2.cs` (C++-literal vector math),
  `api/b2MathCompat.cs` (C++ b2Min/b2Max/b2Abs/b2Clamp literal semantics),
  `api/b2Facade.cs` (b2Collision free functions, b2QueryCallback/b2RayCastCallback),
  `api/b2WorldState.cs` (SaveState/LoadState), `src/Collision/Colliders/EdgeAndPolygonCollider241.cs`
  (literal port of v2.4.1 b2_collide_edge.cpp).

## Numerics policy

`float` only, C++ expression order preserved in all repaired paths (solver, island,
transform/rotation math, world manifold, position solver manifold, Normalize).
`MathF.Sqrt/Sin/Cos/Atan2` stand in for `sqrtf/sinf/cosf/atan2f` (same values).
Unity's native Box2D is MSVC x64 SSE, /fp:precise, no FMA — .NET RyuJIT x64 also
uses SSE without FMA contraction, so identical expression order gives identical bits
on the same machine/runtime.

### Deviations found in the base port and fixed to C++ semantics

1. `b2Settings.FLT_EPSILON` was `float.Epsilon` (1.4e-45) — C++ `b2_epsilon` is
   1.192092896e-07f. Every degenerate-vector guard (Normalize, hull, GJK, world
   manifold) was effectively disabled. Fixed; `FLT_EPSILON_SQUARED` is derived.
2. `maxTranslationSquared`/`maxRotationSquared` were computed once at static init
   (stale after tuning) — now get-only computed properties (C++ macro semantics),
   like `FLT_EPSILON_SQUARED`, `MaxLinearVelocitySquared` (removed, see below).
3. `b2Vec2.Normalize` divided (`v / len`); C++ multiplies by the reciprocal
   (`invLength = 1/len; v *= invLength`) and leaves the vector unchanged when
   `length < b2_epsilon`. Division vs reciprocal rounds differently. Fixed.
4. `Math.MulT(Matrix3x2, v)` computed `Invert(A) * v`; C++ `b2MulT` is
   `(dot(v, ex), dot(v, ey))` (different rounding, Inf on singular). Fixed. Same
   for `MulT(A, B)`; `Mul(A, B)` was the System.Numerics operator — both are now
   literal C++ column forms.
5. `Math.Mul22(Mat33, v)` had `A.ey.X * v.X` where C++ has `A.ey.x * v.y` — fixed.
6. `b2Vec2.Transform(v, Matrix3x2)` added `+ M31/M32` (breaks `-0.0` results);
   now the literal `b2Mul(b2Mat22, b2Vec2)` form.
7. Block solver LCP cases 2/3 read `K.M22`/`K.M12` where C++ reads `K.ex.y`/`K.ey.x`
   (= `M12`/`M21` here). Value-neutral while K is symmetric, now literal.
8. `Matrex.Invert` lacked C++ `GetInverse`'s `det != 0` guard (1/0 → Inf vs C++'s
   zero matrix). Fixed.
9. Double-precision leaks: `Settings.MixFriction` used `(float)System.Math.Sqrt`
   (double sqrt), `Math.Sqrt(float)` wrapper and legacy `Vec2.Length` likewise.
   All now `MathF.Sqrt`.
10. `b2Settings.Pi`/`Pi2` were `MathF.PI`/(double)System.Math.PI (3.14159274f); C++
    `b2_pi` is 3.14159265359f. All pi constants now the C++ value.
    `angularSleepTolerance` was `2/180` (missing `* pi` vs C++). Fixed.
11. Min/Max/Abs/Clamp now implement the C++ templates exactly (`a > b ? a : b`,
    `b2Clamp(a, low, high) = b2Max(low, b2Min(a, high))`); System.Math differs on
    NaN/signed-zero. All physics call sites route here.

### Dead Box2DX relics removed (never referenced by any code)

`MaxLinearVelocity=200`/`MaxAngularVelocity=250` (+Squared) — there is **no**
velocity clamp anywhere; `ToiSlop`; `MaxProxies`/`MaxPairs`; `InvSqrt` (fast
bit-hack inverse sqrt). If velocity clamping is ever needed it must be an explicit
tunable.

## 2.3.x vs 2.4.1 differences and flags

| Area | 2.3.x (Unity lineage) | 2.4.1 | Control |
|---|---|---|---|
| `b2CollideEdgeAndPolygon` | `b2EPCollider` SAT (`src/Collision/Colliders/EdgeAndPolygonCollider.cs`, vendored = legacy lineage) | rewritten Gauss-map algorithm (`EdgeAndPolygonCollider241.cs`, literal v2.4.1 port) | `b2Settings.UseLegacyEdgePolygon` (default **true** = 2.3.x) |
| `b2CollideEdgeAndCircle` | two-sided + `m_hasVertex` ghosts (vendored inline) | `m_oneSided` early-out + adjacency regions (`CollideEdgeAndCircle241`) | same flag |
| `b2ChainShape`/`b2EdgeShape` ghosts | two-sided edges, optional ghost vertices | one-sided edges, ghosts mandatory (`m_oneSided`) | same flag (`GetChildEdge` sets `m_oneSided = !UseLegacyEdgePolygon`) |
| Contact restitution threshold | global `b2Settings.velocityThreshold = 1.0f` (solver: `vRel < -velocityThreshold`) | per-fixture `restitutionThreshold` mixed per contact (`b2MixRestitutionThreshold`) | port keeps the 2.3.x global (matches Unity). Per-fixture variant NOT ported. |
| Island velocity integration | `v += h * (gravityScale * gravity + invMass * force)` | `v += h * invMass * (gravityScale * mass * gravity + force)` (different rounding: `invMass * mass != 1.0`) | `b2Settings.useLegacyGravityIntegration` (default **true** = 2.3.x) |
| GJK `b2Distance` simplex | barycentrics w/ `GetClosestPoint` divergence check | simplex rewritten (different barycentrics, radii handling reworked) | no flag (deep re-port); port lineage ≈ 2.3-era. **Next suspect for residual ulp diffs.** |
| `b2TimeOfImpact` | separation function formulas + pure bisection root finder | reworked separation + secant/bisection mixed root finder | no flag (deep re-port) |
| `b2DynamicTree`/`b2BroadPhase` | `m_path`, old insert/move heuristics | AVL rebalance, hugeAABB move proxy, moved-flag pair dedup | structural; affects pair/creation ORDER, no flag |
| `b2PolygonShape::Set` dedup + centroid/mass reference point | `< 0.5f * linearSlop` (unsquared), origin reference | `< (0.5f*linearSlop)^2`, first-vertex reference | no flag; verify when centroid-side ulp diffs appear |
| `b2AABB::RayCast` | old slab code | rewritten slab code | no flag |
| Body `active`/`enabled` naming, `awake` on statics | `SetActive`, statics may be "awake" | `SetEnabled`, awake ignored for statics | API has `SetEnabled` (+2.3 naming kept where harmless) |

The vendored solver is 2.3-style (`velocityThreshold` global) which matches Unity;
the solver/island/transform expression order was repaired to be literal C++
(identical between 2.3.1 and 2.4.1 except the two flagged rows).

## Public API (namespace `Box2D`, C++ v2 names)

`b2World(b2Vec2 gravity)`, `CreateBody(b2BodyDef)`, `DestroyBody`, `Step(float dt,
int velocityIterations, int positionIterations)`, `SetContactListener`,
`SetContactFilter` (`b2ContactFilter.ShouldCollide(b2Fixture, b2Fixture)`),
`SetContactListener(b2ContactListener)` (Begin/End/Pre/Post), `RayCast`/`QueryAABB`
(delegates on `b2World` + `b2RayCastCallback`/`b2QueryCallback` class adapters),
`GetBodyList`/`GetContactList`/`GetBodyCount`/`GetContactCount`/`GetGravity`/
`SetGravity`/`SetAutoClearForces`/`GetAutoClearForces`/`ClearForces`/
`SetContinuousPhysics`/`SetSubStepping`/`SetWarmStarting`/`SetAllowSleeping`/
`GetProfile`, `SetDestructionListener`, `SetDebugDraw`.

`b2Body`: `CreateFixture(b2FixtureDef)` (returns `b2Fixture`), `SetTransform`,
`GetTransform`/`GetPosition`/`GetAngle`/`GetWorldCenter`/`GetLocalCenter`,
`Set/GetLinearVelocity`, `Set/GetAngularVelocity`, `ApplyForce(ToCenter)`,
`ApplyTorque`, `ApplyLinearImpulse(ToCenter)`, `ApplyAngularImpulse`, `SetType`
(`b2BodyType.b2_staticBody/b2_kinematicBody/b2_dynamicBody`), `Set/IsBullet`,
`Set/IsFixedRotation`, `Set/IsSleepingAllowed`, `Set/IsAwake`, `Set/IsEnabled`,
`Set/GetGravityScale`, `Set/GetLinearDamping`, `Set/GetAngularDamping`,
`GetFixtureList`/`GetContactList`/`GetNext`, `GetMass`/`GetInertia`/`GetMassData`/
`SetMassData`, `GetWorldPoint/Vector`, `GetLocalPoint/Vector`, `UserData` (object,
settable).

`b2Fixture`: `GetBody`/`GetShape`/`GetNext`, `IsSensor`, `TestPoint(in b2Vec2)`,
`RayCast(out b2RayCastOutput, in b2RayCastInput, int childIndex)`, `GetAABB(int)`,
`Get/SetFilterData(b2Filter { categoryBits, maskBits, groupIndex })`,
`Get/SetFriction`, `Get/SetRestitution`, `Get/SetDensity`, `GetChildCount`,
`UserData` (object, settable).

Shapes: `b2PolygonShape.SetAsBox(hx, hy)` / `SetAsBox(hx, hy, in center, angle)` /
`bool Set(b2Vec2[] points, int count)` (true = valid hull); `b2CircleShape` (`m_p`,
`m_radius`); `b2EdgeShape.SetTwoSided(v1, v2)` / `SetOneSided(v0..v3)` (+2.3-style
ghost fields `m_vertex0/m_vertex3`); `b2ChainShape.CreateLoop(b2Vec2[])`,
`CreateChain(b2Vec2[], in prev, in next)`, `CreateChain(b2Vec2[] verts, int count,
in prev, in next)`, `GetChildEdge`. All shapes: **`m_radius` is a public mutable
field** (Unity sets polygon radius = contact offset + edge radius).

`b2Contact`: `GetFixtureA/B`, `GetChildIndexA/B`, `IsTouching`, `IsEnabled`,
`SetEnabled`, `GetManifold() → b2Manifold` (live object; `points[i].normalImpulse/
tangentImpulse/id`), `GetWorldManifold(out b2WorldManifold)` (`normal`, `points`,
`separations`), `GetNext`, `Get/SetFriction`, `Get/SetRestitution`,
`Get/SetTangentSpeed`, `Update(b2ContactListener)`.

Static functions (`using static Box2D.b2Collision;` for bare names):
`b2Distance(out b2DistanceOutput, ref b2SimplexCache, in b2DistanceInput)`,
`b2TimeOfImpact(out b2TOIOutput, in b2TOIInput)`, `b2TestOverlap(in b2AABB, in b2AABB)`,
`b2TestOverlap(shapeA, iA, shapeB, iB, xfA, xfB)`.

### Branching API

`object b2World.SaveState()` / `void b2World.LoadState(object state)` — exact,
in-place snapshot/restore. After `LoadState`, stepping is bit-identical to stepping
at `SaveState` time (verified by smoke: `SAVESTATE OK`). Contract:
- Static bodies and their fixtures must not change between save/load (level
  geometry). They are not copied; the dynamic-tree node array is (Array.Copy).
- Non-static bodies keep identity; their mutable state and fixture lists are
  restored exactly as saved (the saved `b2Fixture` objects themselves are restored;
  `UserData` preserved).
- Contacts restore manifold (points, impulses, ids), flags and link order exactly;
  live contact objects are reused when the fixture pair is unchanged.
- Body creation/destruction between save/load is unsupported: `LoadState` throws
  `InvalidOperationException` if the body count differs.

### World cloning

`b2World.Clone()` is **not** implemented; per contract this documents the
determinism guarantee instead: creating a fresh world and replaying yields
bit-identical results (smoke: `DETERMINISM OK`), and `SaveState`/`LoadState`
provides exact branching at lower cost than cloning object graphs (an exact clone
must preserve broadphase proxy ids and intrusive list order — which the in-place
snapshot already does). If object-graph cloning is ever needed it can be layered on
`SaveState` with a body/fixture reference map.

## Verification

`dotnet build enginesim/Box2D/IGTAP.Box2D.csproj -c Release` — 0 errors, 0 warnings.

`dotnet run -c Release --project enginesim/Box2D.Smoke` prints the 60-step
trajectory (9 significant digits) for the specced scenario (dynamic box 15x19,
fixed rotation, polygon radius 0.01, velocity forced to (460, -939) per step,
static ground + wall, dt = 0.0199999921f, 8/3 iterations, zero gravity) and:

```
DETERMINISM OK
SAVESTATE OK
EDGEPATH OK (UseLegacyEdgePolygon=false deterministic)
EDGECOLLIDE OK (edge + chain paths executed in both modes)
BENCH steps/sec=~200000 (120000 steps in ~0.6s)
```

Measured throughput for the smoke scenario (3 bodies, 2-3 contacts, 8 velocity /
3 position iterations): **~190k-215k steps/sec** single-threaded Release x64.

## SaveState/LoadState correctness fixes (found via the CLI replay suite)

Three real bugs were found and fixed while validating branching against
`enginesim/Cli` replay/bench (fixture churn + repeated LoadState):

1. **Mixed-side contact edges.** A body's `m_contactList` mixes `m_nodeA` and
   `m_nodeB` edges (contact (P,Q) puts nodeA on P's list; contact (R,P) puts
   nodeB on P's list). The first restore always linked `.m_nodeA`, corrupting
   edge chains so `DestroyFixture` could miss contacts (stale contacts -> NRE in
   `ContactManager.Collide`). Edge links are now recorded/restored as
   (record index, side) pairs.
2. **Fixture proxy bookkeeping.** `FixtureProxy` objects are shared with the
   dynamic tree and mutate across save/load (teardown sets `proxyId = -1`,
   `m_proxyCount = 0`; sync updates `aabb`). The snapshot now captures/restores
   per-fixture `m_proxyCount`, `m_proxies[i].proxyId` and `.aabb` alongside the
   wholesale tree copy — without this, a restored-then-re-destroyed fixture left
   live tree nodes for a detached fixture (NRE in `b2World.RayCast`).
3. **Static fixtures do churn.** Unity's backend rebuilds colliders on
   activation (`ActiveChanged`/`ColliderChanged`), including statics. The
   snapshot now records every body's fixture list and fixture value state
   (statics included), keeping the fixture set consistent with the restored
   broadphase.

Also hardened to C++ semantics: `b2Body.DestroyFixture` now tears down proxies
whenever any remain (idempotent `DestroyProxies`), and a stale/duplicate destroy
proceeds like C++ (assert-only check) instead of throwing.

`b2Sweep.GetTransform` now uses the C++ form `(1 - beta) * c0 + beta * c` (the
port's `c0 + beta * (c - c0)` rounds differently).

## Known gaps / next suspects

- Two residual tick-level diffs remain in the 23-trace suite (all [MATCH] at
  result level; 21/23 are tick-identical): `rand-c1-wj-0` tick 262 (forced
  velocity `y: -21` vs `-20.9999905`) and `rand-c2-wjdash-1` tick 79 (position
  `x` 1-ulp `4074.37524` vs `4074.37549`). The manifold generators
  (`b2CollidePolygons`, `b2CollidePolygonAndCircle`, `b2CollideCircles`),
  `b2ClipSegmentToLine` and `b2Sweep` were verified expression-order-literal
  against v2.4.1; the remaining suspects are the unported GJK/TOI lineage (see
  table above) and `b2DynamicTree`/broadphase pair ordering.
- The manifold generators (`src/Collision/Colliders/PolygonAndPolygonCollider.cs`,
  the inline circle/edge-circle colliders in the contact classes) are the base
  port's rewrites and were **not** yet verified expression-order-against-C++. They
  are the next suspect for any residual 1-ulp differences (compare against
  v2.4.1 `b2_collide_polygon.cpp` / `b2_collide_circle.cpp` literally).
- GJK `b2Distance` and `b2TimeOfImpact` remain the base port's lineage (see table).
- `Collision.ClipSegmentToLine` allocates its `out` array per call (hot-path GC
  churn; numerics unaffected). Same for per-step `ContactVelocityConstraint`
  allocation in `ContactSolver`. Not per-`Step`-allocating beyond these.
- `b2Draw`/`DebugDraw` exists for parity but is unused by the offline engine.
- Joints ARE ported (full suite) but unused by the offline engine so far.
