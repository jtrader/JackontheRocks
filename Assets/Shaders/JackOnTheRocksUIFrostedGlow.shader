Shader "Jack On The Rocks/UI/Frosted Glow"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _GlowColor ("Glow Color", Color) = (0.502,0.871,0.918,1)
        _GlowIntensity ("Glow Intensity", Range(0,4)) = 0
        _GlowSize ("Glow Size", Range(0.001,0.03)) = 0.008
        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255
        _ColorMask ("Color Mask", Float) = 15
        [Toggle(UNITY_UI_ALPHACLIP)] _UseUIAlphaClip ("Use Alpha Clip", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
            "RenderType" = "Transparent"
            "PreviewType" = "Plane"
            "CanUseSpriteAtlas" = "True"
        }

        Stencil
        {
            Ref [_Stencil]
            Comp [_StencilComp]
            Pass [_StencilOp]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
        }

        Cull Off
        Lighting Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha
        ColorMask [_ColorMask]

        Pass
        {
            Name "FrostedGlow"

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 2.0
            #pragma multi_compile_local _ UNITY_UI_CLIP_RECT
            #pragma multi_compile_local _ UNITY_UI_ALPHACLIP

            #include "UnityCG.cginc"
            #include "UnityUI.cginc"

            struct appdata_t
            {
                float4 vertex : POSITION;
                fixed4 color : COLOR;
                float2 texcoord : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                fixed4 color : COLOR;
                float2 texcoord : TEXCOORD0;
                float4 worldPosition : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            sampler2D _MainTex;
            fixed4 _TextureSampleAdd;
            fixed4 _Color;
            fixed4 _GlowColor;
            float4 _ClipRect;
            float _GlowIntensity;
            float _GlowSize;

            v2f vert(appdata_t input)
            {
                v2f output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.worldPosition = input.vertex;
                output.vertex = UnityObjectToClipPos(input.vertex);
                output.texcoord = input.texcoord;
                output.color = input.color * _Color;
                return output;
            }

            fixed4 frag(v2f input) : SV_Target
            {
                float2 uv = input.texcoord;
                float2 horizontal = float2(_GlowSize, 0.0);
                float2 vertical = float2(0.0, _GlowSize);
                float2 diagonalA = float2(_GlowSize, _GlowSize) * 0.7071;
                float2 diagonalB = float2(_GlowSize, -_GlowSize) * 0.7071;

                fixed4 center = (tex2D(_MainTex, uv) + _TextureSampleAdd) * input.color;
                fixed neighbourAlpha = 0.0;
                neighbourAlpha = max(neighbourAlpha, tex2D(_MainTex, uv + horizontal).a);
                neighbourAlpha = max(neighbourAlpha, tex2D(_MainTex, uv - horizontal).a);
                neighbourAlpha = max(neighbourAlpha, tex2D(_MainTex, uv + vertical).a);
                neighbourAlpha = max(neighbourAlpha, tex2D(_MainTex, uv - vertical).a);
                neighbourAlpha = max(neighbourAlpha, tex2D(_MainTex, uv + diagonalA).a);
                neighbourAlpha = max(neighbourAlpha, tex2D(_MainTex, uv - diagonalA).a);
                neighbourAlpha = max(neighbourAlpha, tex2D(_MainTex, uv + diagonalB).a);
                neighbourAlpha = max(neighbourAlpha, tex2D(_MainTex, uv - diagonalB).a);

                fixed outerGlow = saturate(neighbourAlpha - center.a);
                fixed frost = 0.86 + 0.14 * frac(sin(dot(uv * 512.0, float2(12.9898, 78.233))) * 43758.5453);
                fixed glowAlpha = outerGlow * saturate(_GlowIntensity) * _GlowColor.a * frost;

                fixed4 color;
                color.rgb = lerp(_GlowColor.rgb, center.rgb, center.a);
                color.a = saturate(center.a + glowAlpha);

                #ifdef UNITY_UI_CLIP_RECT
                color.a *= UnityGet2DClipping(input.worldPosition.xy, _ClipRect);
                #endif

                #ifdef UNITY_UI_ALPHACLIP
                clip(color.a - 0.001);
                #endif

                return color;
            }
            ENDCG
        }
    }

    FallBack "UI/Default"
}
