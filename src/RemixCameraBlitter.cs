using System;
using BepInEx.Logging;
using UnityEngine;

namespace UnityRemix
{
    /// <summary>
    /// Attached to the active 3D World Camera in SingleWindow Copy mode.
    /// Injects the captured Remix framebuffer into the render pipeline via OnRenderImage,
    /// allowing subsequent UI cameras, Canvases, and OnGUI to render natively on top in the DX11 window.
    /// </summary>
    public class RemixCameraBlitter : MonoBehaviour
    {
        private ManualLogSource logger;
        private RemixWindowManager windowManager;
        private Texture2D copyTexture;
        private byte[] pixelBuffer;
        private int lastWidth = 0;
        private int lastHeight = 0;
        private bool enabledBlit = true;

        public void Initialize(RemixWindowManager windowManager, ManualLogSource logger)
        {
            this.windowManager = windowManager;
            this.logger = logger;
            logger?.LogInfo($"[RemixCameraBlitter] Attached to camera '{gameObject.name}'");
        }

        public void SetBlitEnabled(bool enabled)
        {
            this.enabledBlit = enabled;
        }

        void OnRenderImage(RenderTexture src, RenderTexture dest)
        {
            if (enabledBlit && windowManager != null)
            {
                int width = Screen.width > 0 ? Screen.width : 1920;
                int height = Screen.height > 0 ? Screen.height : 1080;

                if (copyTexture == null || lastWidth != width || lastHeight != height)
                {
                    if (copyTexture != null) Destroy(copyTexture);
                    copyTexture = new Texture2D(width, height, TextureFormat.BGRA32, false);
                    pixelBuffer = new byte[width * height * 4];
                    lastWidth = width;
                    lastHeight = height;
                }

                if (windowManager.CaptureRemixFramebuffer(pixelBuffer, width, height))
                {
                    copyTexture.LoadRawTextureData(pixelBuffer);
                    copyTexture.Apply(false, false);
                    Graphics.Blit(copyTexture, dest);
                    return;
                }
            }

            Graphics.Blit(src, dest);
        }

        void OnDestroy()
        {
            if (copyTexture != null)
            {
                Destroy(copyTexture);
                copyTexture = null;
            }
        }
    }
}
