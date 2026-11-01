using System;
using System.Runtime.InteropServices;

namespace Piper
{
    /// <summary>
    /// P/Invoke bindings for the parts of eSpeak NG that Piper needs
    /// (text -> IPA phonemes). Library is GPLv3, data in
    /// StreamingAssets/Piper/espeak-ng-data is GPLv3 as well.
    /// </summary>
    internal static class ESpeakNative
    {
        private const string LibName = "espeak-ng";

        // espeak_AUDIO_OUTPUT_PLAYBACK would try to open an audio device; we only
        // want phonemes, so use AUDIO_OUTPUT_SYNCHRONOUS (1) or PLAYBACK (0).
        // Piper-style usage passes 0; espeak-ng works without audio callbacks here.
        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int espeak_Initialize(int output, int buflength, string path, int options);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int espeak_SetVoiceByName(string name);

        // textmode: 0 = espeakCHARS_AUTO. phonememode bit 1 set (2) = IPA as UTF-8.
        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr espeak_TextToPhonemes(ref IntPtr text, int textmode, int phonememode);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int espeak_Terminate();
    }
}
