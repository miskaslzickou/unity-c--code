using System;
using UnityEngine;

namespace GameSystems.Animation2D
{
#pragma warning disable 0649 // Potlačení varování kompilátoru pro privátní [SerializeField] pole přiřazovaná v Unity Inspectoru
    /// <summary>
    /// Režim inverzní kinematiky pro 2D řetězce kostí.
    /// </summary>
    public enum IKSolverMode2D
    {
        /// <summary>
        /// Přesné analytické goniometrické řešení (kosinová věta) pro 2-kostní řetězce (ruce, nohy, lokty, kolena).
        /// Extrémně rychlé, deterministické, nulová iterace.
        /// </summary>
        TwoBoneAnalytic,

        /// <summary>
        /// Iterační algoritmus FABRIK (Forward And Backward Reaching Inverse Kinematics)
        /// pro N-kostní řetězce (chapadla, pavoučí nohy, průmyslová robotická ramena, ocasy).
        /// </summary>
        FABRIKChain
    }

    /// <summary>
    /// Definice úhlového omezení pro jednotlivý kloub v N-kostním řetězci.
    /// </summary>
    [Serializable]
    public struct JointAngularConstraint2D
    {
        [Tooltip("Povolit úhlové omezení pro tento kloub.")]
        public bool EnableConstraint;

        [Range(-180f, 180f), Tooltip("Minimální povolený relativní úhel vůči rodičovské kosti ve stupních.")]
        public float MinAngle;

        [Range(-180f, 180f), Tooltip("Maximální povolený relativní úhel vůči rodičovské kosti ve stupních.")]
        public float MaxAngle;

        public JointAngularConstraint2D(bool enable, float min, float max)
        {
            EnableConstraint = enable;
            MinAngle = min;
            MaxAngle = max;
        }
    }

    /// <summary>
    /// Vysoce výkonný 2D Procedurální IK Řešič (Inverse Kinematics Solver) s podporou
    /// analytického 2-Bone IK, N-kostního FABRIK řešiče, procedurálního krokování (Spider/Mech Walker)
    /// a míření zbraní/nástrojů s automatickým zrcadlením.
    /// Navrženo pro Zero-GC za běhu (nulové alokace v LateUpdate).
    /// </summary>
    [SelectionBase]
    [DisallowMultipleComponent]
    public class ProceduralIKSolver2D : MonoBehaviour
    {
        // =========================================================================
        // 1. NASTAVENÍ ŘEŠIČE (SOLVER SETTINGS)
        // =========================================================================

        [Header("Režim a Cíl IK (Solver Mode & Target)")]
        [Tooltip("Zvolený algoritmus pro výpočet inverzní kinematiky.")]
        [SerializeField] private IKSolverMode2D solverMode = IKSolverMode2D.TwoBoneAnalytic;

        [Tooltip("Cílový transform, kterého se má koncový bod řetězce (Tip) dotknout.")]
        [SerializeField] private Transform target;

        [Tooltip("Ruční globální pozice cíle, pokud není přiřazen Target Transform.")]
        [SerializeField] private Vector2 customTargetPosition;

        [Range(0f, 1f), Tooltip("Celková váha IK vlivu (0 = původní animace, 1 = plný IK vliv).")]
        [SerializeField] private float weight = 1f;

        [Tooltip("Základní osa orientace kostí v klidovém stavu (typicky Vector2.right = X doprava).")]
        [SerializeField] private Vector2 boneAxis = Vector2.right;

        // =========================================================================
        // 2. NASTAVENÍ PRO ANALYTICKÝ 2-BONE IK
        // =========================================================================

        [Header("Dvou-kostní Analytické IK (Two-Bone Analytic)")]
        [Tooltip("Kořenová kost (např. rameno nebo stehno).")]
        [SerializeField] private Transform rootBone;

        [Tooltip("Prostřední kloub (např. loket nebo koleno).")]
        [SerializeField] private Transform midBone;

        [Tooltip("Koncová kost / efektor (např. dlaň nebo chodidlo).")]
        [SerializeField] private Transform tipBone;

        [Tooltip("Obrátit směr ohybu kloubu (např. lidské koleno vs. ptačí/psí kloub).")]
        [SerializeField] private bool flipBendDirection = false;

        [Tooltip("Měkké utlumení tahu při natažení na maximální délku (zabraňuje trhání).")]
        [SerializeField] private bool enableSoftStretching = false;

        // =========================================================================
        // 3. NASTAVENÍ PRO N-KOSTNÍ FABRIK ŘETĚZEC
        // =========================================================================

        [Header("FABRIK N-Kostní Řetězec (FABRIK Chain)")]
        [Tooltip("Seřazený seznam kostí řetězce od kořene (index 0) po koncový bod.")]
        [SerializeField] private Transform[] fabrikBones = new Transform[0];

        [Range(1, 30), Tooltip("Maximální počet iterací FABRIK řešiče na jeden snímek.")]
        [SerializeField] private int maxFabrikIterations = 15;

        [Range(0.0001f, 0.05f), Tooltip("Toleranční vzdálenost od cíle, při které se iterace zastaví.")]
        [SerializeField] private float fabrikTolerance = 0.002f;

        [Tooltip("Úhlová omezení pro jednotlivé kosti v řetězci.")]
        [SerializeField] private JointAngularConstraint2D[] angularConstraints = new JointAngularConstraint2D[0];

        // =========================================================================
        // 4. PROCEDURÁLNÍ KROKOVÁNÍ (PROCEDURAL STEPPING / WALKER)
        // =========================================================================

        [Header("Procedurální Krokování (Procedural Stepper / Spider Leg)")]
        [Tooltip("Povolit procedurální krokování a hledání povrchu pod nohou.")]
        [SerializeField] private bool enableProceduralStepping = false;

        [Tooltip("Referenční transform ideální pozice nohy (Home Position) ukotvený na těle.")]
        [SerializeField] private Transform homePositionTransform;

        [Tooltip("Relativní offset vůči tělu, pokud není přiřazen Home Transform.")]
        [SerializeField] private Vector2 homePositionOffset = new Vector2(1f, -1.5f);

        [Tooltip("Vrstvy terénu a překážek pro detekci došlapu (Physics2D.Raycast).")]
        [SerializeField] private LayerMask groundLayerMask = ~0;

        [Min(0.05f), Tooltip("Vzdálenost od ideální pozice, která vyvolá zahájení nového kroku.")]
        [SerializeField] private float stepDistanceThreshold = 0.6f;

        [Range(0.05f, 2f), Tooltip("Maximální výška parabolického oblouku zvednuté nohy během kroku.")]
        [SerializeField] private float stepHeight = 0.35f;

        [Range(1f, 30f), Tooltip("Rychlost animace kroku.")]
        [SerializeField] private float stepSpeed = 8f;

        [Range(0f, 1f), Tooltip("Overshoot předsazení došlapu ve směru pohybu těla (kompenzace setrvačnosti).")]
        [SerializeField] private float stepOvershootMultiplier = 0.25f;

        [Tooltip("Opačné nohy zapojené do alternující chůze (tato noha nezačne krok, dokud druhá došlapuje).")]
        [SerializeField] private ProceduralIKSolver2D[] linkedOppositeLegs = new ProceduralIKSolver2D[0];

        // =========================================================================
        // 5. MÍŘENÍ NÁSTROJŮ A ZBRANÍ (AIMING HELPER)
        // =========================================================================

        [Header("Míření Nástrojů / Zbraní (Aim Tip At Target)")]
        [Tooltip("Povolit natočení koncového bodu / zbraně přímo na cíl.")]
        [SerializeField] private bool enableAimAtTarget = false;

        [Tooltip("Transform zbraně nebo sprite držáku, který se má otáčet.")]
        [SerializeField] private Transform aimTransform;

        [Tooltip("Automaticky převrátit svislou osu sprite (Scale Y = -1), pokud míříme doleva.")]
        [SerializeField] private bool autoFlipAimY = true;

        [Range(-180f, 180f), Tooltip("Korekční úhlový offset zbraně ve stupních.")]
        [SerializeField] private float aimAngleOffset = 0f;

        // =========================================================================
        // VNITŘNÍ PRE-ALOKOVANÉ VYMEZENÍ PAMĚTI (PRE-ALLOCATED INTERNAL CACHES)
        // =========================================================================

        // 2-Bone Cache
        private float length0;
        private float length1;
        private float totalTwoBoneLength;
        private float rootBoneInitialAngleOffset;
        private float midBoneInitialAngleOffset;

        // FABRIK Cache
        private Vector2[] fabrikPositions;
        private float[] fabrikLengths;
        private float totalFabrikLength;
        private float[] fabrikInitialAngleOffsets;

        // Stepper State
        private Vector2 currentFoothold;
        private Vector2 stepStartFoothold;
        private Vector2 stepTargetFoothold;
        private float stepProgress = 1f;
        private bool isCurrentlyStepping = false;
        private Vector2 lastBodyPosition;
        private Vector2 bodyVelocityEstimate;

        // Public Properties
        public bool IsStepping => isCurrentlyStepping;
        public IKSolverMode2D SolverMode { get => solverMode; set => solverMode = value; }
        public Transform Target { get => target; set => target = value; }
        public float Weight { get => weight; set => weight = Mathf.Clamp01(value); }

        // =========================================================================
        // LIFECYCLE & INICIALIZACE (ZERO-GC CACHING)
        // =========================================================================

        private void Awake()
        {
            InitializeSolver();
        }

        private void Start()
        {
            if (enableProceduralStepping)
            {
                // Okamžitý počáteční nález podlahy pro zapuštění nohy
                Vector2 homePos = GetHomePosition();
                Vector2 ground = SampleGroundPosition(homePos);
                currentFoothold = ground;
                stepStartFoothold = ground;
                stepTargetFoothold = ground;
                stepProgress = 1f;
                isCurrentlyStepping = false;
            }

            lastBodyPosition = transform.position;
        }

        /// <summary>
        /// Předpočítá délky segmentů a výchozí úhlové offsety pro běh s nulovou GC režií.
        /// </summary>
        public void InitializeSolver()
        {
            if (solverMode == IKSolverMode2D.TwoBoneAnalytic)
            {
                InitializeTwoBone();
            }
            else if (solverMode == IKSolverMode2D.FABRIKChain)
            {
                InitializeFABRIK();
            }
        }

        private void InitializeTwoBone()
        {
            if (rootBone == null || midBone == null || tipBone == null)
                return;

            length0 = Vector2.Distance(rootBone.position, midBone.position);
            length1 = Vector2.Distance(midBone.position, tipBone.position);
            totalTwoBoneLength = length0 + length1;

            // Výpočet počátečních orientačních odchylek kostí
            Vector2 dir0 = midBone.position - rootBone.position;
            float worldAngle0 = Mathf.Atan2(dir0.y, dir0.x) * Mathf.Rad2Deg;
            rootBoneInitialAngleOffset = Mathf.DeltaAngle(worldAngle0, rootBone.eulerAngles.z);

            Vector2 dir1 = tipBone.position - midBone.position;
            float worldAngle1 = Mathf.Atan2(dir1.y, dir1.x) * Mathf.Rad2Deg;
            midBoneInitialAngleOffset = Mathf.DeltaAngle(worldAngle1, midBone.eulerAngles.z);
        }

        private void InitializeFABRIK()
        {
            if (fabrikBones == null || fabrikBones.Length < 2)
                return;

            int count = fabrikBones.Length;
            fabrikPositions = new Vector2[count];
            fabrikLengths = new float[count - 1];
            fabrikInitialAngleOffsets = new float[count - 1];
            totalFabrikLength = 0f;

            for (int i = 0; i < count - 1; i++)
            {
                if (fabrikBones[i] != null && fabrikBones[i + 1] != null)
                {
                    float len = Vector2.Distance(fabrikBones[i].position, fabrikBones[i + 1].position);
                    fabrikLengths[i] = len;
                    totalFabrikLength += len;

                    Vector2 dir = fabrikBones[i + 1].position - fabrikBones[i].position;
                    float worldAngle = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
                    fabrikInitialAngleOffsets[i] = Mathf.DeltaAngle(worldAngle, fabrikBones[i].eulerAngles.z);
                }
            }

            // Inicializace úhlových omezení na bezpečnou velikost
            if (angularConstraints == null || angularConstraints.Length != count - 1)
            {
                Array.Resize(ref angularConstraints, count - 1);
            }
        }

        // =========================================================================
        // HLAVNÍ SMYČKA ŘEŠENÍ (LATEUPDATE ZERO-GC)
        // =========================================================================

        private void LateUpdate()
        {
            if (weight <= 0.0001f) return;

            // 1. Odhad rychlosti těla pro předstih kroku
            Vector2 currentBodyPos = transform.position;
            float dt = Time.deltaTime;
            if (dt > 0.0001f)
            {
                bodyVelocityEstimate = (currentBodyPos - lastBodyPosition) / dt;
            }
            lastBodyPosition = currentBodyPos;

            // 2. Aktualizace procedurálního kroku
            Vector2 targetPos;
            if (enableProceduralStepping)
            {
                UpdateProceduralStepping(dt);
                targetPos = currentFoothold;
            }
            else
            {
                targetPos = target != null ? (Vector2)target.position : customTargetPosition;
            }

            // 3. Výpočet IK dle zvoleného režimu
            if (solverMode == IKSolverMode2D.TwoBoneAnalytic)
            {
                SolveTwoBoneAnalytic(targetPos);
            }
            else if (solverMode == IKSolverMode2D.FABRIKChain)
            {
                SolveFABRIK(targetPos);
            }

            // 4. Míření koncového nástroje / zbraně
            if (enableAimAtTarget)
            {
                UpdateAiming(targetPos);
            }
        }

        // =========================================================================
        // METODA A: ANALYTICKÉ 2-BONE ŘEŠENÍ (TRIGONOMETRIC LAW OF COSINES)
        // =========================================================================

        private void SolveTwoBoneAnalytic(Vector2 targetWorldPos)
        {
            if (rootBone == null || midBone == null || tipBone == null) return;
            if (length0 <= 0.0001f || length1 <= 0.0001f) return;

            Vector2 rootPos = rootBone.position;
            Vector2 toTarget = targetWorldPos - rootPos;
            float distToTarget = toTarget.magnitude;

            if (distToTarget < 0.0001f)
            {
                toTarget = boneAxis;
                distToTarget = 0.0001f;
            }

            // Omezení maximálního dosahu a měkké utlumení (Soft Clamping)
            float maxReach = totalTwoBoneLength;
            if (enableSoftStretching)
            {
                float softZone = maxReach * 0.12f;
                float softStart = maxReach - softZone;
                if (distToTarget > softStart)
                {
                    float excess = distToTarget - softStart;
                    distToTarget = softStart + softZone * (1f - Mathf.Exp(-excess / softZone));
                }
            }

            if (distToTarget >= maxReach)
            {
                // Cíl je mimo dosah – kosti se plně natáhnou v přímce směrem k cíli
                float targetAngle = Mathf.Atan2(toTarget.y, toTarget.x) * Mathf.Rad2Deg;

                Quaternion rootRot = Quaternion.Euler(0f, 0f, targetAngle + rootBoneInitialAngleOffset);
                Quaternion midRot = Quaternion.Euler(0f, 0f, targetAngle + midBoneInitialAngleOffset);

                rootBone.rotation = Quaternion.Slerp(rootBone.rotation, rootRot, weight);
                midBone.rotation = Quaternion.Slerp(midBone.rotation, midRot, weight);
                return;
            }

            // Kosinová věta pro vnitřní úhel v kořeni (úhel A) a v lokti/koleni (úhel B)
            // c^2 = a^2 + b^2 - 2ab*cos(C)
            float a = length0;
            float b = length1;
            float c = distToTarget;

            // Úhel u Root bone
            float cosAngleRoot = (a * a + c * c - b * b) / (2f * a * c);
            cosAngleRoot = Mathf.Clamp(cosAngleRoot, -1f, 1f);
            float angleRootRad = Mathf.Acos(cosAngleRoot);

            // Úhel u Mid bone
            float cosAngleMid = (a * a + b * b - c * c) / (2f * a * b);
            cosAngleMid = Mathf.Clamp(cosAngleMid, -1f, 1f);
            float angleMidRad = Mathf.Acos(cosAngleMid);

            float baseTargetAngleRad = Mathf.Atan2(toTarget.y, toTarget.x);
            float sign = flipBendDirection ? -1f : 1f;

            // Finální světový úhel pro Root kost
            float finalRootAngleRad = baseTargetAngleRad + (sign * angleRootRad);
            float finalRootAngleDeg = finalRootAngleRad * Mathf.Rad2Deg;

            // Výpočet pozice prostředního kloubu
            Vector2 midCalculatedPos = rootPos + new Vector2(Mathf.Cos(finalRootAngleRad), Mathf.Sin(finalRootAngleRad)) * a;

            // Finální světový úhel pro Mid kost směrem k cíli
            Vector2 midToTarget = targetWorldPos - midCalculatedPos;
            float finalMidAngleDeg = Mathf.Atan2(midToTarget.y, midToTarget.x) * Mathf.Rad2Deg;

            // Aplikace rotací se započtením počátečních offsetů a váhy
            Quaternion desiredRootRot = Quaternion.Euler(0f, 0f, finalRootAngleDeg + rootBoneInitialAngleOffset);
            Quaternion desiredMidRot = Quaternion.Euler(0f, 0f, finalMidAngleDeg + midBoneInitialAngleOffset);

            rootBone.rotation = Quaternion.Slerp(rootBone.rotation, desiredRootRot, weight);
            midBone.rotation = Quaternion.Slerp(midBone.rotation, desiredMidRot, weight);
        }

        // =========================================================================
        // METODA B: ITERAČNÍ FABRIK ŘEŠENÍ PRO N-KOSTÍ (FORWARD AND BACKWARD REACHING)
        // =========================================================================

        private void SolveFABRIK(Vector2 targetWorldPos)
        {
            int count = fabrikBones.Length;
            if (count < 2 || fabrikPositions == null || fabrikPositions.Length != count) return;

            // 1. Zkopírování aktuálních pozic do pre-alokovaného pole
            for (int i = 0; i < count; i++)
            {
                if (fabrikBones[i] == null) return;
                fabrikPositions[i] = fabrikBones[i].position;
            }

            Vector2 rootPos = fabrikPositions[0];
            float distToTarget = Vector2.Distance(rootPos, targetWorldPos);

            // 2. Případ A: Cíl je mimo dosah celého řetězce
            if (distToTarget >= totalFabrikLength)
            {
                Vector2 dir = (targetWorldPos - rootPos).normalized;
                for (int i = 0; i < count - 1; i++)
                {
                    fabrikPositions[i + 1] = fabrikPositions[i] + dir * fabrikLengths[i];
                }
            }
            else
            {
                // Případ B: Cíl je v dosahu – iterační fáze FABRIK
                float sqrTolerance = fabrikTolerance * fabrikTolerance;
                int iteration = 0;

                while (iteration < maxFabrikIterations)
                {
                    float distSq = (fabrikPositions[count - 1] - targetWorldPos).sqrMagnitude;
                    if (distSq <= sqrTolerance)
                        break;

                    // --- Zpětný průchod (Backward Passing: od koncového bodu k počátku) ---
                    fabrikPositions[count - 1] = targetWorldPos;
                    for (int i = count - 2; i >= 0; i--)
                    {
                        Vector2 segDir = (fabrikPositions[i] - fabrikPositions[i + 1]).normalized;
                        if (segDir.sqrMagnitude < 0.0001f) segDir = Vector2.up;
                        fabrikPositions[i] = fabrikPositions[i + 1] + segDir * fabrikLengths[i];
                    }

                    // --- Dopředný průchod (Forward Passing: od počátku ke koncovému bodu) ---
                    fabrikPositions[0] = rootPos;
                    for (int i = 0; i < count - 1; i++)
                    {
                        Vector2 segDir = (fabrikPositions[i + 1] - fabrikPositions[i]).normalized;
                        if (segDir.sqrMagnitude < 0.0001f) segDir = Vector2.right;

                        // Aplikace úhlového omezení kloubu (Joint Angular Constraints)
                        if (i < angularConstraints.Length && angularConstraints[i].EnableConstraint)
                        {
                            Vector2 parentDir = i > 0
                                ? (fabrikPositions[i] - fabrikPositions[i - 1]).normalized
                                : (Vector2)fabrikBones[0].right;

                            segDir = ApplyAngleConstraint(parentDir, segDir, angularConstraints[i].MinAngle, angularConstraints[i].MaxAngle);
                        }

                        fabrikPositions[i + 1] = fabrikPositions[i] + segDir * fabrikLengths[i];
                    }

                    iteration++;
                }
            }

            // 3. Promítnutí vypočtených pozic do rotací transformů kostí
            for (int i = 0; i < count - 1; i++)
            {
                Vector2 segDir = fabrikPositions[i + 1] - (Vector2)fabrikBones[i].position;
                float angleDeg = Mathf.Atan2(segDir.y, segDir.x) * Mathf.Rad2Deg;
                Quaternion desiredRot = Quaternion.Euler(0f, 0f, angleDeg + fabrikInitialAngleOffsets[i]);
                fabrikBones[i].rotation = Quaternion.Slerp(fabrikBones[i].rotation, desiredRot, weight);
            }
        }

        private static Vector2 ApplyAngleConstraint(Vector2 parentDir, Vector2 currentDir, float minDeg, float maxDeg)
        {
            float currentAngle = Vector2.SignedAngle(parentDir, currentDir);
            float clampedAngle = Mathf.Clamp(currentAngle, minDeg, maxDeg);
            float rad = (Mathf.Atan2(parentDir.y, parentDir.x) + clampedAngle * Mathf.Deg2Rad);
            return new Vector2(Mathf.Cos(rad), Mathf.Sin(rad));
        }

        // =========================================================================
        // METODA C: PROCEDURÁLNÍ KROKOVÁNÍ (GAIT COORDINATION & STEPPING)
        // =========================================================================

        private void UpdateProceduralStepping(float deltaTime)
        {
            Vector2 homeGroundPos = SampleGroundPosition(GetHomePosition());

            if (isCurrentlyStepping)
            {
                // Průběh krokem
                stepProgress += deltaTime * stepSpeed;
                float t = Mathf.Clamp01(stepProgress);

                // Lineární horizontální interpolace
                Vector2 horizPos = Vector2.Lerp(stepStartFoothold, stepTargetFoothold, t);

                // Parabolický vertikální oblouk zvednutí nohy: 4 * h * t * (1 - t)
                float arcHeight = 4f * stepHeight * t * (1f - t);
                currentFoothold = new Vector2(horizPos.x, horizPos.y + arcHeight);

                if (t >= 1f)
                {
                    currentFoothold = stepTargetFoothold;
                    isCurrentlyStepping = false;
                }
            }
            else
            {
                // Kontrola potřeby nového kroku
                float distFromHome = Vector2.Distance(currentFoothold, homeGroundPos);

                if (distFromHome >= stepDistanceThreshold)
                {
                    // Kontrola protilehlých nohou (nesmí krokovat současně)
                    bool canStep = true;
                    if (linkedOppositeLegs != null)
                    {
                        for (int i = 0; i < linkedOppositeLegs.Length; i++)
                        {
                            var otherLeg = linkedOppositeLegs[i];
                            if (otherLeg != null && otherLeg.IsStepping)
                            {
                                canStep = false;
                                break;
                            }
                        }
                    }

                    if (canStep)
                    {
                        // Zahájení kroku s predikcí rychlosti
                        stepStartFoothold = currentFoothold;
                        Vector2 overshoot = bodyVelocityEstimate * stepOvershootMultiplier;
                        Vector2 predictedTarget = homeGroundPos + overshoot;
                        stepTargetFoothold = SampleGroundPosition(predictedTarget);

                        stepProgress = 0f;
                        isCurrentlyStepping = true;
                    }
                }
            }
        }

        private Vector2 GetHomePosition()
        {
            if (homePositionTransform != null)
                return homePositionTransform.position;

            return (Vector2)transform.position + homePositionOffset;
        }

        private Vector2 SampleGroundPosition(Vector2 origin)
        {
            Vector2 rayOrigin = origin + Vector2.up * 1.5f;
            RaycastHit2D hit = Physics2D.Raycast(rayOrigin, Vector2.down, 4f, groundLayerMask);
            if (hit.collider != null)
            {
                return hit.point;
            }
            return origin;
        }

        // =========================================================================
        // METODA D: MÍŘENÍ NÁSTROJŮ/ZBRANÍ (AIM TIP AT TARGET)
        // =========================================================================

        private void UpdateAiming(Vector2 targetWorldPos)
        {
            Transform weaponTrans = aimTransform != null ? aimTransform : tipBone;
            if (weaponTrans == null) return;

            Vector2 toTarget = targetWorldPos - (Vector2)weaponTrans.position;
            float angleDeg = Mathf.Atan2(toTarget.y, toTarget.x) * Mathf.Rad2Deg + aimAngleOffset;

            weaponTrans.rotation = Quaternion.Euler(0f, 0f, angleDeg);

            // Automatické převrácení osy Y (flip) při míření doleva
            if (autoFlipAimY)
            {
                Vector3 scale = weaponTrans.localScale;
                bool isAimingLeft = Mathf.Abs(Mathf.DeltaAngle(0f, angleDeg)) > 90f;
                scale.y = isAimingLeft ? -Mathf.Abs(scale.y) : Mathf.Abs(scale.y);
                weaponTrans.localScale = scale;
            }
        }

        // =========================================================================
        // 6. SCENE GIZMOS & VIZUÁLNÍ LADĚNÍ (SCENE VIEW DEBUGGING)
        // =========================================================================

#if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            if (!enabled) return;

            // Kreslení cíle
            Vector2 targetPos = target != null ? (Vector2)target.position : customTargetPosition;
            if (enableProceduralStepping && Application.isPlaying)
            {
                targetPos = currentFoothold;
            }

            Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.7f);
            Gizmos.DrawWireSphere(targetPos, 0.12f);

            // Kreslení kostí pro 2-Bone
            if (solverMode == IKSolverMode2D.TwoBoneAnalytic && rootBone != null && midBone != null && tipBone != null)
            {
                Gizmos.color = Color.green;
                Gizmos.DrawLine(rootBone.position, midBone.position);
                Gizmos.color = Color.yellow;
                Gizmos.DrawLine(midBone.position, tipBone.position);

                // Max reach kružnice
                float maxReach = (length0 > 0f ? length0 : Vector2.Distance(rootBone.position, midBone.position)) +
                                 (length1 > 0f ? length1 : Vector2.Distance(midBone.position, tipBone.position));
                Gizmos.color = new Color(0.3f, 1f, 0.3f, 0.15f);
                Gizmos.DrawWireSphere(rootBone.position, maxReach);
            }
            // Kreslení kostí pro FABRIK
            else if (solverMode == IKSolverMode2D.FABRIKChain && fabrikBones != null && fabrikBones.Length > 1)
            {
                Gizmos.color = Color.cyan;
                for (int i = 0; i < fabrikBones.Length - 1; i++)
                {
                    if (fabrikBones[i] != null && fabrikBones[i + 1] != null)
                    {
                        Gizmos.DrawLine(fabrikBones[i].position, fabrikBones[i + 1].position);
                        Gizmos.DrawWireSphere(fabrikBones[i].position, 0.05f);
                    }
                }
            }

            // Kreslení krokovacího modulu
            if (enableProceduralStepping)
            {
                Vector2 home = GetHomePosition();
                Gizmos.color = Color.magenta;
                Gizmos.DrawWireSphere(home, 0.08f);
                Gizmos.DrawWireSphere(home, stepDistanceThreshold);

                if (Application.isPlaying && isCurrentlyStepping)
                {
                    Gizmos.color = Color.red;
                    Gizmos.DrawLine(stepStartFoothold, stepTargetFoothold);
                    Gizmos.DrawWireSphere(stepTargetFoothold, 0.06f);
                }
            }
        }
#endif
    }
}
