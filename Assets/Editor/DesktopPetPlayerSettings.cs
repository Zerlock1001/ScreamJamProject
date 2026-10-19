using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// 透明窗口依赖 D3D11 的旧版呈现。Flip Model 开着时，背景会一直是黑色。
/// </summary>
[InitializeOnLoad]
static class DesktopPetPlayerSettings
{
    static DesktopPetPlayerSettings()
    {
        try
        {
            if (PlayerSettings.useFlipModelSwapchain)
                PlayerSettings.useFlipModelSwapchain = false;

            var target = BuildTarget.StandaloneWindows64;
            var apis = PlayerSettings.GetGraphicsAPIs(target);
            if (apis == null || apis.Length != 1 || apis[0] != GraphicsDeviceType.Direct3D11)
                PlayerSettings.SetGraphicsAPIs(target, new[] { GraphicsDeviceType.Direct3D11 });
        }
        catch (Exception)
        {
        }
    }
}
