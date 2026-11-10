using System;
using UnityEngine;
using GameSystems.Events;
using GameSystems.Persistence;

namespace GameSystems.PhysicsAndCombat
{
    // =========================================================================
    // 1. ALLOCATION-FREE INDUSTRIAL PID CONTROLLER STRUCTS
    // =========================================================================

    /// <summary>
    /// High-performance, zero-allocation scalar PID controller featuring:
    /// - Derivative-on-measurement (eliminates derivative kick on setpoint step changes)
    /// - Anti-windup integral clamping
    /// - First-order low-pass derivative filtering
    /// </summary>
    [Serializable]
    public struct FloatPIDController
    {
        [Header("Gains")]
        [Tooltip("Proportional gain")]
        public float Kp;
        [Tooltip("Integral gain")]
        public float Ki;
        [Tooltip("Derivative gain")]
        public float Kd;

        [Header("Limits & Clamping")]
        [Tooltip("Anti-windup limit for integral term")]
        public float IntegralLimit;
        [Tooltip("Minimum output saturation limit")]
        public float OutputMin;
        [Tooltip("Maximum output saturation limit")]
        public float OutputMax;

        [Header("Filtering & Stability")]
        [Tooltip("Low-pass filter alpha for derivative smoothing (0.0 to 1.0, 1 = unfiltered)")]
        [Range(0.01f, 1f)] public float DerivativeFilterAlpha;
        [Tooltip("Derive on measurement instead of error to avoid setpoint kick")]
        public bool DerivativeOnMeasurement;

        // Internal persistent state
        private float integralAccumulator;
        private float lastMeasurement;
        private float lastError;
        private float filteredDerivative;
        private bool isFirstTick;

        public FloatPIDController(float kp, float ki, float kd, float outMin = -1f, float outMax = 1f, float integralLimit = 1f, float filterAlpha = 0.8f, bool derivOnMeasurement = true)
        {
            Kp = kp;
            Ki = ki;
            Kd = kd;
            OutputMin = outMin;
            OutputMax = outMax;
            IntegralLimit = Mathf.Abs(integralLimit);
            DerivativeFilterAlpha = Mathf.Clamp01(filterAlpha);
            DerivativeOnMeasurement = derivOnMeasurement;

            integralAccumulator = 0f;
            lastMeasurement = 0f;
            lastError = 0f;
            filteredDerivative = 0f;
            isFirstTick = true;
        }

        public void Reset()
        {
            integralAccumulator = 0f;
            lastMeasurement = 0f;
            lastError = 0f;
            filteredDerivative = 0f;
            isFirstTick = true;
        }

        public float Update(float setpoint, float currentMeasurement, float dt)
        {
            if (dt <= 0.000001f) return 0f;

            float error = setpoint - currentMeasurement;

            // 1. Proportional Term
            float pTerm = Kp * error;

            // 2. Integral Term with anti-windup clamping
            integralAccumulator += error * dt;
            if (IntegralLimit > 0f)
            {
                integralAccumulator = Mathf.Clamp(integralAccumulator, -IntegralLimit, IntegralLimit);
            }
            float iTerm = Ki * integralAccumulator;

            // 3. Derivative Term
            float rawDerivative;
            if (isFirstTick)
            {
                rawDerivative = 0f;
                lastMeasurement = currentMeasurement;
                lastError = error;
                isFirstTick = false;
            }
            else if (DerivativeOnMeasurement)
            {
                // d(error)/dt = d(setpoint - measurement)/dt = -d(measurement)/dt (when setpoint is constant)
                rawDerivative = -(currentMeasurement - lastMeasurement) / dt;
            }
            else
            {
                rawDerivative = (error - lastError) / dt;
            }

            // Low-pass filter derivative
            filteredDerivative = Mathf.Lerp(filteredDerivative, rawDerivative, DerivativeFilterAlpha);
            float dTerm = Kd * filteredDerivative;

            lastMeasurement = currentMeasurement;
            lastError = error;

            // 4. Sum & Clamp Output
            float output = pTerm + iTerm + dTerm;
            if (OutputMin < OutputMax)
            {
                output = Mathf.Clamp(output, OutputMin, OutputMax);
            }

            return output;
        }
    }

    /// <summary>
    /// Reusable 2D vector PID controller for planar motion (XY or XZ).
    /// </summary>
    [Serializable]
    public struct Vector2PIDController
    {
        public FloatPIDController ControllerX;
        public FloatPIDController ControllerY;
        public float MaxOutputMagnitude;

        public Vector2PIDController(float kp, float ki, float kd, float maxMag = 100f)
        {
            ControllerX = new FloatPIDController(kp, ki, kd, -maxMag, maxMag);
            ControllerY = new FloatPIDController(kp, ki, kd, -maxMag, maxMag);
            MaxOutputMagnitude = maxMag;
        }

        public void Reset()
        {
            ControllerX.Reset();
            ControllerY.Reset();
        }

        public Vector2 Update(Vector2 setpoint, Vector2 current, float dt)
        {
            float outX = ControllerX.Update(setpoint.x, current.x, dt);
            float outY = ControllerY.Update(setpoint.y, current.y, dt);
            Vector2 result = new Vector2(outX, outY);

            if (MaxOutputMagnitude > 0f && result.sqrMagnitude > MaxOutputMagnitude * MaxOutputMagnitude)
            {
                result = result.normalized * MaxOutputMagnitude;
            }
            return result;
        }
    }

    /// <summary>
    /// Reusable 3D vector PID controller for full 6-DOF translation/force regulation.
    /// </summary>
    [Serializable]
    public struct Vector3PIDController
    {
        public FloatPIDController ControllerX;
        public FloatPIDController ControllerY;
        public FloatPIDController ControllerZ;
        public float MaxOutputMagnitude;

        public Vector3PIDController(float kp, float ki, float kd, float maxMag = 100f)
        {
            ControllerX = new FloatPIDController(kp, ki, kd, -maxMag, maxMag);
            ControllerY = new FloatPIDController(kp, ki, kd, -maxMag, maxMag);
            ControllerZ = new FloatPIDController(kp, ki, kd, -maxMag, maxMag);
            MaxOutputMagnitude = maxMag;
        }

        public void Reset()
        {
            ControllerX.Reset();
            ControllerY.Reset();
            ControllerZ.Reset();
        }

        public Vector3 Update(Vector3 setpoint, Vector3 current, float dt)
        {
            float outX = ControllerX.Update(setpoint.x, current.x, dt);
            float outY = ControllerY.Update(setpoint.y, current.y, dt);
            float outZ = ControllerZ.Update(setpoint.z, current.z, dt);
            Vector3 result = new Vector3(outX, outY, outZ);

            if (MaxOutputMagnitude > 0f && result.sqrMagnitude > MaxOutputMagnitude * MaxOutputMagnitude)
            {
                result = result.normalized * MaxOutputMagnitude;
            }
            return result;
        }
    }

    /// <summary>
    /// Special PID controller for angles (-180 to +180 deg) solving modular wrap-around cleanly.
    /// </summary>
    [Serializable]
    public struct AnglePIDController
    {
        public float Kp;
        public float Ki;
        public float Kd;
        public float OutputMin;
        public float OutputMax;
        public float IntegralLimit;

        private float integralAccumulator;
        private float lastMeasurement;
        private bool isFirstTick;

        public AnglePIDController(float kp, float ki, float kd, float outMin = -1f, float outMax = 1f)
        {
            Kp = kp;
            Ki = ki;
            Kd = kd;
            OutputMin = outMin;
            OutputMax = outMax;
            IntegralLimit = 45f;
            integralAccumulator = 0f;
            lastMeasurement = 0f;
            isFirstTick = true;
        }

        public void Reset()
        {
            integralAccumulator = 0f;
            lastMeasurement = 0f;
            isFirstTick = true;
        }

        public float Update(float targetAngleDeg, float currentAngleDeg, float dt)
        {
            if (dt <= 0.000001f) return 0f;

            float error = Mathf.DeltaAngle(currentAngleDeg, targetAngleDeg);
            float pTerm = Kp * error;

            integralAccumulator += error * dt;
            if (IntegralLimit > 0f)
                integralAccumulator = Mathf.Clamp(integralAccumulator, -IntegralLimit, IntegralLimit);
            float iTerm = Ki * integralAccumulator;

            float rawDerivative;
            if (isFirstTick)
            {
                rawDerivative = 0f;
                lastMeasurement = currentAngleDeg;
                isFirstTick = false;
            }
            else
            {
                float angleDelta = Mathf.DeltaAngle(lastMeasurement, currentAngleDeg);
                rawDerivative = -angleDelta / dt;
            }
            lastMeasurement = currentAngleDeg;
            float dTerm = Kd * rawDerivative;

            float output = pTerm + iTerm + dTerm;
            if (OutputMin < OutputMax)
                output = Mathf.Clamp(output, OutputMin, OutputMax);

            return output;
        }
    }

    // =========================================================================
    // 2. ZERO-GC EVENT BUS STRUCTS
    // =========================================================================

    public enum AutopilotMode
    {
        Off,
        AltitudeOrDepthHold,
        HeadingHold,
        VelocityMatch,
        PositionHoverHold,
        WaypointNavigation
    }

    public readonly struct AutopilotStateChangedEvent : IGameEvent
    {
        public readonly int ControllerInstanceId;
        public readonly AutopilotMode PreviousMode;
        public readonly AutopilotMode NewMode;
        public readonly Vector3 TargetSetpoint;

        public AutopilotStateChangedEvent(int instanceId, AutopilotMode prev, AutopilotMode next, Vector3 setpoint)
        {
            ControllerInstanceId = instanceId;
            PreviousMode = prev;
            NewMode = next;
            TargetSetpoint = setpoint;
        }
    }

    // =========================================================================
    // 3. MASTER AUTOPILOT CONTROLLER MONOBEHAVIOUR (2D & 3D COMPATIBLE)
    // =========================================================================

    /// <summary>
    /// Autonomous flight and vessel stability autopilot controller.
    /// Regulates altitude/depth, heading/yaw, translational velocity, and spatial waypoints.
    /// Supports both 2D (Rigidbody2D) and 3D (Rigidbody) vessels seamlessly with zero GC allocations.
    /// </summary>
    [DisallowMultipleComponent]
    public class PIDControllerAndAutopilotSuite : MonoBehaviour, ISaveableSystem
    {
        [Header("Autopilot Operational Mode")]
        [SerializeField] private AutopilotMode currentMode = AutopilotMode.PositionHoverHold;
        [SerializeField] private bool operateIn2DMode = false;

        [Header("Setpoints")]
        [SerializeField] private Vector3 targetPosition;
        [SerializeField] private Vector3 targetVelocity;
        [SerializeField] private float targetAltitude = 10f;
        [SerializeField] private float targetHeadingDeg = 0f;

        [Header("Altitude / Depth PID")]
        [SerializeField] private FloatPIDController altitudePID = new FloatPIDController(4.0f, 0.5f, 2.5f, -50f, 50f, 10f);

        [Header("Heading / Yaw PID")]
        [SerializeField] private AnglePIDController headingPID = new AnglePIDController(2.5f, 0.2f, 1.2f, -20f, 20f);

        [Header("Translational 3D Velocity PID")]
        [SerializeField] private Vector3PIDController translationPID3D = new Vector3PIDController(3.0f, 0.2f, 1.5f, 100f);

        [Header("Translational 2D Velocity PID")]
        [SerializeField] private Vector2PIDController translationPID2D = new Vector2PIDController(3.0f, 0.2f, 1.5f, 100f);

        [Header("Physics Actuators")]
        [SerializeField] private Rigidbody body3D;
        [SerializeField] private Rigidbody2D body2D;
        [SerializeField] private float maxThrustNewtons = 250f;
        [SerializeField] private float maxTorque = 50f;

        // Telemetry values for visualization
        private Vector3 computedThrustVector;
        private float computedTorque;

        public string SaveKey => $"Autopilot_{gameObject.name}_{GetInstanceID()}";
        public AutopilotMode CurrentMode => currentMode;

        private void Awake()
        {
            if (body3D == null) body3D = GetComponent<Rigidbody>();
            if (body2D == null) body2D = GetComponent<Rigidbody2D>();

            if (body2D != null && body3D == null)
            {
                operateIn2DMode = true;
            }

            targetPosition = transform.position;
            targetHeadingDeg = operateIn2DMode ? transform.eulerAngles.z : transform.eulerAngles.y;
            targetAltitude = transform.position.y;
        }

        public void SetMode(AutopilotMode newMode)
        {
            if (currentMode != newMode)
            {
                AutopilotMode prev = currentMode;
                currentMode = newMode;
                ResetAllControllers();
                EventBus<AutopilotStateChangedEvent>.Raise(new AutopilotStateChangedEvent(
                    GetInstanceID(), prev, newMode, targetPosition));
            }
        }

        public void SetTargetPosition(Vector3 pos) => targetPosition = pos;
        public void SetTargetAltitude(float altitude) => targetAltitude = altitude;
        public void SetTargetHeading(float headingDeg) => targetHeadingDeg = headingDeg;
        public void SetTargetVelocity(Vector3 vel) => targetVelocity = vel;

        private void ResetAllControllers()
        {
            altitudePID.Reset();
            headingPID.Reset();
            translationPID3D.Reset();
            translationPID2D.Reset();
        }

        private void FixedUpdate()
        {
            if (currentMode == AutopilotMode.Off) return;

            float dt = Time.fixedDeltaTime;
            Vector3 currentPos = transform.position;
            Vector3 currentVel = operateIn2DMode ? (body2D != null ? (Vector3)body2D.linearVelocity : Vector3.zero) :
                                                  (body3D != null ? body3D.linearVelocity : Vector3.zero);
            float currentHeading = operateIn2DMode ? transform.eulerAngles.z : transform.eulerAngles.y;

            computedThrustVector = Vector3.zero;
            computedTorque = 0f;

            switch (currentMode)
            {
                case AutopilotMode.AltitudeOrDepthHold:
                {
                    float thrustY = altitudePID.Update(targetAltitude, currentPos.y, dt);
                    computedThrustVector = new Vector3(0f, thrustY, 0f);
                    break;
                }

                case AutopilotMode.HeadingHold:
                {
                    computedTorque = headingPID.Update(targetHeadingDeg, currentHeading, dt);
                    break;
                }

                case AutopilotMode.VelocityMatch:
                {
                    if (operateIn2DMode)
                    {
                        Vector2 outThrust2D = translationPID2D.Update((Vector2)targetVelocity, (Vector2)currentVel, dt);
                        computedThrustVector = outThrust2D;
                    }
                    else
                    {
                        computedThrustVector = translationPID3D.Update(targetVelocity, currentVel, dt);
                    }
                    break;
                }

                case AutopilotMode.PositionHoverHold:
                case AutopilotMode.WaypointNavigation:
                {
                    Vector3 posError = targetPosition - currentPos;
                    Vector3 desiredVel = Vector3.ClampMagnitude(posError * 2.5f, 15f);

                    if (operateIn2DMode)
                    {
                        Vector2 outThrust2D = translationPID2D.Update((Vector2)desiredVel, (Vector2)currentVel, dt);
                        computedThrustVector = outThrust2D;

                        // Align heading with motion if navigating
                        if (currentMode == AutopilotMode.WaypointNavigation && posError.sqrMagnitude > 0.5f)
                        {
                            float desiredAngle = Mathf.Atan2(posError.y, posError.x) * Mathf.Rad2Deg;
                            computedTorque = headingPID.Update(desiredAngle, currentHeading, dt);
                        }
                    }
                    else
                    {
                        computedThrustVector = translationPID3D.Update(desiredVel, currentVel, dt);

                        // Align heading in 3D (Yaw)
                        if (currentMode == AutopilotMode.WaypointNavigation && (posError.x * posError.x + posError.z * posError.z) > 0.5f)
                        {
                            float desiredYaw = Mathf.Atan2(posError.x, posError.z) * Mathf.Rad2Deg;
                            computedTorque = headingPID.Update(desiredYaw, currentHeading, dt);
                        }
                    }
                    break;
                }
            }

            // Apply calculated forces to physics body
            ApplyActuationForces();
        }

        private void ApplyActuationForces()
        {
            Vector3 clampedThrust = Vector3.ClampMagnitude(computedThrustVector, maxThrustNewtons);
            float clampedTorque = Mathf.Clamp(computedTorque, -maxTorque, maxTorque);

            if (operateIn2DMode && body2D != null)
            {
                body2D.AddForce(new Vector2(clampedThrust.x, clampedThrust.y), ForceMode2D.Force);
                body2D.AddTorque(clampedTorque, ForceMode2D.Force);
            }
            else if (body3D != null)
            {
                body3D.AddForce(clampedThrust, ForceMode.Force);
                body3D.AddTorque(new Vector3(0f, clampedTorque, 0f), ForceMode.Force);
            }
        }

        // =========================================================================
        // SAVE & RESTORE (ISaveableSystem)
        // =========================================================================

        [Serializable]
        private class AutopilotSaveData
        {
            public int Mode;
            public Vector3 TargetPos;
            public float TargetAlt;
            public float TargetHead;
        }

        public object CaptureState()
        {
            return new AutopilotSaveData
            {
                Mode = (int)currentMode,
                TargetPos = targetPosition,
                TargetAlt = targetAltitude,
                TargetHead = targetHeadingDeg
            };
        }

        public void RestoreState(string stateJson)
        {
            if (string.IsNullOrEmpty(stateJson)) return;
            var data = JsonUtility.FromJson<AutopilotSaveData>(stateJson);
            if (data == null) return;

            currentMode = (AutopilotMode)data.Mode;
            targetPosition = data.TargetPos;
            targetAltitude = data.TargetAlt;
            targetHeadingDeg = data.TargetHead;
            ResetAllControllers();
        }

        // =========================================================================
        // SCENE GIZMOS & VISUAL TELEMETRY
        // =========================================================================

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.green;
            Gizmos.DrawWireSphere(targetPosition, 0.5f);
            Gizmos.DrawLine(transform.position, targetPosition);

            // Draw calculated thrust vector
            Gizmos.color = Color.yellow;
            Gizmos.DrawRay(transform.position, computedThrustVector.normalized * 2f);

            // Draw altitude target plane
            if (currentMode == AutopilotMode.AltitudeOrDepthHold)
            {
                Gizmos.color = new Color(0f, 1f, 1f, 0.3f);
                Vector3 planeCenter = new Vector3(transform.position.x, targetAltitude, transform.position.z);
                Gizmos.DrawWireCube(planeCenter, new Vector3(10f, 0.1f, 10f));
            }
        }
    }
}
