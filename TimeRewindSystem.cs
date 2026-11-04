using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Time-rewind system (Braid / Prince of Persia style).
/// TimeRewindTracker records snapshots of any transform (+ rigidbody velocities) into a ring
/// buffer. TimeRewindController (one in the scene) drives a global rewind: press-and-hold to
/// scrub every tracked object backwards in time, release to resume with restored velocities.
/// Works in 2D and 3D. Uses: puzzle mechanics, death rewinds, boss replays,
/// 'echo' racing ghosts, undo in physics puzzlers.
/// </summary>
public class TimeRewindTracker : MonoBehaviour
{
    struct Snap
    {
        public Vector3 pos, scale;
        public Quaternion rot;
        public Vector3 vel3, angVel3;
        public Vector2 vel2, angVel2;
        public bool active;
    }

    [Tooltip("Seconds of history to keep.")]
    public float bufferSeconds = 5f;
    [Tooltip("Snapshots per second while recording.")]
    public float recordRate = 30f;
    public bool recordScale = true;

    readonly Queue<Snap> history = new Queue<Snap>();
    Rigidbody rb3;
    Rigidbody2D rb2d;
    float recordTimer;

    public bool IsRewinding { get; private set; }
    public int SnapshotCount => history.Count;

    void Awake()
    {
        rb3 = GetComponent<Rigidbody>();
        rb2d = GetComponent<Rigidbody2D>();
    }

    void Update()
    {
        if (!IsRewinding)
        {
            recordTimer += Time.deltaTime;
            float interval = 1f / Mathf.Max(1f, recordRate);
            while (recordTimer >= interval)
            {
                recordTimer -= interval;
                Record();
            }
        }
    }

    void Record()
    {
        var s = new Snap
        {
            pos = transform.position,
            rot = transform.rotation,
            scale = transform.localScale,
            active = gameObject.activeInHierarchy
        };
        if (rb3 != null) { s.vel3 = rb3.linearVelocity; s.angVel3 = rb3.angularVelocity; }
        if (rb2d != null) { s.vel2 = rb2d.linearVelocity; s.angVel2 = rb2d.angularVelocity; }
        history.Enqueue(s);

        while (history.Count > Mathf.CeilToInt(bufferSeconds * recordRate))
            history.Dequeue();
    }

    /// <summary>Step one snapshot back in time. Returns false when history is empty.</summary>
    public bool RewindStep()
    {
        IsRewinding = true;
        SetPhysicsKinematic(true);

        if (history.Count == 0) return false;

        var s = history.Dequeue();
        transform.SetPositionAndRotation(s.pos, s.rot);
        if (recordScale) transform.localScale = s.scale;
        if (s.active != gameObject.activeSelf) gameObject.SetActive(s.active);
        pendingVel3 = s.vel3; pendingAngVel3 = s.angVel3;
        pendingVel2 = s.vel2; pendingAngVel2 = s.angVel2;
        return true;
    }

    Vector3 pendingVel3, pendingAngVel3;
    Vector2 pendingVel2, pendingAngVel2;

    /// <summary>Stop rewinding; restores recorded velocities so physics resumes believably.</summary>
    public void StopRewind()
    {
        if (!IsRewinding) return;
        IsRewinding = false;
        SetPhysicsKinematic(false);
        if (rb3 != null) { rb3.linearVelocity = pendingVel3; rb3.angularVelocity = pendingAngVel3; }
        if (rb2d != null) { rb2d.linearVelocity = pendingVel2; rb2d.angularVelocity = pendingAngVel2; }
    }

    void SetPhysicsKinematic(bool k)
    {
        if (rb3 != null) rb3.isKinematic = k;
        if (rb2d != null) rb2d.isKinematic = k;
    }

    /// <summary>Duplicate this object's recorded history onto another tracker (ghost replays).</summary>
    public void CopyHistoryTo(TimeRewindTracker other)
    {
        other.history.Clear();
        foreach (var s in history) other.history.Enqueue(s);
    }

    /// <summary>Clear stored history (call on respawn/teleport to avoid weird rewinds).</summary>
    public void ClearHistory() => history.Clear();
}

/// <summary>
/// Global rewind driver. Holds the rewind key (or call BeginRewind/EndRewind from code),
/// slows game time while scrubbing, and steps all trackers in the scene.
/// </summary>
public class TimeRewindController : MonoBehaviour
{
    [Header("Input")]
    public KeyCode rewindKey = KeyCode.R;
    public bool allowHoldKey = true;

    [Header("Playback")]
    [Tooltip("Snapshots restored per second while holding rewind (30 = 1x reverse).")]
    public float rewindSpeed = 30f;
    [Tooltip("Timescale while rewinding (audio/UI keep running at this speed).")]
    public float rewindTimescale = 1f;

    [Header("Feedback (optional)")]
    public AudioSource rewindLoopSound;
    public bool debugUI = true;

    public bool IsRewinding { get; private set; }
    public static TimeRewindController Instance { get; private set; }

    readonly List<TimeRewindTracker> trackers = new List<TimeRewindTracker>();
    float stepAccum;

    void Awake() => Instance = this;

    void Update()
    {
        bool wants = allowHoldKey && Input.GetKey(rewindKey);

        if (wants && !IsRewinding) BeginRewind();
        else if (!wants && IsRewinding) EndRewind();

        if (IsRewinding)
        {
            stepAccum += rewindSpeed * Time.unscaledDeltaTime;
            int steps = Mathf.FloorToInt(stepAccum);
            stepAccum -= steps;
            for (int i = 0; i < steps; i++)
                foreach (var t in trackers) t.RewindStep();
        }
    }

    public void BeginRewind()
    {
        if (IsRewinding) return;
        IsRewinding = true;
        stepAccum = 0f;
        trackers.Clear();
        trackers.AddRange(FindObjectsOfType<TimeRewindTracker>(true));
        Time.timeScale = rewindTimescale;
        if (rewindLoopSound != null) rewindLoopSound.Play();
    }

    public void EndRewind()
    {
        if (!IsRewinding) return;
        IsRewinding = false;
        foreach (var t in trackers) t.StopRewind();
        Time.timeScale = 1f;
        if (rewindLoopSound != null) rewindLoopSound.Stop();
    }

    void OnGUI()
    {
        if (!debugUI) return;
        if (IsRewinding) GUILayout.Label("<< REWINDING >>");
    }
}
