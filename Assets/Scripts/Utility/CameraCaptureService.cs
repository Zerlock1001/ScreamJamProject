using System;
using System.Collections;
using System.Globalization;
using UnityEngine;

namespace ScreamJam.Utility
{
    /// <summary>
    /// Shared webcam capture flow used by the debug /camera command and by
    /// in-game photo moments. The camera is opened, checked for changing
    /// frames, then a fresh frame is captured and handed back to the caller.
    /// </summary>
    public sealed class CameraCaptureResult
    {
        public Texture2D Photo;
        public string Error;
        public float MotionDifference;
        public bool Success => Photo != null;
    }

    public static class CameraCaptureService
    {
        public const int RequestedWidth = 1280;
        public const int RequestedHeight = 720;
        public const int RequestedFps = 30;
        public const float StartTimeoutSeconds = 8f;
        public const float FrameTimeoutSeconds = 3f;
        public const float FrameWaitTimeoutSeconds = 1f;
        public const int MotionGridWidth = 32;
        public const int MotionGridHeight = 18;
        public const float DefaultCaptureDelaySeconds = 2f;

        public static float MotionWarmupSeconds = 1f;
        public static float MotionCheckDelaySeconds = 0.3f;
        public static float MotionThreshold = 0.5f;
        public static bool HasCapturedPhoto { get; private set; }

        public static bool TryStartLiveCamera(out WebCamTexture camera, out string error)
        {
            camera = null;
            error = null;

            string device;
            try
            {
                device = PickCameraDevice();
            }
            catch (Exception ex)
            {
                error = "Failed to enumerate cameras: " + ex.Message;
                return false;
            }

            if (string.IsNullOrEmpty(device))
            {
                error = "No camera found.";
                return false;
            }

            camera = CreateCamera(device);
            if (camera == null)
            {
                error = "Failed to create camera.";
                return false;
            }

            if (!TryPlayCamera(camera, out error))
            {
                StopCamera(camera);
                camera = null;
                return false;
            }

            return true;
        }

        public static void ReleaseLiveCamera(WebCamTexture camera)
        {
            StopCamera(camera);
        }

        public static IEnumerator Capture(float delaySeconds, CameraCaptureResult result)
        {
            if (result == null)
                yield break;

            WebCamTexture camera = null;
            Color32[] frameBuffer = null;
            float[] motionBaseline = null;

            try
            {
                string device;
                try
                {
                    device = PickCameraDevice();
                }
                catch (Exception ex)
                {
                    result.Error = "Failed to enumerate cameras: " + ex.Message;
                    yield break;
                }

                if (string.IsNullOrEmpty(device))
                {
                    result.Error = "No camera found.";
                    yield break;
                }

                camera = CreateCamera(device);
                if (camera == null)
                {
                    result.Error = "Failed to create camera.";
                    yield break;
                }

                if (!TryPlayCamera(camera, out string playError))
                {
                    result.Error = "Failed to start camera: " + playError;
                    yield break;
                }

                float captureTime = Time.realtimeSinceStartup + Mathf.Max(0f, delaySeconds);

                float readyDeadline = Time.realtimeSinceStartup + StartTimeoutSeconds;
                while (camera.width <= 16 || !camera.didUpdateThisFrame)
                {
                    if (Time.realtimeSinceStartup > readyDeadline)
                    {
                        result.Error = "Camera failed to start (timeout).";
                        yield break;
                    }

                    yield return null;
                }

                int width = camera.width;
                int height = camera.height;
                frameBuffer = new Color32[width * height];

                float warmup = Mathf.Clamp(MotionWarmupSeconds, 0f, 10f);
                float warmupEnd = Time.realtimeSinceStartup + warmup;
                while (Time.realtimeSinceStartup < warmupEnd)
                    yield return null;

                bool gotReferenceFrame = false;
                float referenceDeadline = Time.realtimeSinceStartup + FrameWaitTimeoutSeconds;
                while (Time.realtimeSinceStartup < referenceDeadline)
                {
                    if (camera.didUpdateThisFrame)
                    {
                        gotReferenceFrame = true;
                        break;
                    }

                    yield return null;
                }

                if (!gotReferenceFrame)
                {
                    result.Error =
                        "Camera invalid: no frame after " +
                        warmup.ToString("0.##", CultureInfo.InvariantCulture) +
                        "s warm-up (timeout " +
                        FrameWaitTimeoutSeconds.ToString("0.##", CultureInfo.InvariantCulture) +
                        "s).";
                    yield break;
                }

                if (!TryReadFrame(camera, frameBuffer, out string baselineError))
                {
                    result.Error = "Failed to read camera frame: " + baselineError;
                    yield break;
                }

                motionBaseline = new float[MotionGridWidth * MotionGridHeight];
                SampleLumaGrid(frameBuffer, width, height, motionBaseline);

                float checkDelay = Mathf.Clamp(MotionCheckDelaySeconds, 0.05f, 5f);
                float motionThreshold = Mathf.Max(0.01f, MotionThreshold);
                float checkTime = Time.realtimeSinceStartup + checkDelay;
                while (Time.realtimeSinceStartup < checkTime)
                    yield return null;

                bool gotComparisonFrame = false;
                float comparisonDeadline = Time.realtimeSinceStartup + FrameWaitTimeoutSeconds;
                while (Time.realtimeSinceStartup < comparisonDeadline)
                {
                    if (camera.didUpdateThisFrame)
                    {
                        gotComparisonFrame = true;
                        break;
                    }

                    yield return null;
                }

                if (!gotComparisonFrame)
                {
                    result.Error =
                        "Camera invalid: no new frame after " +
                        checkDelay.ToString("0.##", CultureInfo.InvariantCulture) +
                        "s (timeout " +
                        FrameWaitTimeoutSeconds.ToString("0.##", CultureInfo.InvariantCulture) +
                        "s).";
                    yield break;
                }

                if (!TryReadFrame(camera, frameBuffer, out string sampleError))
                {
                    result.Error = "Failed to read camera frame: " + sampleError;
                    yield break;
                }

                float difference = ComputeLumaDifference(
                    frameBuffer,
                    width,
                    height,
                    motionBaseline);

                if (difference < motionThreshold)
                {
                    result.Error =
                        "Camera invalid: no pixel change detected in " +
                        checkDelay.ToString("0.##", CultureInfo.InvariantCulture) +
                        "s (diff " +
                        difference.ToString("0.###", CultureInfo.InvariantCulture) +
                        ", threshold " +
                        motionThreshold.ToString("0.###", CultureInfo.InvariantCulture) +
                        ").";
                    yield break;
                }

                result.MotionDifference = difference;

                while (Time.realtimeSinceStartup < captureTime)
                    yield return null;

                bool freshFrame = false;
                float frameDeadline = Time.realtimeSinceStartup + FrameTimeoutSeconds;
                while (Time.realtimeSinceStartup < frameDeadline)
                {
                    if (camera.didUpdateThisFrame)
                    {
                        freshFrame = true;
                        break;
                    }

                    yield return null;
                }

                if (!freshFrame)
                {
                    result.Error = "Camera stopped producing frames.";
                    yield break;
                }

                result.Photo = GrabFrame(camera);
                if (result.Photo == null)
                    result.Error = "Failed to capture photo.";
                else
                    HasCapturedPhoto = true;
            }
            finally
            {
                StopCamera(camera);
                frameBuffer = null;
                motionBaseline = null;
            }
        }

        static string PickCameraDevice()
        {
            WebCamDevice[] devices = WebCamTexture.devices;
            if (devices == null || devices.Length == 0)
                return null;

            for (int i = 0; i < devices.Length; i++)
            {
                string deviceName = devices[i].name;
                if (string.IsNullOrEmpty(deviceName))
                    continue;

                if (deviceName.IndexOf("virtual", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;

                return deviceName;
            }

            return devices[0].name;
        }

        static WebCamTexture CreateCamera(string deviceName)
        {
            try
            {
                return new WebCamTexture(
                    deviceName,
                    RequestedWidth,
                    RequestedHeight,
                    RequestedFps);
            }
            catch
            {
                return null;
            }
        }

        static bool TryPlayCamera(WebCamTexture camera, out string error)
        {
            try
            {
                camera.Play();
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        static void StopCamera(WebCamTexture camera)
        {
            if (camera == null)
                return;

            try
            {
                camera.Stop();
            }
            catch
            {
                // Ignore shutdown errors from a removed/busy device.
            }

            UnityEngine.Object.Destroy(camera);
        }

        static bool TryReadFrame(WebCamTexture source, Color32[] buffer, out string error)
        {
            try
            {
                source.GetPixels32(buffer);
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        static void SampleLumaGrid(Color32[] pixels, int width, int height, float[] target)
        {
            for (int gy = 0; gy < MotionGridHeight; gy++)
            {
                int y = MotionGridHeight <= 1
                    ? 0
                    : (gy * (height - 1)) / (MotionGridHeight - 1);

                for (int gx = 0; gx < MotionGridWidth; gx++)
                {
                    int x = MotionGridWidth <= 1
                        ? 0
                        : (gx * (width - 1)) / (MotionGridWidth - 1);

                    target[gy * MotionGridWidth + gx] = Luma(pixels[y * width + x]);
                }
            }
        }

        static float ComputeLumaDifference(
            Color32[] pixels,
            int width,
            int height,
            float[] baseline)
        {
            float sum = 0f;

            for (int gy = 0; gy < MotionGridHeight; gy++)
            {
                int y = MotionGridHeight <= 1
                    ? 0
                    : (gy * (height - 1)) / (MotionGridHeight - 1);

                for (int gx = 0; gx < MotionGridWidth; gx++)
                {
                    int x = MotionGridWidth <= 1
                        ? 0
                        : (gx * (width - 1)) / (MotionGridWidth - 1);

                    float difference = Luma(pixels[y * width + x]) - baseline[gy * MotionGridWidth + gx];
                    sum += Mathf.Abs(difference);
                }
            }

            return sum / (MotionGridWidth * MotionGridHeight);
        }

        static float Luma(Color32 color)
        {
            return 0.299f * color.r + 0.587f * color.g + 0.114f * color.b;
        }

        static Texture2D GrabFrame(WebCamTexture source)
        {
            int width = source.width;
            int height = source.height;

            if (width <= 0 || height <= 0)
                return null;

            var pixels = new Color32[width * height];

            try
            {
                source.GetPixels32(pixels);
            }
            catch
            {
                return null;
            }

            if (source.videoVerticallyMirrored)
                FlipVertically(pixels, width, height);

            var texture = new Texture2D(width, height, TextureFormat.RGB24, false);
            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            return texture;
        }

        static void FlipVertically(Color32[] pixels, int width, int height)
        {
            for (int y = 0; y < height / 2; y++)
            {
                int top = y * width;
                int bottom = (height - 1 - y) * width;

                for (int x = 0; x < width; x++)
                {
                    Color32 temp = pixels[top + x];
                    pixels[top + x] = pixels[bottom + x];
                    pixels[bottom + x] = temp;
                }
            }
        }
    }
}
