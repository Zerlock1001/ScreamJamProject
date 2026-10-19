using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace ScreamJam.Audio
{
    /// <summary>
    /// Always-on microphone level monitor.
    /// Exposes RMS/peak levels in dBFS and runs the MICSTART baseline/verdict logic.
    /// </summary>
    public sealed class MicrophoneLevelService : MonoBehaviour
    {
        public enum MicStartPhase
        {
            Idle,
            Baseline,
            Listening,
            Loud
        }

        private const int SampleWindowFrames = 2048;
        private const float UpdateIntervalSeconds = 0.1f;
        private const int ClipLengthSeconds = 1;
        private const int ClipFrequency = 44100;
        private const float StartTimeoutSeconds = 2f;
        private const float RetryIntervalSeconds = 5f;
        private const float MinDb = -80f;
        private const float MinBaselineRms = 1e-5f;
        private const int RollingCapacity = 128;

        public static MicrophoneLevelService Instance { get; private set; }

        /// <summary>Duration of the MICSTART baseline recording, in seconds.</summary>
        public static float MicStartBaselineSeconds = 15f;

        /// <summary>Rolling average window used for the LOUD comparison, in seconds.</summary>
        public static float MicStartAverageSeconds = 1f;

        /// <summary>How long the average must stay above the loud threshold, in seconds.</summary>
        public static float MicStartVerdictSeconds = 5f;

        /// <summary>Maximum duration of a MICSTART event before it times out, in seconds.</summary>
        public static float MicStartTimeoutSeconds = 100f;

        /// <summary>Default microphone recording duration, in seconds.</summary>
        public static float RecordSeconds = 10f;

        public bool IsRunning { get; private set; }
        public bool HasSample { get; private set; }
        public float RmsLinear { get; private set; }
        public float RmsDb { get; private set; } = MinDb;
        public float PeakDb { get; private set; } = MinDb;
        public string DeviceName { get; private set; } = string.Empty;
        public string Status { get; private set; } = "Not started.";

        public MicStartPhase CurrentMicStartPhase { get; private set; } = MicStartPhase.Idle;
        public float MicStartBaselineRms { get; private set; }
        public float MicStartBaselineDb { get; private set; } = MinDb;

        public bool IsMicStartActive =>
            CurrentMicStartPhase == MicStartPhase.Baseline ||
            CurrentMicStartPhase == MicStartPhase.Listening;

        public float MicStartBaselineElapsedSeconds
        {
            get
            {
                if (CurrentMicStartPhase != MicStartPhase.Baseline || _baselineStartTime < 0f)
                    return 0f;

                return Mathf.Max(0f, Time.unscaledTime - _baselineStartTime);
            }
        }

        public float MicStartListeningElapsedSeconds
        {
            get
            {
                if (CurrentMicStartPhase != MicStartPhase.Listening)
                    return 0f;

                return Mathf.Max(0f, Time.unscaledTime - _listeningStartTime);
            }
        }

        public bool IsRecording { get; private set; }

        public float RecordingProgress =>
            IsRecording && _recordTargetFrames > 0
                ? Mathf.Clamp01((float)_recordFramesWritten / _recordTargetFrames)
                : 0f;

        public bool HasRecording =>
            _lastRecordingClip != null || File.Exists(RecordingPath);

        public static string RecordingsDirectory
        {
            get { return Path.Combine(Application.persistentDataPath, "Recordings"); }
        }

        public static string RecordingPath
        {
            get { return Path.Combine(RecordingsDirectory, "last_recording.wav"); }
        }

        private AudioClip _clip;
        private float[] _samples;
        private int _channels = 1;
        private float _nextUpdateTime;
        private float _startTime;
        private float _nextRetryTime;

        private float _baselineStartTime = -1f;
        private double _baselineSum;
        private int _baselineCount;
        private float _listeningStartTime;
        private float _loudStartTime = -1f;
        private float _zeroStartTime = -1f;
        private float _micStartStartedAt;

        private float[] _recordBuffer;
        private int _recordFramesWritten;
        private int _recordTargetFrames;
        private int _recordLastPosition;
        private AudioClip _lastRecordingClip;
        private AudioSource _playbackSource;

        private readonly float[] _rollingValues = new float[RollingCapacity];
        private readonly float[] _rollingTimes = new float[RollingCapacity];
        private int _rollingHead;
        private int _rollingCount;
        private double _rollingSum;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Bootstrap()
        {
            if (Instance != null)
                return;

            var go = new GameObject("[MicrophoneLevelService]");
            go.hideFlags = HideFlags.DontSave;
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<MicrophoneLevelService>();
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            TryStartMicrophone();
        }

        private void Update()
        {
            if (!IsRunning)
            {
                if (Time.unscaledTime >= _nextRetryTime)
                {
                    _nextRetryTime = Time.unscaledTime + RetryIntervalSeconds;
                    TryStartMicrophone();
                }

                return;
            }

            if (Time.unscaledTime < _nextUpdateTime)
                return;

            _nextUpdateTime = Time.unscaledTime + UpdateIntervalSeconds;
            SampleMicrophone();
        }

        private void OnDestroy()
        {
            if (Instance == this)
                Instance = null;

            StopMicrophone();
            CancelRecording();

            if (_playbackSource != null)
            {
                _playbackSource.Stop();
                _playbackSource.clip = null;
            }

            if (_lastRecordingClip != null)
            {
                Destroy(_lastRecordingClip);
                _lastRecordingClip = null;
            }
        }

        public bool StartMicStartEvent()
        {
            if (IsMicStartActive)
                return false;

            CurrentMicStartPhase = MicStartPhase.Baseline;
            _micStartStartedAt = Time.unscaledTime;
            _baselineStartTime = -1f;
            _baselineSum = 0.0;
            _baselineCount = 0;
            _listeningStartTime = 0f;
            _loudStartTime = -1f;
            MicStartBaselineRms = 0f;
            MicStartBaselineDb = MinDb;
            ResetRolling();
            return true;
        }

        public void StopMicStartEvent()
        {
            CurrentMicStartPhase = MicStartPhase.Idle;
            _baselineStartTime = -1f;
            _baselineSum = 0.0;
            _baselineCount = 0;
            _listeningStartTime = 0f;
            _loudStartTime = -1f;
            MicStartBaselineRms = 0f;
            MicStartBaselineDb = MinDb;
            ResetRolling();
        }

        private void TryStartMicrophone()
        {
            if (IsRunning)
                return;

            string[] devices = Microphone.devices;
            if (devices == null || devices.Length == 0)
            {
                Status = "No microphone found or access denied.";
                _nextRetryTime = Time.unscaledTime + RetryIntervalSeconds;
                return;
            }

            DeviceName = devices[0];

            try
            {
                _clip = Microphone.Start(DeviceName, true, ClipLengthSeconds, ClipFrequency);
            }
            catch (Exception ex)
            {
                _clip = null;
                Status = "Microphone failed to start: " + ex.Message;
            }

            if (_clip == null || _clip.samples == 0)
            {
                if (string.IsNullOrEmpty(Status) || !Status.StartsWith("Microphone failed", StringComparison.Ordinal))
                    Status = "Microphone failed to start.";

                if (_clip != null)
                    Destroy(_clip);

                _clip = null;
                _nextRetryTime = Time.unscaledTime + RetryIntervalSeconds;
                return;
            }

            _channels = Mathf.Max(1, _clip.channels);
            _samples = new float[_clip.samples * _channels];
            _startTime = Time.unscaledTime;
            _nextUpdateTime = 0f;
            _zeroStartTime = -1f;
            HasSample = false;
            RmsLinear = 0f;
            RmsDb = MinDb;
            PeakDb = MinDb;
            IsRunning = true;
            Status = "Starting...";

            Debug.Log("Microphone started: " + DeviceName);
        }

        private void SampleMicrophone()
        {
            float now = Time.unscaledTime;
            int position = Microphone.GetPosition(DeviceName);

            if (position <= 0)
            {
                // Before the first sample, 0 means the device has not started yet.
                // After that, 0 can simply be the loop wrap, so only treat a
                // sustained run of zeroes as a real failure.
                if (!HasSample)
                {
                    if (now - _startTime > StartTimeoutSeconds)
                    {
                        StopMicrophone();
                        Status = "Microphone produced no samples.";
                        _nextRetryTime = now + RetryIntervalSeconds;
                    }
                }
                else
                {
                    if (_zeroStartTime < 0f)
                        _zeroStartTime = now;
                    else if (now - _zeroStartTime > StartTimeoutSeconds)
                    {
                        StopMicrophone();
                        Status = "Microphone produced no samples.";
                        _nextRetryTime = now + RetryIntervalSeconds;
                    }
                }

                return;
            }

            _zeroStartTime = -1f;

            try
            {
                _clip.GetData(_samples, 0);
            }
            catch (Exception ex)
            {
                StopMicrophone();
                Status = "Microphone read failed: " + ex.Message;
                _nextRetryTime = Time.unscaledTime + RetryIntervalSeconds;
                return;
            }

            int frameCount = _clip.samples;
            int startFrame = position - SampleWindowFrames;
            while (startFrame < 0)
                startFrame += frameCount;

            double sum = 0.0;
            float peak = 0f;

            for (int i = 0; i < SampleWindowFrames; i++)
            {
                int frame = (startFrame + i) % frameCount;
                int baseIndex = frame * _channels;
                float sample = 0f;

                for (int c = 0; c < _channels; c++)
                    sample += _samples[baseIndex + c];

                sample /= _channels;
                sum += sample * sample;

                float abs = sample < 0f ? -sample : sample;
                if (abs > peak)
                    peak = abs;
            }

            float rms = Mathf.Sqrt((float)(sum / SampleWindowFrames));

            RmsLinear = Mathf.Clamp01(rms);
            RmsDb = Mathf.Clamp(20f * Mathf.Log10(Mathf.Max(rms, 1e-7f)), MinDb, 0f);
            PeakDb = Mathf.Clamp(20f * Mathf.Log10(Mathf.Max(peak, 1e-7f)), MinDb, 0f);
            HasSample = true;
            Status = "Running";

            AppendRecording(position, frameCount);
            UpdateMicStart(RmsLinear);
        }

        public bool StartRecording(float seconds = -1f)
        {
            if (IsRecording)
                return false;

            if (!IsRunning || _clip == null || !HasSample)
                return false;

            float duration = seconds > 0f ? seconds : RecordSeconds;
            duration = Mathf.Clamp(duration, 1f, 60f);

            _recordTargetFrames = Mathf.Max(1, Mathf.CeilToInt(duration * _clip.frequency));
            _recordBuffer = new float[_recordTargetFrames * _channels];
            _recordFramesWritten = 0;
            _recordLastPosition = Microphone.GetPosition(DeviceName);
            IsRecording = true;
            return true;
        }

        public bool TryPlayLastRecording(out string error)
        {
            error = null;

            if (IsRecording)
            {
                error = "Recording is in progress.";
                return false;
            }

            if (_lastRecordingClip == null && !TryLoadLastRecording(out error))
                return false;

            EnsurePlaybackSource();
            _playbackSource.Stop();
            _playbackSource.clip = _lastRecordingClip;
            _playbackSource.Play();
            return true;
        }

        public void ClearRecordingCache()
        {
            CancelRecording();

            if (_playbackSource != null)
            {
                _playbackSource.Stop();
                _playbackSource.clip = null;
            }

            if (_lastRecordingClip != null)
            {
                Destroy(_lastRecordingClip);
                _lastRecordingClip = null;
            }

            try
            {
                if (File.Exists(RecordingPath))
                    File.Delete(RecordingPath);
            }
            catch
            {
                // Ignore cleanup errors; the caller may delete the directory.
            }
        }

        private void AppendRecording(int position, int frameCount)
        {
            if (!IsRecording || _recordBuffer == null)
                return;

            if (_recordFramesWritten >= _recordTargetFrames)
            {
                FinishRecording();
                return;
            }

            int available = position - _recordLastPosition;
            if (available < 0)
                available += frameCount;

            if (available <= 0)
                return;

            if (available > frameCount)
                available = frameCount;

            int remaining = _recordTargetFrames - _recordFramesWritten;
            int copyFrames = Mathf.Min(available, remaining);

            for (int i = 0; i < copyFrames; i++)
            {
                int sourceFrame = (_recordLastPosition + i) % frameCount;
                int sourceIndex = sourceFrame * _channels;
                int targetIndex = (_recordFramesWritten + i) * _channels;

                for (int c = 0; c < _channels; c++)
                    _recordBuffer[targetIndex + c] = _samples[sourceIndex + c];
            }

            _recordFramesWritten += copyFrames;
            _recordLastPosition = (_recordLastPosition + copyFrames) % frameCount;

            if (_recordFramesWritten >= _recordTargetFrames)
                FinishRecording();
        }

        private void FinishRecording()
        {
            if (!IsRecording)
                return;

            IsRecording = false;

            int frames = _recordFramesWritten;
            int channels = _channels;
            int frequency = _clip != null ? _clip.frequency : ClipFrequency;

            if (frames <= 0)
            {
                _recordBuffer = null;
                return;
            }

            var data = new float[frames * channels];
            Array.Copy(_recordBuffer, 0, data, 0, data.Length);
            _recordBuffer = null;

            if (_lastRecordingClip != null)
            {
                if (_playbackSource != null)
                {
                    _playbackSource.Stop();
                    _playbackSource.clip = null;
                }

                Destroy(_lastRecordingClip);
            }

            _lastRecordingClip = AudioClip.Create("LastRecording", frames, channels, frequency, false);
            _lastRecordingClip.SetData(data, 0);

            try
            {
                Directory.CreateDirectory(RecordingsDirectory);
                WriteWav(RecordingPath, data, channels, frequency);
                Debug.Log(
                    "Recording saved: " +
                    (frames / (float)frequency).ToString("0.0", CultureInfo.InvariantCulture) +
                    "s.");
            }
            catch (Exception ex)
            {
                Debug.LogError("Failed to save recording: " + ex.Message);
            }
        }

        private void CancelRecording()
        {
            IsRecording = false;
            _recordBuffer = null;
            _recordFramesWritten = 0;
            _recordTargetFrames = 0;
            _recordLastPosition = 0;
        }

        private void EnsurePlaybackSource()
        {
            if (_playbackSource != null)
                return;

            _playbackSource = gameObject.AddComponent<AudioSource>();
            _playbackSource.playOnAwake = false;
            _playbackSource.spatialBlend = 0f;
        }

        private bool TryLoadLastRecording(out string error)
        {
            error = null;

            if (!File.Exists(RecordingPath))
            {
                error = "No recording found.";
                return false;
            }

            try
            {
                byte[] bytes = File.ReadAllBytes(RecordingPath);
                if (bytes.Length < 44)
                {
                    error = "Recording WAV is invalid.";
                    return false;
                }

                int channels = 1;
                int sampleRate = ClipFrequency;
                int bitsPerSample = 16;
                int dataOffset = -1;
                int dataLength = 0;
                int position = 12;

                while (position + 8 <= bytes.Length)
                {
                    string chunkId = Encoding.ASCII.GetString(bytes, position, 4);
                    int chunkSize = BitConverter.ToInt32(bytes, position + 4);
                    int chunkData = position + 8;

                    if (chunkId == "fmt " && chunkSize >= 16 && chunkData + 16 <= bytes.Length)
                    {
                        channels = BitConverter.ToInt16(bytes, chunkData + 2);
                        sampleRate = BitConverter.ToInt32(bytes, chunkData + 4);
                        bitsPerSample = BitConverter.ToInt16(bytes, chunkData + 14);
                    }
                    else if (chunkId == "data")
                    {
                        dataOffset = chunkData;
                        dataLength = Mathf.Min(chunkSize, bytes.Length - chunkData);
                        break;
                    }

                    position = chunkData + chunkSize + (chunkSize % 2);
                }

                if (dataOffset < 0 || bitsPerSample != 16 || channels <= 0 || sampleRate <= 0)
                {
                    error = "Unsupported recording WAV.";
                    return false;
                }

                int sampleCount = dataLength / 2;
                int frames = sampleCount / channels;

                if (frames <= 0)
                {
                    error = "Recording WAV is empty.";
                    return false;
                }

                var samples = new float[sampleCount];

                for (int i = 0; i < sampleCount; i++)
                {
                    short value = BitConverter.ToInt16(bytes, dataOffset + i * 2);
                    samples[i] = value / 32768f;
                }

                var clip = AudioClip.Create("LastRecording", frames, channels, sampleRate, false);
                clip.SetData(samples, 0);

                if (_lastRecordingClip != null)
                    Destroy(_lastRecordingClip);

                _lastRecordingClip = clip;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static void WriteWav(string path, float[] samples, int channels, int frequency)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));

            int dataSize = samples.Length * 2;

            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Encoding.ASCII.GetBytes("RIFF"));
                writer.Write(36 + dataSize);
                writer.Write(Encoding.ASCII.GetBytes("WAVE"));
                writer.Write(Encoding.ASCII.GetBytes("fmt "));
                writer.Write(16);
                writer.Write((short)1);
                writer.Write((short)channels);
                writer.Write(frequency);
                writer.Write(frequency * channels * 2);
                writer.Write((short)(channels * 2));
                writer.Write((short)16);
                writer.Write(Encoding.ASCII.GetBytes("data"));
                writer.Write(dataSize);

                for (int i = 0; i < samples.Length; i++)
                {
                    int value = Mathf.Clamp(
                        Mathf.RoundToInt(samples[i] * 32767f),
                        -32768,
                        32767);

                    writer.Write((short)value);
                }
            }
        }

        private void UpdateMicStart(float rmsLinear)
        {
            if (CurrentMicStartPhase != MicStartPhase.Baseline &&
                CurrentMicStartPhase != MicStartPhase.Listening)
            {
                return;
            }

            if (Time.unscaledTime - _micStartStartedAt >= MicStartTimeoutSeconds)
            {
                StopMicStartEvent();
                Debug.Log(
                    "MICSTART timed out: no loud level detected within " +
                    MicStartTimeoutSeconds.ToString("0.#", CultureInfo.InvariantCulture) +
                    "s.");
                return;
            }

            float now = Time.unscaledTime;

            if (CurrentMicStartPhase == MicStartPhase.Baseline)
            {
                if (_baselineStartTime < 0f)
                {
                    _baselineStartTime = now;
                    _baselineSum = 0.0;
                    _baselineCount = 0;
                }

                _baselineSum += rmsLinear;
                _baselineCount++;

                if (now - _baselineStartTime >= MicStartBaselineSeconds)
                {
                    double average = _baselineSum / Mathf.Max(1, _baselineCount);
                    MicStartBaselineRms = Mathf.Max((float)average, MinBaselineRms);
                    MicStartBaselineDb = Mathf.Clamp(
                        20f * Mathf.Log10(MicStartBaselineRms),
                        MinDb,
                        0f);

                    CurrentMicStartPhase = MicStartPhase.Listening;
                    _listeningStartTime = now;
                    ResetRolling();

                    Debug.Log(
                        "MICSTART baseline complete: " +
                        MicStartBaselineDb.ToString("0.0", CultureInfo.InvariantCulture) +
                        " dBFS.");
                }

                return;
            }

            AddRolling(now, rmsLinear);

            if (_rollingCount <= 0)
                return;

            float averageLevel = (float)(_rollingSum / _rollingCount);

            if (averageLevel > MicStartBaselineRms * 2f)
            {
                if (_loudStartTime < 0f)
                    _loudStartTime = now;

                if (now - _loudStartTime >= MicStartVerdictSeconds)
                {
                    CurrentMicStartPhase = MicStartPhase.Loud;
                    _loudStartTime = -1f;
                    ResetRolling();
                }
            }
            else
            {
                _loudStartTime = -1f;
            }
        }

        private void AddRolling(float time, float value)
        {
            while (_rollingCount > 0)
            {
                int oldest = _rollingHead;
                if (time - _rollingTimes[oldest] <= MicStartAverageSeconds)
                    break;

                _rollingSum -= _rollingValues[oldest];
                _rollingHead = (_rollingHead + 1) % RollingCapacity;
                _rollingCount--;
            }

            int index = (_rollingHead + _rollingCount) % RollingCapacity;
            _rollingValues[index] = value;
            _rollingTimes[index] = time;
            _rollingSum += value;
            _rollingCount++;

            if (_rollingCount > RollingCapacity)
            {
                _rollingSum -= _rollingValues[_rollingHead];
                _rollingHead = (_rollingHead + 1) % RollingCapacity;
                _rollingCount--;
            }
        }

        private void ResetRolling()
        {
            _rollingHead = 0;
            _rollingCount = 0;
            _rollingSum = 0.0;
        }

        private void StopMicrophone()
        {
            if (!string.IsNullOrEmpty(DeviceName))
            {
                try
                {
                    if (Microphone.IsRecording(DeviceName))
                        Microphone.End(DeviceName);
                }
                catch
                {
                    // Device may already be gone.
                }
            }

            if (_clip != null)
            {
                Destroy(_clip);
                _clip = null;
            }

            _samples = null;
            _zeroStartTime = -1f;
            IsRunning = false;
            HasSample = false;
            RmsLinear = 0f;
            RmsDb = MinDb;
            PeakDb = MinDb;
            CancelRecording();

            if (CurrentMicStartPhase == MicStartPhase.Baseline ||
                CurrentMicStartPhase == MicStartPhase.Listening)
            {
                StopMicStartEvent();
            }
        }
    }
}
