using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEngine;

namespace YargVr
{
    /// <summary>
    /// Taps YARG's audio engine for FFT spectrum data.
    ///
    /// YARG plays ALL of its audio through the native BASS library (un4seen, WASAPI/ASIO) -
    /// it never routes through Unity's AudioSource/AudioListener, so
    /// AudioListener.GetSpectrumData always returns silence and a listener-based visualizer
    /// never moves. Instead this class reflects into YARG's mixer manager, grabs the BASS
    /// stream handle of every active stem mixer, and calls BASS_ChannelGetData with an FFT
    /// flag - the exact call YARG itself uses for its whammy-pitch detector.
    ///
    /// bass.dll is already loaded into the game process (ManagedBass P/Invokes it), so a
    /// plain DllImport binds to the very same native module - no extra files to ship.
    /// </summary>
    internal static class BassSpectrum
    {
        /// <summary>BASS_DATA_FFT2048 (0x80000003) | BASS_DATA_FFT_REMOVEDC (0x40).</summary>
        private const int FftFlags = unchecked((int)0x80000003) | 0x40;

        /// <summary>FFT2048 produces 1024 complex pairs -> 1024 magnitude bins.</summary>
        public const int BinCount = 1024;

        /// <summary>Bin i of the FFT corresponds to this many Hz per bin at YARG's 44.1 kHz mixer rate.</summary>
        public const float HzPerBin = 44100f / 2048f;

        [DllImport("bass", EntryPoint = "BASS_ChannelGetData")]
        private static extern int BASS_ChannelGetData(int handle, float[] buffer, int length);

        private static bool _resolved;
        private static FieldInfo _instanceField;      // GlobalAudioHandler._instance (static)
        private static FieldInfo _activeMixersField;  // AudioManager._activeMixers (instance)
        private static FieldInfo _tempoHandleField;   // BassStemMixer._tempoStreamHandle (instance)
        private static FieldInfo _mixerHandleField;   // BassStemMixer._mixerHandle (instance)

        private static readonly List<int> _handles = new List<int>();
        private static readonly float[] _fftBuffer = new float[BinCount * 2]; // interleaved re/im
        private static int _nextRefreshFrame;

        /// <summary>True when at least one live BASS mixer handle was found.</summary>
        public static bool HasHandles
        {
            get { return _handles.Count > 0; }
        }

        private static void Resolve()
        {
            _resolved = true;
            try
            {
                Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < assemblies.Length; i++)
                {
                    Assembly asm = assemblies[i];

                    Type handler = asm.GetType("YARG.Core.Audio.GlobalAudioHandler");
                    if (handler != null && _instanceField == null)
                    {
                        _instanceField = handler.GetField("_instance",
                            BindingFlags.NonPublic | BindingFlags.Static);
                    }

                    Type audioManager = asm.GetType("YARG.Core.Audio.AudioManager");
                    if (audioManager != null && _activeMixersField == null)
                    {
                        _activeMixersField = audioManager.GetField("_activeMixers",
                            BindingFlags.NonPublic | BindingFlags.Instance);
                    }

                    Type bassMixer = asm.GetType("YARG.Audio.BASS.BassStemMixer");
                    if (bassMixer != null && _tempoHandleField == null)
                    {
                        _tempoHandleField = bassMixer.GetField("_tempoStreamHandle",
                            BindingFlags.NonPublic | BindingFlags.Instance);
                        _mixerHandleField = bassMixer.GetField("_mixerHandle",
                            BindingFlags.NonPublic | BindingFlags.Instance);
                    }
                }
            }
            catch
            {
                // Reflection is best-effort; the visualizer falls back to idling.
            }
        }

        /// <summary>
        /// Rebuilds the list of live mixer stream handles. Mixers are created/destroyed per
        /// song and for menu music, so this is refreshed about once per second.
        /// </summary>
        private static void RefreshHandles()
        {
            _handles.Clear();

            if (!_resolved)
            {
                Resolve();
            }

            if (_instanceField == null || _activeMixersField == null)
            {
                return;
            }

            try
            {
                object manager = _instanceField.GetValue(null);
                if (manager == null)
                {
                    return; // YARG has not initialized its audio manager yet
                }

                System.Collections.IEnumerable mixers =
                    _activeMixersField.GetValue(manager) as System.Collections.IEnumerable;
                if (mixers == null)
                {
                    return;
                }

                foreach (object mixer in mixers)
                {
                    if (mixer == null)
                    {
                        continue;
                    }

                    int handle = 0;
                    if (_tempoHandleField != null)
                    {
                        handle = (int)_tempoHandleField.GetValue(mixer);
                    }
                    if (handle == 0 && _mixerHandleField != null)
                    {
                        handle = (int)_mixerHandleField.GetValue(mixer);
                    }
                    if (handle != 0)
                    {
                        _handles.Add(handle);
                    }
                }
            }
            catch
            {
                _handles.Clear();
            }
        }

        /// <summary>
        /// Fills <paramref name="magnitudes"/> (length BinCount) with the FFT magnitude of the
        /// summed audio of every active stem mixer (max across mixers, which is usually just
        /// the song / menu music). Returns false when YARG's BASS engine could not be tapped
        /// (reflection failed, no mixers active, or the engine is silent/not playing yet).
        /// </summary>
        public static bool TryGetMagnitudes(float[] magnitudes)
        {
            if (magnitudes == null || magnitudes.Length < BinCount)
            {
                return false;
            }

            if (Time.frameCount >= _nextRefreshFrame)
            {
                _nextRefreshFrame = Time.frameCount + 60; // ~1 s between handle refreshes
                RefreshHandles();
            }

            if (_handles.Count == 0)
            {
                return false;
            }

            bool any = false;
            for (int h = 0; h < _handles.Count; h++)
            {
                int got;
                try
                {
                    got = BASS_ChannelGetData(_handles[h], _fftBuffer, FftFlags);
                }
                catch
                {
                    got = -1;
                }

                if (got <= 0)
                {
                    continue; // dead handle or no data yet for this mixer
                }

                if (!any)
                {
                    for (int b = 0; b < BinCount; b++)
                    {
                        magnitudes[b] = 0f;
                    }
                    any = true;
                }

                // BASS writes interleaved re/im pairs: bin magnitude = sqrt(re^2 + im^2).
                for (int b = 0; b < BinCount; b++)
                {
                    float re = _fftBuffer[b * 2];
                    float im = _fftBuffer[b * 2 + 1];
                    float mag = Mathf.Sqrt(re * re + im * im);
                    if (mag > magnitudes[b])
                    {
                        magnitudes[b] = mag;
                    }
                }
            }

            return any;
        }
    }
}
