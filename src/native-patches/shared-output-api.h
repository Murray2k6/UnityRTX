#pragma once
#include <remix/remix_c.h>

// Optional exports: resolve with GetProcAddress. No change to the 0.1000.0 table.
// Startup must use forceNoVkSwapchain=1; enable output before the first Present.
typedef remixapi_ErrorCode (REMIXAPI_CALL *PFN_remixapi_EnableUnityOutput)(void);
typedef remixapi_ErrorCode (REMIXAPI_CALL *PFN_remixapi_SetUnityOutputTargets)(void* sceneD3D11Texture, void* guiD3D11Texture);
typedef void* (REMIXAPI_CALL *PFN_remixapi_GetUnityRenderEvent)(void);
typedef uint64_t (REMIXAPI_CALL *PFN_remixapi_GetUnityOutputState)(void);
typedef remixapi_ErrorCode (REMIXAPI_CALL *PFN_remixapi_AcquireSharedD3D11Frame)(void** sceneHandle, void** guiHandle, uint32_t* width, uint32_t* height);
typedef remixapi_ErrorCode (REMIXAPI_CALL *PFN_remixapi_ReleaseSharedD3D11Frame)(void);
