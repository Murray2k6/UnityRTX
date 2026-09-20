#define WIN32_LEAN_AND_MEAN
#define REMIX_ALLOW_X86
#include <windows.h>
#include <d3d9.h>
#include <d3dcompiler.h>
#include <cstring>
#include <cstdio>
#include <stdexcept>
#include "../artifacts/remix-native/source/public/include/remix/remix_c.h"

using BridgeVersion = uint32_t (__stdcall*)();
using RendererInfo = int (__stdcall*)(uint64_t*);
using TagUiTexture = int (__stdcall*)(IDirect3DTexture9*);
using TagTextureCategory = int (__stdcall*)(IDirect3DBaseTexture9*, uint32_t);
using RegisterWaterMaterial = int (__stdcall*)(IDirect3DBaseTexture9*, float, float, float, float);

static void require(bool value, const char* message) {
  if (!value) { throw std::runtime_error(message); }
  std::printf("PASS: %s\n", message);
  std::fflush(stdout);
}

static void pump() {
  MSG message{};
  while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
    TranslateMessage(&message);
    DispatchMessageW(&message);
  }
}

struct Vertex { float x, y, z, nx, ny, nz, u, v; };

static bool tracedWorldReady(IDirect3DDevice9* device) {
  IDirect3DSurface9* rendered = nullptr;
  IDirect3DSurface9* readback = nullptr;
  D3DLOCKED_RECT locked{};
  HRESULT result = device->GetRenderTarget(0, &rendered);
  if (SUCCEEDED(result)) result = device->CreateOffscreenPlainSurface(640, 480, D3DFMT_X8R8G8B8, D3DPOOL_SYSTEMMEM, &readback, nullptr);
  if (SUCCEEDED(result)) result = device->GetRenderTargetData(rendered, readback);
  if (SUCCEEDED(result)) result = readback->LockRect(&locked, nullptr, D3DLOCK_READONLY);
  bool ready = false;
  if (SUCCEEDED(result)) {
    const uint32_t pixel = *reinterpret_cast<uint32_t*>(static_cast<char*>(locked.pBits) + 240 * locked.Pitch + 320 * 4);
    const uint32_t r = (pixel >> 16) & 255, g = (pixel >> 8) & 255, b = pixel & 255;
    ready = g > r * 1.15f && g > b * 1.15f;
    readback->UnlockRect();
  }
  if (readback) readback->Release();
  if (rendered) rendered->Release();
  if (FAILED(result)) throw std::runtime_error("World pipeline warmup readback failed");
  return ready;
}

int main(int argc, char** argv) {
  SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX);
  try {
    HMODULE library = LoadLibraryW(L".\\d3d9.dll");
    require(library != nullptr, "32-bit bridge library loaded");
    auto bridgeVersion = reinterpret_cast<BridgeVersion>(GetProcAddress(library, "UnityRemix_LegacyBridgeVersion"));
    auto rendererInfo = reinterpret_cast<RendererInfo>(GetProcAddress(library, "UnityRemix_GetRendererInfo"));
    auto tagTexture = reinterpret_cast<TagUiTexture>(GetProcAddress(library, "UnityRemix_TagUiTexture"));
    auto tagCategory = reinterpret_cast<TagTextureCategory>(GetProcAddress(library, "UnityRemix_TagTextureCategory"));
    auto registerWater = reinterpret_cast<RegisterWaterMaterial>(GetProcAddress(library, "UnityRemix_RegisterWaterMaterial"));
    require(bridgeVersion && rendererInfo && tagTexture && bridgeVersion() == 1, "legacy bridge protocol exports");
    require(tagCategory && tagCategory(nullptr, 1) == REMIXAPI_ERROR_CODE_INVALID_ARGUMENTS, "category extension validates texture pointers");
    auto create = reinterpret_cast<IDirect3D9* (WINAPI*)(UINT)>(GetProcAddress(library, "Direct3DCreate9"));
    IDirect3D9* d3d = create(D3D_SDK_VERSION);
    require(d3d != nullptr, "32-bit D3D9 connects to the 64-bit server");
    HWND window = CreateWindowExW(0, L"STATIC", L"UnityRemix isolated legacy probe", WS_OVERLAPPEDWINDOW,
      0, 0, 640, 480, nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
    require(window != nullptr, "independent hidden test window created");
    D3DPRESENT_PARAMETERS present{};
    present.BackBufferWidth = 640;
    present.BackBufferHeight = 480;
    present.BackBufferFormat = D3DFMT_X8R8G8B8;
    present.BackBufferCount = 1;
    present.SwapEffect = D3DSWAPEFFECT_DISCARD;
    present.hDeviceWindow = window;
    present.Windowed = TRUE;
    present.EnableAutoDepthStencil = TRUE;
    present.AutoDepthStencilFormat = D3DFMT_D24S8;
    present.PresentationInterval = D3DPRESENT_INTERVAL_IMMEDIATE;
    IDirect3DDevice9* device = nullptr;
    HRESULT result = d3d->CreateDevice(0, D3DDEVTYPE_HAL, window, D3DCREATE_HARDWARE_VERTEXPROCESSING | D3DCREATE_MULTITHREADED, &present, &device);
    std::printf("CreateDevice HRESULT=0x%08lX\n", static_cast<unsigned long>(result));
    require(SUCCEEDED(result) && device, "Remix graphics device created through the bridge");
    uint64_t apiVersion = 0;
    require(rendererInfo(&apiVersion) == 0 && REMIXAPI_VERSION_GET_MINOR(apiVersion) == 1000, "renderer API negotiated across 32/64-bit processes");
    auto initialize = reinterpret_cast<PFN_remixapi_InitializeLibrary>(GetProcAddress(library, "remixapi_InitializeLibrary"));
    remixapi_InitializeLibraryInfo info{ REMIXAPI_STRUCT_TYPE_INITIALIZE_LIBRARY_INFO, nullptr, REMIXAPI_VERSION_MAKE(0, 6, 0) };
    remixapi_Interface api{};
    require(initialize(&info, &api) == REMIXAPI_ERROR_CODE_INCOMPATIBLE_VERSION, "incorrect upstream ABI rejected by the bridge");
    info.version = apiVersion;
    require(initialize(&info, &api) == REMIXAPI_ERROR_CODE_SUCCESS, "native bridge API initialized");
    IDirect3DTexture9* texture = nullptr;
    require(SUCCEEDED(device->CreateTexture(32, 32, 1, 0, D3DFMT_A8R8G8B8, D3DPOOL_MANAGED, &texture, nullptr)), "D3D9 texture crosses the bridge");
    D3DLOCKED_RECT locked{};
    require(SUCCEEDED(texture->LockRect(0, &locked, nullptr, 0)), "texture CPU update is writable");
    for (int y = 0; y < 32; y++) {
      auto row = reinterpret_cast<uint32_t*>(static_cast<char*>(locked.pBits) + y * locked.Pitch);
      for (int x = 0; x < 32; x++) { row[x] = (x + y) % 2 ? 0xff80ff80 : 0xff206040; }
    }
    texture->UnlockRect(0);
    IDirect3DCubeTexture9* cube = nullptr;
    require(SUCCEEDED(device->CreateCubeTexture(16, 1, 0, D3DFMT_A8R8G8B8, D3DPOOL_MANAGED, &cube, nullptr)), "cubemap sky crosses the bridge");
    for (int face = 0; face < 6; face++) {
      require(SUCCEEDED(cube->LockRect(static_cast<D3DCUBEMAP_FACES>(face), 0, &locked, nullptr, 0)), "sky cubemap face is writable");
      for (int y = 0; y < 16; y++) {
        auto row = reinterpret_cast<uint32_t*>(static_cast<char*>(locked.pBits) + y * locked.Pitch);
        for (int x = 0; x < 16; x++) { row[x] = 0xff804020 + face * 0x00101010 + x + y; }
      }
      cube->UnlockRect(static_cast<D3DCUBEMAP_FACES>(face), 0);
    }
    D3DMATRIX identity{};
    identity._11 = identity._22 = identity._33 = identity._44 = 1;
    D3DMATRIX projection{};
    projection._11 = projection._22 = 1;
    projection._33 = 100.0f / 99.9f;
    projection._34 = 1;
    projection._43 = -10.0f / 99.9f;
    device->SetTransform(D3DTS_WORLD, &identity);
    device->SetTransform(D3DTS_VIEW, &identity);
    device->SetTransform(D3DTS_PROJECTION, &projection);
    device->SetRenderState(D3DRS_CULLMODE, D3DCULL_NONE);
    device->SetRenderState(D3DRS_LIGHTING, FALSE);
    D3DLIGHT9 sun{};
    sun.Type = D3DLIGHT_DIRECTIONAL;
    sun.Direction = {0, -0.6f, 0.8f};
    sun.Diffuse = {0.8f, 0.6f, 0.4f, 1.0f};
    require(SUCCEEDED(device->SetLight(0, &sun)) && SUCCEEDED(device->LightEnable(0, TRUE)), "directional light crosses the D3D9 bridge");
    D3DLIGHT9 returnedSun{};
    require(SUCCEEDED(device->GetLight(0, &returnedSun)) && returnedSun.Type == D3DLIGHT_DIRECTIONAL && returnedSun.Direction.z == sun.Direction.z && returnedSun.Diffuse.r == sun.Diffuse.r, "directional light preserves direction and radiance");
    device->SetFVF(D3DFVF_XYZ | D3DFVF_NORMAL | D3DFVF_TEX1);
    device->SetTexture(0, texture);
    const Vertex vertices[] = { {-1,-1,2,0,0,-1,0,1}, {0,1,2,0,0,-1,0.5f,0}, {1,-1,2,0,0,-1,1,1} };
    for (int frame = 0; frame < 24; frame++) {
      pump();
      if (frame == 18) { device->SetTexture(0, cube); }
      device->Clear(0, nullptr, D3DCLEAR_TARGET | D3DCLEAR_ZBUFFER, 0xff203040, 1.0f, 0);
      device->BeginScene();
      require(SUCCEEDED(device->DrawPrimitiveUP(D3DPT_TRIANGLELIST, 1, vertices, sizeof(Vertex))), "fixed-function mesh submission");
      device->EndScene();
      require(SUCCEEDED(device->Present(nullptr, nullptr, nullptr, nullptr)), "frame presentation into the existing window");
      if (frame == 10) { require(tagTexture(texture) == 0, "Unity UI texture hash tagged in the 64-bit renderer"); }
      if (frame == 20) {
        require(tagCategory(cube, 1) == 0, "cubemap sky hash survives type-correct bridge transport");
        require(tagCategory(texture, 1) == 0 && tagCategory(texture, 2) == 0 && tagCategory(texture, 3) == 0, "sky, water, and depth-qualified Canvas categories cross the bridge");
        require(tagCategory(cube, 4) == 0, "procedural ocean reflection input crosses the bridge");
        require(tagCategory(texture, 99) == REMIXAPI_ERROR_CODE_INVALID_ARGUMENTS, "unknown category is rejected");
      }
      if (frame == 12) { require(api.SetUIState(REMIXAPI_UI_STATE_ADVANCED) == REMIXAPI_ERROR_CODE_SUCCESS, "native Remix menu opens through IPC"); }
      if (frame == 18) { require(api.GetUIState() == REMIXAPI_UI_STATE_ADVANCED, "native menu state round-trips through IPC"); }
      Sleep(40);
    }
    require(api.SetUIState(REMIXAPI_UI_STATE_NONE) == REMIXAPI_ERROR_CODE_SUCCESS, "native Remix menu closes through IPC");
    require(registerWater && registerWater(cube, 0.1f, 0.35f, 0.45f, 1.333f) == 0, "water transmittance and IOR cross the bridge");
    require(api.SetConfigVariable("rtx.useVertexCapture", "False") == 0, "exercise Unity capture independently of the generic capture option");
    const char* waterVs = "float4 main(float4 p:POSITION):POSITION { return float4(p.xy,p.z*0.25,1); }";
    const Vertex waterVertices[] = { {-1,-1,3.2f,0,0,-1,0,1}, {0,1,3.2f,0,0,-1,0.5f,0}, {1,-1,3.2f,0,0,-1,1,1} };
    const char* waterPs = "samplerCUBE sky:register(s0); float4 main():COLOR { return float4(texCUBE(sky,float3(0,0,1)).rgb*0.1+float3(0.05,0.27,0.35),1); }";
    ID3DBlob* vsCode = nullptr;
    ID3DBlob* psCode = nullptr;
    require(SUCCEEDED(D3DCompile(waterVs, strlen(waterVs), nullptr, nullptr, nullptr, "main", "vs_3_0", 0, 0, &vsCode, nullptr)) &&
      SUCCEEDED(D3DCompile(waterPs, strlen(waterPs), nullptr, nullptr, nullptr, "main", "ps_3_0", 0, 0, &psCode, nullptr)), "procedural water test shaders compile");
    IDirect3DVertexShader9* vertexShader = nullptr;
    IDirect3DPixelShader9* pixelShader = nullptr;
    require(SUCCEEDED(device->CreateVertexShader(static_cast<const DWORD*>(vsCode->GetBufferPointer()), &vertexShader)) &&
      SUCCEEDED(device->CreatePixelShader(static_cast<const DWORD*>(psCode->GetBufferPointer()), &pixelShader)), "procedural water shaders cross the bridge");
    vsCode->Release(); psCode->Release();
    const char* worldPs = "sampler2D base:register(s3); float4 main():COLOR { return float4(tex2D(base,float2(0.5,0.5)).rgb*0.001+float3(1,0,1),1); }";
    IDirect3DPixelShader9* worldShader = nullptr;
    require(SUCCEEDED(D3DCompile(worldPs, strlen(worldPs), nullptr, nullptr, nullptr, "main", "ps_3_0", 0, 0, &psCode, nullptr)) &&
      SUCCEEDED(device->CreatePixelShader(static_cast<const DWORD*>(psCode->GetBufferPointer()), &worldShader)), "sparse-sampler world shader compiles");
    psCode->Release();
    IDirect3DTexture9* worldTexture = nullptr;
    require(SUCCEEDED(device->CreateTexture(32, 32, 1, 0, D3DFMT_A8R8G8B8, D3DPOOL_MANAGED, &worldTexture, nullptr)) &&
      SUCCEEDED(worldTexture->LockRect(0, &locked, nullptr, 0)), "independent world albedo is writable");
    for (int y = 0; y < 32; ++y) {
      auto row = reinterpret_cast<uint32_t*>(static_cast<char*>(locked.pBits) + y * locked.Pitch);
      for (int x = 0; x < 32; ++x) { row[x] = 0xff20c040; }
    }
    worldTexture->UnlockRect(0);
    const ULONGLONG warmupDeadline = GetTickCount64() + 60000;
    for (int frame = 0; ; ++frame) {
      device->SetRenderState(D3DRS_ZENABLE, TRUE);
      device->SetRenderState(D3DRS_ZWRITEENABLE, TRUE);
      device->SetRenderState(D3DRS_ALPHABLENDENABLE, FALSE);
      device->Clear(0, nullptr, D3DCLEAR_TARGET | D3DCLEAR_ZBUFFER, 0xff203040, 1, 0);
      device->BeginScene();
      device->SetVertexShader(nullptr); device->SetPixelShader(nullptr); device->SetTexture(0, worldTexture);
      device->DrawPrimitiveUP(D3DPT_TRIANGLELIST, 1, vertices, sizeof(Vertex));
      device->SetVertexShader(vertexShader); device->SetPixelShader(pixelShader); device->SetTexture(0, cube);
      device->SetRenderState(D3DRS_ZWRITEENABLE, FALSE);
      require(SUCCEEDED(device->DrawPrimitiveUP(D3DPT_TRIANGLELIST, 1, waterVertices, sizeof(Vertex))), "displaced water enters scene capture");
      device->SetPixelShader(worldShader); device->SetTexture(0, nullptr); device->SetTexture(3, worldTexture);
      device->SetRenderState(D3DRS_ZWRITEENABLE, TRUE);
      require(SUCCEEDED(device->DrawPrimitiveUP(D3DPT_TRIANGLELIST, 1, vertices, sizeof(Vertex))), "world shader following water enters scene capture");
      device->EndScene();
      // New shader binaries invalidate the driver's pipeline cache. Wait for real
      // traced pixels, with a deadline; a permanent raster fallback still fails.
      if (frame >= 11 && (tracedWorldReady(device) || GetTickCount64() >= warmupDeadline)) break;
      device->Present(nullptr, nullptr, nullptr, nullptr); pump(); Sleep(40);
    }
    // Read before Present: DISCARD swapchains do not preserve the backbuffer.
    if (argc > 1 && std::strcmp(argv[1], "--readback") == 0) {
    IDirect3DSurface9* rendered = nullptr;
    IDirect3DSurface9* readback = nullptr;
    require(SUCCEEDED(device->GetRenderTarget(0, &rendered)) &&
      SUCCEEDED(device->CreateOffscreenPlainSurface(640, 480, D3DFMT_X8R8G8B8, D3DPOOL_SYSTEMMEM, &readback, nullptr)) &&
      SUCCEEDED(device->GetRenderTargetData(rendered, readback)), "water output is readable after composition");
    require(SUCCEEDED(readback->LockRect(&locked, nullptr, D3DLOCK_READONLY)), "water output readback locks");
    uint32_t waterPixel = *reinterpret_cast<uint32_t*>(static_cast<char*>(locked.pBits) + 240 * locked.Pitch + 320 * 4);
    const uint32_t red = (waterPixel >> 16) & 255, green = (waterPixel >> 8) & 255, blue = waterPixel & 255;
    std::printf("Path-traced world readback RGB: %u, %u, %u\n", red, green, blue);
    require(!(red > 240 && blue > 240 && green < 5) && green > red * 1.15f && green > blue * 1.15f,
      "world following water is path traced with its slot-3 albedo, without the magenta raster shader");
    uint32_t displacedPixel = *reinterpret_cast<uint32_t*>(static_cast<char*>(locked.pBits) + 320 * locked.Pitch + 160 * 4);
    const uint32_t displacedRed = (displacedPixel >> 16) & 255, displacedGreen = (displacedPixel >> 8) & 255, displacedBlue = displacedPixel & 255;
    std::printf("Captured vertex output RGB: %u, %u, %u\n", displacedRed, displacedGreen, displacedBlue);
    require(displacedGreen > displacedRed * 1.15f && displacedGreen > displacedBlue * 1.15f,
      "path tracing uses vertex-shader output outside the original mesh silhouette");
    readback->UnlockRect(); readback->Release(); rendered->Release();
    }
    require(SUCCEEDED(device->Present(nullptr, nullptr, nullptr, nullptr)), "procedural water frame presents after composition");
    const char* effectVs = "struct O {float4 p:POSITION;float2 uv:TEXCOORD0;float4 c:COLOR0;}; float4 tint:register(c0); O main(float4 p:POSITION) { O o; o.p=float4(p.xy,p.z*0.25,1); o.uv=float2(0.75,0.5); o.c=tint; return o; }";
    const char* effectPs = "sampler2D atlas:register(s0); float4 main(float2 uv:TEXCOORD0,float4 c:COLOR0):COLOR { return float4(1,0,1,1)+0.001*tex2D(atlas,uv)*c; }";
    IDirect3DVertexShader9* effectVertex = nullptr;
    IDirect3DPixelShader9* effectPixel = nullptr;
    require(SUCCEEDED(D3DCompile(effectVs, strlen(effectVs), nullptr, nullptr, nullptr, "main", "vs_3_0", 0, 0, &vsCode, nullptr)) &&
      SUCCEEDED(D3DCompile(effectPs, strlen(effectPs), nullptr, nullptr, nullptr, "main", "ps_3_0", 0, 0, &psCode, nullptr)) &&
      SUCCEEDED(device->CreateVertexShader(static_cast<const DWORD*>(vsCode->GetBufferPointer()), &effectVertex)) &&
      SUCCEEDED(device->CreatePixelShader(static_cast<const DWORD*>(psCode->GetBufferPointer()), &effectPixel)), "particle output-color and atlas shaders compile");
    vsCode->Release(); psCode->Release();
    IDirect3DTexture9* atlas = nullptr;
    require(SUCCEEDED(device->CreateTexture(32, 32, 1, 0, D3DFMT_A8R8G8B8, D3DPOOL_MANAGED, &atlas, nullptr)) &&
      SUCCEEDED(atlas->LockRect(0, &locked, nullptr, 0)), "particle atlas is writable");
    for (int y = 0; y < 32; ++y) {
      auto row = reinterpret_cast<uint32_t*>(static_cast<char*>(locked.pBits) + y * locked.Pitch);
      for (int x = 0; x < 32; ++x) { row[x] = x < 16 ? 0xff00ff00 : 0xffffffff; }
    }
    atlas->UnlockRect(0);
    const Vertex effectVertices[] = { {-1,-1,1,0,0,-1,0,0}, {0,1,1,0,0,-1,0,0}, {1,-1,1,0,0,-1,0,0} };
    struct ColoredVertex { float x, y, z, nx, ny, nz; DWORD color; float u, v; };
    // The world test VS moves its surface to z ~= 0.2 after inverse projection.
    const ColoredVertex fixedParticles[] = {
      {-.12f,-.12f,.12f,0,0,-1,0xff00ffff,.75f,.5f}, {0,.12f,.12f,0,0,-1,0xff00ffff,.75f,.5f}, {.12f,-.12f,.12f,0,0,-1,0xff00ffff,.75f,.5f}
    };
    for (int phase = 0; phase < 4; ++phase) {
      const float tint[4] = {1,0,0,phase == 0 ? 1.0f : 0.0f};
      for (int frame = 0; frame < 16; ++frame) {
        device->SetRenderState(D3DRS_ZENABLE, TRUE); device->SetRenderState(D3DRS_ZWRITEENABLE, TRUE);
        device->SetRenderState(D3DRS_ALPHABLENDENABLE, FALSE);
        device->Clear(0, nullptr, D3DCLEAR_TARGET | D3DCLEAR_ZBUFFER, 0xff203040, 1, 0);
        device->BeginScene();
        device->SetFVF(D3DFVF_XYZ | D3DFVF_NORMAL | D3DFVF_TEX1);
        device->SetVertexShader(vertexShader); device->SetPixelShader(worldShader);
        device->SetTexture(0, nullptr); device->SetTexture(3, worldTexture);
        device->DrawPrimitiveUP(D3DPT_TRIANGLELIST, 1, vertices, sizeof(Vertex));
        device->SetVertexShader(effectVertex); device->SetPixelShader(effectPixel);
        device->SetVertexShaderConstantF(0, tint, 1); device->SetTexture(0, atlas); device->SetTexture(3, nullptr);
        device->SetRenderState(D3DRS_ZWRITEENABLE, FALSE); device->SetRenderState(D3DRS_ALPHABLENDENABLE, TRUE);
        device->SetRenderState(D3DRS_SRCBLEND, D3DBLEND_SRCALPHA); device->SetRenderState(D3DRS_DESTBLEND, D3DBLEND_ONE);
        if (phase < 2) {
          require(SUCCEEDED(device->DrawPrimitiveUP(D3DPT_TRIANGLELIST, 1, effectVertices, sizeof(Vertex))), "particle geometry stays in the traced scene");
        } else {
          device->SetVertexShader(nullptr); device->SetPixelShader(nullptr);
          device->SetFVF(D3DFVF_XYZ | D3DFVF_NORMAL | D3DFVF_DIFFUSE | D3DFVF_TEX1);
          device->SetRenderState(D3DRS_COLORVERTEX, TRUE);
          device->SetTexture(1, atlas);
          device->SetTextureStageState(0, D3DTSS_COLOROP, D3DTOP_MODULATE);
          device->SetTextureStageState(0, D3DTSS_COLORARG1, D3DTA_TEXTURE);
          device->SetTextureStageState(0, D3DTSS_COLORARG2, D3DTA_DIFFUSE);
          device->SetTextureStageState(0, D3DTSS_ALPHAOP, D3DTOP_MODULATE);
          device->SetTextureStageState(0, D3DTSS_ALPHAARG1, D3DTA_TEXTURE);
          device->SetTextureStageState(0, D3DTSS_ALPHAARG2, D3DTA_DIFFUSE);
          device->SetTextureStageState(1, D3DTSS_CONSTANT, phase == 2 ? 0xffff00ff : 0x00ff00ff);
          device->SetTextureStageState(1, D3DTSS_COLOROP, D3DTOP_MODULATE);
          device->SetTextureStageState(1, D3DTSS_COLORARG1, D3DTA_CURRENT);
          device->SetTextureStageState(1, D3DTSS_COLORARG2, D3DTA_CONSTANT);
          device->SetTextureStageState(1, D3DTSS_ALPHAOP, D3DTOP_MODULATE);
          device->SetTextureStageState(1, D3DTSS_ALPHAARG1, D3DTA_CURRENT);
          device->SetTextureStageState(1, D3DTSS_ALPHAARG2, D3DTA_CONSTANT);
          device->SetTextureStageState(2, D3DTSS_COLOROP, D3DTOP_DISABLE);
          require(SUCCEEDED(device->DrawPrimitiveUP(D3DPT_TRIANGLELIST, 1, fixedParticles, sizeof(ColoredVertex))), "fixed-function particle geometry enters the traced scene");
          device->SetTexture(1, nullptr);
          device->SetTextureStageState(1, D3DTSS_COLOROP, D3DTOP_DISABLE);
        }
        device->EndScene();
        if (frame < 15) { device->Present(nullptr, nullptr, nullptr, nullptr); pump(); Sleep(40); }
      }
      if (argc > 1 && std::strcmp(argv[1], "--readback") == 0) {
        IDirect3DSurface9* rendered = nullptr;
        IDirect3DSurface9* readback = nullptr;
        require(SUCCEEDED(device->GetRenderTarget(0, &rendered)) &&
          SUCCEEDED(device->CreateOffscreenPlainSurface(640, 480, D3DFMT_X8R8G8B8, D3DPOOL_SYSTEMMEM, &readback, nullptr)) &&
          SUCCEEDED(device->GetRenderTargetData(rendered, readback)) && SUCCEEDED(readback->LockRect(&locked, nullptr, D3DLOCK_READONLY)), "particle frame readback");
        const uint32_t pixel = *reinterpret_cast<uint32_t*>(static_cast<char*>(locked.pBits) + 240 * locked.Pitch + 320 * 4);
        const uint32_t r = (pixel >> 16) & 255, g = (pixel >> 8) & 255, b = pixel & 255;
        std::printf("Particle phase %d RGB: %u, %u, %u\n", phase, r, g, b);
        require(phase == 0 ? (r > g * 1.3f && r > b * 1.3f) : phase == 2 ? (b > r * 1.3f && b > g * 1.3f) : (g > r * 1.15f && g > b * 1.15f),
          phase == 0 ? "traced particle uses vertex-shader color and animated atlas UV, not the magenta pixel shader" :
          phase == 2 ? "fixed-function particle multiplies texture, vertex color and material tint" :
          phase == 3 ? "zero material opacity reveals the traced world" : "zero particle opacity reveals the traced world");
        readback->UnlockRect(); readback->Release(); rendered->Release();
      }
      device->Present(nullptr, nullptr, nullptr, nullptr);
    }
    device->SetTexture(0, nullptr); atlas->Release(); effectVertex->Release(); effectPixel->Release();
    device->SetVertexShader(nullptr); device->SetPixelShader(nullptr);
    device->SetTexture(3, nullptr); worldShader->Release(); worldTexture->Release();
    vertexShader->Release(); pixelShader->Release();
    device->SetTexture(0, nullptr);
    texture->Release();
    cube->Release();
    device->Release();
    d3d->Release();
    DestroyWindow(window);
    std::puts("PASS: legacy graphics integration completed without a Unity game.");
    return 0;
  } catch (const std::exception& error) {
    std::fprintf(stderr, "FAIL: %s\n", error.what());
    return 1;
  }
}
