using System;
using UnityEngine;

/// <summary>
/// DSP-time rhythm engine: keeps everything locked to actual audio time (not frame time),
/// emits beat/measure events, and provides hit-judgement windows for rhythm games plus
/// beat-sync helpers for non-rhythm games (pulses, doors that open on beat, music-reactive VFX).
/// 2D or 3D - it is dimension-agnostic. Genres: rhythm games, beat-em-ups, music puzzles,
/// anything with a soundtrack you want the world to react to.
///
/// Usage: put on a GameObject with an AudioSource playing the song, set BPM & first-beat time.
/// </summary>
public class RhythmConductor : MonoBehaviour
{
    [Header("Audio")]
    public AudioSource musicSource;
    public float songPosition;      // seconds into the song
    public float songBpmPosition;   // beats into the song (can be fractional)

    [Header("Timing")]
    public float bpm = 120f;
    [Tooltip("Seconds of silence (or offset) before the first beat in the track.")]
    public float firstBeatOffset = 0f;
    [Tooltip("Live pitch/dj-speed changes change playback rate; multiply time advance by this.")]
    public bool compensatePitch = true;

    [Header("Judgement Windows (rhythm games, in seconds)")]
    public float perfectWindow = 0.045f;
    public float goodWindow = 0.09f;
    public float okWindow = 0.14f;
    public float missWindow = 0.2f;

    // Events
    public event Action<int> OnBeat;            // beat index since song start
    public event Action<int> OnMeasure;         // measure (4-beat bar) index
    public event Action<Judgement, float> OnJudgement; // judgement + offset (-early/+late)

    public int BeatIndex { get; private set; }
    public int MeasureIndex { get; private set; }
    public int ClosestBeatIndex => Mathf.RoundToInt(songBpmPosition);
    public float SecPerBeat => 60f / Mathf.Max(1f, bpm);
    public float TimeToNextBeat => (ClosestBeatIndex + (songBpmPosition > ClosestBeatIndex ? 1 : 0) - songBpmPosition) * SecPerBeat;
    public float BeatPhase => Mathf.Repeat(songBpmPosition, 1f); // 0..1 within current beat

    double dspSongTime;   // AudioSettings.dspTime at song start
    bool playing;

    public enum Judgement { Perfect, Good, Ok, Miss, None }

    void Start()
    {
        if (musicSource == null) musicSource = GetComponent<AudioSource>();
    }

    /// <summary>Begin (or restart) the track.</summary>
    public void PlayFromStart()
    {
        if (musicSource == null) return;
        dspSongTime = (double)AudioSettings.dspTime;
        BeatIndex = -1;
        MeasureIndex = -1;
        playing = true;
        musicSource.Stop();
        musicSource.time = 0f;
        musicSource.Play();
    }

    void Update()
    {
        if (!playing || musicSource == null) return;

        float rate = compensatePitch ? Mathf.Max(0.01f, musicSource.pitch) : 1f;
        songPosition = (float)((double)AudioSettings.dspTime - dspSongTime) * rate - firstBeatOffset;
        songBpmPosition = songPosition / SecPerBeat;

        int beat = Mathf.FloorToInt(songBpmPosition);
        while (BeatIndex < beat)
        {
            BeatIndex++;
            OnBeat?.Invoke(BeatIndex);
            if (BeatIndex % 4 == 0)
            {
                MeasureIndex = BeatIndex / 4;
                OnMeasure?.Invoke(MeasureIndex);
            }
        }
    }

    // ---------------- JUDGING HITS ----------------

    /// <summary>
    /// Judge a player input right now against the nearest beat.
    /// Returns the judgement; also fires OnJudgement.
    /// </summary>
    public Judgement JudgeInput()
    {
        float offset = songBpmPosition - ClosestBeatIndex;  // in beats
        float offsetSeconds = Mathf.Abs(offset) * SecPerBeat;

        var j = Judgement.None;
        if (offsetSeconds <= perfectWindow) j = Judgement.Perfect;
        else if (offsetSeconds <= goodWindow) j = Judgement.Good;
        else if (offsetSeconds <= okWindow) j = Judgement.Ok;
        else if (offsetSeconds <= missWindow) j = Judgement.Miss;
        else j = Judgement.None; // input nowhere near a beat - ignore

        OnJudgement?.Invoke(j, offset * SecPerBeat);
        return j;
    }

    /// <summary>World position (e.g. a note lane Y) for a beat that is 'beatsAway' beats in the future.</summary>
    public float BeatArrivalPosition(float spawnPos, float hitPos, float beatsAway, float travelBeats = 2f)
    {
        float t = Mathf.Clamp01(beatsAway / Mathf.Max(0.01f, travelBeats));
        return Mathf.Lerp(hitPos, spawnPos, t);
    }

    // ---------------- BEAT-REACTIVE HELPERS ----------------

    /// <summary>0..1 pulse that decays after every beat: great for scaling objects on-beat.</summary>
    public float BeatPulse(float decaySpeed = 4f) => Mathf.Clamp01(1f - BeatPhase * decaySpeed);

    /// <summary>True only on the frame a beat occurred (checks once per frame).</summary>
    public bool BeatThisFrame()
    {
        float prev = Mathf.FloorToInt((songBpmPosition - Time.deltaTime / SecPerBeat));
        return Mathf.FloorToInt(songBpmPosition) > prev;
    }

    /// <summary>Snap any action to the next beat (returns how long to wait).</summary>
    public float DelayUntilNextBeat() => Mathf.Max(0f, TimeToNextBeat);

    /// <summary>Is the song currently on an 'off-beat' eighth? (for alternating patterns)</summary>
    public bool OnOffBeat() => BeatPhase > 0.45f && BeatPhase < 0.55f;
}

/// <summary>
/// EXAMPLE: makes any object 'dance' - scale-pops on every beat with a configurable intensity,
/// and can alternate direction every measure. Attach next to a RhythmConductor reference.
/// </summary>
public class BeatReactiveExample : MonoBehaviour
{
    public RhythmConductor conductor;
    public float pulseScale = 0.15f;
    public float decay = 5f;
    public bool alternatePerMeasure = false;
    public bool alsoRotate = false;

    Vector3 baseScale;
    Quaternion baseRot;

    void Start()
    {
        baseScale = transform.localScale;
        baseRot = transform.rotation;
    }

    void Update()
    {
        if (conductor == null) return;
        float pulse = conductor.BeatPulse(decay) * pulseScale;
        float dir = alternatePerMeasure && conductor.MeasureIndex % 2 == 1 ? -1f : 1f;
        transform.localScale = baseScale * (1f + pulse * dir);

        if (alsoRotate)
            transform.rotation = baseRot * Quaternion.Euler(0f, 0f, conductor.songBpmPosition * 30f);
    }
}
