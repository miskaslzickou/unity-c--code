using System;
using System.Collections.Generic;
using UnityEngine;
using GameSystems.Events;
using GameSystems.Persistence;

namespace GameSystems.PhysicsAndCombat
{
    // =========================================================================
    // 1. BALLISTIC & AMMUNITION ENUMS AND STRUCTS
    // =========================================================================

    public enum AmmunitionType
    {
        AP_Kinetic,
        APFSDS_Dart,
        HE_HighExplosive,
        HEAT_ShapedCharge
    }

    public enum ImpactResultType
    {
        Ricochet,
        NonPenetrationBlocked,
        PartialPenetration,
        FullPenetration,
        Overpenetration
    }

    [Serializable]
    public struct ProjectileBallisticData
    {
        public AmmunitionType AmmoType;
        [Tooltip("Projectile mass in kilograms")]
        public float MassKg;
        [Tooltip("Caliber diameter in millimeters")]
        public float CaliberMm;
        [Tooltip("Muzzle velocity in meters/second")]
        public float VelocityM_s;
        [Tooltip("Maximum base penetration at 0 degrees incidence (RHA millimeters)")]
        public float BasePenetrationMm;
        [Tooltip("High explosive blast filler mass in kg (TNT equivalent)")]
        public float HEFillerMassKg;
        [Tooltip("Armor normalization angle in degrees")]
        public float NormalizationDeg;
        [Tooltip("Angle at which ricochet chance begins (degrees from normal)")]
        public float RicochetAngleDeg;
    }

    // =========================================================================
    // 2. ZERO-GC EVENT BUS STRUCTS
    // =========================================================================

    public readonly struct ArmorPenetrationEvent : IGameEvent
    {
        public readonly ImpactResultType Result;
        public readonly Vector3 ImpactPoint;
        public readonly Vector3 Normal;
        public readonly float CaliberMm;
        public readonly float EffectiveArmorMm;
        public readonly float ResidualVelocityM_s;

        public ArmorPenetrationEvent(ImpactResultType result, Vector3 point, Vector3 normal, float caliber, float effArmor, float resVel)
        {
            Result = result;
            ImpactPoint = point;
            Normal = normal;
            CaliberMm = caliber;
            EffectiveArmorMm = effArmor;
            ResidualVelocityM_s = resVel;
        }
    }

    public readonly struct SpallingShrapnelHitEvent : IGameEvent
    {
        public readonly Vector3 ShrapnelHitPoint;
        public readonly float ShrapnelDamage;
        public readonly int TargetEntityId;

        public SpallingShrapnelHitEvent(Vector3 point, float damage, int targetId)
        {
            ShrapnelHitPoint = point;
            ShrapnelDamage = damage;
            TargetEntityId = targetId;
        }
    }

    public readonly struct ExplosiveBlastEvent : IGameEvent
    {
        public readonly Vector3 Epicenter;
        public readonly float BlastRadiusMeters;
        public readonly float OverpressureDamage;

        public ExplosiveBlastEvent(Vector3 center, float radius, float damage)
        {
            Epicenter = center;
            BlastRadiusMeters = radius;
            OverpressureDamage = damage;
        }
    }

    // =========================================================================
    // 3. ARMOR PLATE COMPONENT (CAN ATTACH TO ANY 2D OR 3D OBJECT)
    // =========================================================================

    /// <summary>
    /// Armor plate definition for vehicles, fortifications, and body armor.
    /// Supports Rolled Homogeneous Armor (RHA) equivalents, spaced armor, and Explosive Reactive Armor (ERA).
    /// </summary>
    [DisallowMultipleComponent]
    public class VehicleArmorPlate : MonoBehaviour
    {
        [Header("Armor Thickness & Material")]
        [Tooltip("Physical plate thickness in millimeters")]
        public float ThicknessMm = 50f;
        [Tooltip("Hardness / Material quality multiplier (RHA = 1.0, Cast = 0.9, Composite = 1.35)")]
        public float MaterialMultiplier = 1.0f;
        [Tooltip("Spaced armor standoff distance")]
        public bool IsSpacedArmor = false;
        public float SpacedStandoffMm = 100f;

        [Header("Explosive Reactive Armor (ERA)")]
        public bool HasERA = false;
        public bool IsERADetonated = false;
        [Tooltip("Penetration reduction against HEAT (in mm RHA)")]
        public float ERAReductionHEAT = 350f;
        [Tooltip("Penetration reduction against kinetic AP (in mm RHA)")]
        public float ERAReductionKinetic = 80f;

        public float EffectiveBaseRHA => ThicknessMm * MaterialMultiplier;

        public float TriggerERA(AmmunitionType ammo)
        {
            if (!HasERA || IsERADetonated) return 0f;
            IsERADetonated = true;
            return (ammo == AmmunitionType.HEAT_ShapedCharge) ? ERAReductionHEAT : ERAReductionKinetic;
        }
    }

    // =========================================================================
    // 4. MASTER BALLISTICS AND PENETRATION SOLVER (2D & 3D COMPATIBLE)
    // =========================================================================

    /// <summary>
    /// High-fidelity physical armor penetration and terminal internal ballistics solver.
    /// Simulates DeMarre / Lanz-Odermatt kinetic penetration, shaped-charge chemical jets (HEAT),
    /// HE blast overpressure, extreme-angle ricochet reflections, and internal secondary spalling shrapnel cones.
    /// Fully compatible with both 3D (Physics) and 2D (Physics2D) collision pipelines.
    /// </summary>
    [DisallowMultipleComponent]
    public class BallisticsAndPenetrationSolver : MonoBehaviour, ISaveableSystem
    {
        public static BallisticsAndPenetrationSolver Instance { get; private set; }

        [Header("Spalling Configuration")]
        [Tooltip("Number of shrapnel fragments generated in the internal spalling cone upon penetration")]
        [Range(3, 32)] [SerializeField] private int spallFragmentCount = 12;
        [Tooltip("Half-angle of the internal spalling cone in degrees")]
        [Range(5f, 45f)] [SerializeField] private float spallConeHalfAngleDeg = 25f;
        [Tooltip("Maximum travel distance of internal shrapnel rays")]
        [SerializeField] private float maxSpallDistance = 4.0f;
        [Tooltip("Internal module layer mask for spall damage")]
        [SerializeField] private LayerMask internalModulesLayerMask = ~0;

        [Header("Ricochet Physics")]
        [Tooltip("Friction loss during ricochet deflection")]
        [Range(0.1f, 0.9f)] [SerializeField] private float ricochetVelocityRetention = 0.65f;

        [Header("Spatial Mode")]
        [SerializeField] private bool defaultTo2DPhysics = false;
        public bool DefaultTo2DPhysics => defaultTo2DPhysics;

        // Pre-allocated non-alloc raycast buffers to guarantee zero GC during impacts
        private readonly RaycastHit[] spallHits3D = new RaycastHit[16];
        private readonly RaycastHit2D[] spallHits2D = new RaycastHit2D[16];
        private readonly Collider[] blastColliders3D = new Collider[32];
        private readonly Collider2D[] blastColliders2D = new Collider2D[32];

        public string SaveKey => "BallisticsAndPenetrationSolver";

        private void Awake()
        {
            if (Instance == null) Instance = this;
            else if (Instance != this) { Destroy(gameObject); return; }
        }

        // =========================================================================
        // PUBLIC API: IMPACT & PENETRATION EVALUATION
        // =========================================================================

        public ImpactResultType EvaluateImpact3D(
            in ProjectileBallisticData projectile,
            Vector3 impactVelocity,
            RaycastHit hit,
            out Vector3 outRicochetVelocity,
            out float residualSpeed)
        {
            return SolveImpactInternal(
                projectile,
                impactVelocity,
                hit.point,
                hit.normal,
                hit.collider.GetComponent<VehicleArmorPlate>(),
                hit.collider.gameObject,
                false,
                out outRicochetVelocity,
                out residualSpeed);
        }

        public ImpactResultType EvaluateImpact2D(
            in ProjectileBallisticData projectile,
            Vector2 impactVelocity,
            RaycastHit2D hit,
            out Vector2 outRicochetVelocity,
            out float residualSpeed)
        {
            Vector3 ricochet3D;
            var res = SolveImpactInternal(
                projectile,
                impactVelocity,
                hit.point,
                hit.normal,
                hit.collider.GetComponent<VehicleArmorPlate>(),
                hit.collider.gameObject,
                true,
                out ricochet3D,
                out residualSpeed);

            outRicochetVelocity = new Vector2(ricochet3D.x, ricochet3D.y);
            return res;
        }

        private ImpactResultType SolveImpactInternal(
            in ProjectileBallisticData proj,
            Vector3 impactVelocity,
            Vector3 hitPoint,
            Vector3 surfaceNormal,
            VehicleArmorPlate armorPlate,
            GameObject hitObject,
            bool is2D,
            out Vector3 ricochetVel,
            out float residualSpeed)
        {
            ricochetVel = Vector3.zero;
            residualSpeed = 0f;

            float currentSpeed = impactVelocity.magnitude;
            if (currentSpeed < 0.1f) return ImpactResultType.NonPenetrationBlocked;
            Vector3 flightDir = impactVelocity / currentSpeed;

            // 1. Obliquity Angle: angle between -flightDir and surface normal
            float cosTheta = Mathf.Clamp01(Vector3.Dot(-flightDir, surfaceNormal));
            float impactAngleDeg = Mathf.Acos(cosTheta) * Mathf.Rad2Deg;

            // 2. High Explosive (HE) detonates immediately on contact
            if (proj.AmmoType == AmmunitionType.HE_HighExplosive)
            {
                TriggerExplosiveBlast(hitPoint, proj.HEFillerMassKg, is2D);
                EventBus<ArmorPenetrationEvent>.Raise(new ArmorPenetrationEvent(
                    ImpactResultType.FullPenetration, hitPoint, surfaceNormal, proj.CaliberMm, 0f, 0f));
                return ImpactResultType.FullPenetration;
            }

            // 3. Caliber Overmatch Calculation
            float armorThicknessMm = (armorPlate != null) ? armorPlate.ThicknessMm : 20f;
            float overmatchRatio = proj.CaliberMm / Mathf.Max(1f, armorThicknessMm);
            bool isOvermatched = overmatchRatio >= 2.0f;

            // 4. Ricochet Check
            float effectiveRicochetAngle = proj.RicochetAngleDeg > 0 ? proj.RicochetAngleDeg : 70f;
            if (isOvermatched) effectiveRicochetAngle += 10f; // overmatch delays ricochet

            if (impactAngleDeg >= effectiveRicochetAngle)
            {
                // Bullet ricochets across the surface
                Vector3 reflectedDir = Vector3.Reflect(flightDir, surfaceNormal).normalized;
                ricochetVel = reflectedDir * (currentSpeed * ricochetVelocityRetention);

                EventBus<ArmorPenetrationEvent>.Raise(new ArmorPenetrationEvent(
                    ImpactResultType.Ricochet, hitPoint, surfaceNormal, proj.CaliberMm, 0f, ricochetVel.magnitude));
                return ImpactResultType.Ricochet;
            }

            // 5. Normalization Angle
            float effectiveAngleDeg = Mathf.Max(0f, impactAngleDeg - proj.NormalizationDeg);
            float rad = effectiveAngleDeg * Mathf.Deg2Rad;
            float effectiveCos = Mathf.Max(0.1f, Mathf.Cos(rad));

            // Line-of-Sight effective thickness
            float baseRHA = (armorPlate != null) ? armorPlate.EffectiveBaseRHA : 20f;
            float losEffectiveArmorMm = baseRHA / effectiveCos;

            // Explosive Reactive Armor (ERA) interaction
            if (armorPlate != null && armorPlate.HasERA)
            {
                float eraReduction = armorPlate.TriggerERA(proj.AmmoType);
                losEffectiveArmorMm = Mathf.Max(5f, losEffectiveArmorMm - eraReduction);
            }

            // 6. Penetration Capacity Calculation
            float penetrationMm;
            if (proj.AmmoType == AmmunitionType.HEAT_ShapedCharge)
            {
                // HEAT penetration is independent of flight velocity
                penetrationMm = proj.BasePenetrationMm;
                // Spaced armor causes premature standoff detonation
                if (armorPlate != null && armorPlate.IsSpacedArmor)
                {
                    penetrationMm *= 0.4f;
                }
            }
            else
            {
                // Kinetic DeMarre scaling: P = P0 * (v / v0)^1.4
                float velocityRatio = currentSpeed / Mathf.Max(1f, proj.VelocityM_s);
                penetrationMm = proj.BasePenetrationMm * Mathf.Pow(velocityRatio, 1.4f);
            }

            // 7. Penetration Outcome
            if (penetrationMm < losEffectiveArmorMm)
            {
                // Non-penetration: projectile shattered or absorbed
                EventBus<ArmorPenetrationEvent>.Raise(new ArmorPenetrationEvent(
                    ImpactResultType.NonPenetrationBlocked, hitPoint, surfaceNormal, proj.CaliberMm, losEffectiveArmorMm, 0f));
                return ImpactResultType.NonPenetrationBlocked;
            }

            // Full penetration: calculate residual velocity and generate Spalling Cone
            float residualEnergyRatio = 1f - (losEffectiveArmorMm / penetrationMm);
            residualSpeed = currentSpeed * Mathf.Sqrt(Mathf.Max(0.05f, residualEnergyRatio));

            ImpactResultType result = (residualEnergyRatio > 0.6f) ? ImpactResultType.Overpenetration : ImpactResultType.FullPenetration;

            EventBus<ArmorPenetrationEvent>.Raise(new ArmorPenetrationEvent(
                result, hitPoint, surfaceNormal, proj.CaliberMm, losEffectiveArmorMm, residualSpeed));

            // 8. Generate Secondary Internal Spalling Cone
            GenerateSpallingCone(hitPoint, flightDir, proj.CaliberMm, residualSpeed, is2D);

            return result;
        }

        private void GenerateSpallingCone(Vector3 origin, Vector3 forwardDir, float caliberMm, float residualSpeed, bool is2D)
        {
            float baseShrapnelDamage = (caliberMm * 0.5f) + (residualSpeed * 0.1f);

            for (int i = 0; i < spallFragmentCount; i++)
            {
                // Perturb forward vector within half-angle cone
                float angle = UnityEngine.Random.Range(-spallConeHalfAngleDeg, spallConeHalfAngleDeg);
                Vector3 rayDir;

                if (is2D)
                {
                    float rad = angle * Mathf.Deg2Rad;
                    float cos = Mathf.Cos(rad);
                    float sin = Mathf.Sin(rad);
                    rayDir = new Vector3(
                        forwardDir.x * cos - forwardDir.y * sin,
                        forwardDir.x * sin + forwardDir.y * cos,
                        0f).normalized;

                    int hits = Physics2D.RaycastNonAlloc(new Vector2(origin.x, origin.y), new Vector2(rayDir.x, rayDir.y), spallHits2D, maxSpallDistance, internalModulesLayerMask);
                    for (int h = 0; h < hits; h++)
                    {
                        var hit = spallHits2D[h];
                        if (hit.collider != null)
                        {
                            ApplyModuleDamage(hit.collider.gameObject, baseShrapnelDamage);
                            EventBus<SpallingShrapnelHitEvent>.Raise(new SpallingShrapnelHitEvent(
                                hit.point, baseShrapnelDamage, hit.collider.gameObject.GetInstanceID()));
                            break;
                        }
                    }
                }
                else
                {
                    Quaternion rot = Quaternion.AngleAxis(angle, Vector3.Cross(forwardDir, Vector3.up).normalized);
                    rayDir = rot * forwardDir;

                    int hits = Physics.RaycastNonAlloc(origin, rayDir, spallHits3D, maxSpallDistance, internalModulesLayerMask);
                    for (int h = 0; h < hits; h++)
                    {
                        var hit = spallHits3D[h];
                        if (hit.collider != null)
                        {
                            ApplyModuleDamage(hit.collider.gameObject, baseShrapnelDamage);
                            EventBus<SpallingShrapnelHitEvent>.Raise(new SpallingShrapnelHitEvent(
                                hit.point, baseShrapnelDamage, hit.collider.gameObject.GetInstanceID()));
                            break;
                        }
                    }
                }
            }
        }

        private void TriggerExplosiveBlast(Vector3 epicenter, float tntKg, bool is2D)
        {
            // Blast radius: R = 2.5 * (TNT_kg)^(1/3)
            float blastRadius = 2.5f * Mathf.Pow(Mathf.Max(0.1f, tntKg), 0.333f);
            float maxDamage = tntKg * 150f;

            EventBus<ExplosiveBlastEvent>.Raise(new ExplosiveBlastEvent(epicenter, blastRadius, maxDamage));

            if (is2D)
            {
                int count = Physics2D.OverlapCircleNonAlloc(new Vector2(epicenter.x, epicenter.y), blastRadius, blastColliders2D, internalModulesLayerMask);
                for (int i = 0; i < count; i++)
                {
                    if (blastColliders2D[i] != null)
                    {
                        float dist = Vector2.Distance(epicenter, blastColliders2D[i].transform.position);
                        float dmg = Mathf.Lerp(maxDamage, 10f, dist / blastRadius);
                        ApplyModuleDamage(blastColliders2D[i].gameObject, dmg);
                    }
                }
            }
            else
            {
                int count = Physics.OverlapSphereNonAlloc(epicenter, blastRadius, blastColliders3D, internalModulesLayerMask);
                for (int i = 0; i < count; i++)
                {
                    if (blastColliders3D[i] != null)
                    {
                        float dist = Vector3.Distance(epicenter, blastColliders3D[i].transform.position);
                        float dmg = Mathf.Lerp(maxDamage, 10f, dist / blastRadius);
                        ApplyModuleDamage(blastColliders3D[i].gameObject, dmg);
                    }
                }
            }
        }

        private void ApplyModuleDamage(GameObject targetObj, float damage)
        {
            var hpComp = targetObj.GetComponent<HP>();
            if (hpComp != null)
            {
                hpComp.TakeDamage(Mathf.RoundToInt(damage));
            }
        }

        // =========================================================================
        // SAVE & RESTORE (ISaveableSystem)
        // =========================================================================

        public object CaptureState() => new object();
        public void RestoreState(string stateJson) { }
    }
}
