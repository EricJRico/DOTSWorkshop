#ifndef WORKSHOP_RENDERER_USER_VALUE
#define WORKSHOP_RENDERER_USER_VALUE

// Decodes the 32-bit Renderer Shader User Value (Unity 6.3+, SRP only) set from C# with
// MeshRenderer.SetShaderUserValue. Layout: 0x00RRGGBB, matching SwarmColorizer.
void UnpackUserValue_float(out float3 Color)
{
    uint v = unity_RendererUserValue;
    Color = float3((v >> 16) & 255, (v >> 8) & 255, v & 255) / 255.0;
}

#endif
