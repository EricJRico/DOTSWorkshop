// Draws the whole swarm with one indirect call. Per-instance data is a StructuredBuffer of
// float4(x, z, speed, unused) uploaded straight from the solver's NativeArray, so there is no
// managed Matrix4x4[] copy, no 1023-instance chunking, and no MaterialPropertyBlock per chunk.
Shader "Workshop/BoidInstanced"
{
    Properties
    {
        _Scale("Scale", Float) = 0.3
        _MaxSpeed("Max Speed", Float) = 2.5
        _ColorSlow("Slow", Color) = (0.2, 0.4, 1, 1)
        _ColorMid("Mid", Color) = (1, 0.9, 0.2, 1)
        _ColorFast("Fast", Color) = (0.95, 0.15, 0.15, 1)
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            StructuredBuffer<float4> _Agents;

            // Deliberately NOT in a UnityPerMaterial CBUFFER: these are pushed through a
            // MaterialPropertyBlock alongside _Agents, and the SRP Batcher path is off anyway
            // because RenderMeshIndirect is driving this with an MPB.
            float _Scale;
            float _MaxSpeed;
            float4 _ColorSlow;
            float4 _ColorMid;
            float4 _ColorFast;

            struct Attributes
            {
                float3 positionOS : POSITION;
                float3 normalOS   : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS   : TEXCOORD0;
                float3 color      : TEXCOORD1;
            };

            Varyings vert(Attributes IN, uint instanceID : SV_InstanceID)
            {
                float4 agent = _Agents[instanceID];

                float3 positionWS = float3(agent.x, 0.0, agent.y) + IN.positionOS * _Scale;

                Varyings OUT;
                OUT.positionCS = TransformWorldToHClip(positionWS);
                OUT.normalWS = IN.normalOS;

                float t = saturate(agent.z / max(_MaxSpeed, 1e-4));
                OUT.color = t < 0.5
                    ? lerp(_ColorSlow.rgb, _ColorMid.rgb, t * 2.0)
                    : lerp(_ColorMid.rgb, _ColorFast.rgb, (t - 0.5) * 2.0);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                // Fixed key direction rather than the scene light: the swarm is a top-down
                // readout of speed, and it must not go black if the scene light changes.
                float3 key = normalize(float3(0.3, 0.9, 0.25));
                float ndotl = saturate(dot(normalize(IN.normalWS), key));
                return half4(IN.color * (0.55 + 0.45 * ndotl), 1.0);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
